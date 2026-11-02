using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Flow;

/// <summary>
/// A song played with the room's other player. The host picks a song and course and waits; the guest's Song Select comes
/// to that song and they pick theirs; then both games load it with the other player's chart, options and the host's seed
/// on the second lane, wait for each other under the rainbow, and start together. Nothing played this way is saved.
/// </summary>
internal sealed class OnlineMatch(GameShell shell)
{
    // Chart: the picked course's chart id, by which a guest finds their own copy of a custom song kept elsewhere.
    private sealed record Pick(SongKey Song, TaikoCourse Course, int Options, int Seed, string? Chart = null);

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    private Pick? _mine;
    private Pick? _theirs;
    private bool _theirReady;
    private bool _partnerMissing;
    private bool _go;
    private long _readySince;
    private SongKey? _hostSong; // the guest plays their own copy of the host's song, under this key on the host's side
    private Pick? _awaiting; // the guest is receiving the host's song for this pick
    private Task<byte[]?>? _packing; // the host is packing their song for the guest
    private bool _sending;

    /// <summary>A song with the other player is loading or being played.</summary>
    public bool InSong { get; private set; }

    /// <summary>What the banner says about the song being agreed on; null when there's nothing to say.</summary>
    public string? Status => connected() is not { } session ? null
        : _awaiting is not null ? T("online.receiving", name(session), percent(session.Transfer.ReceiveProgress))
        : _packing is not null || _sending ? T("online.sending", name(session), percent(session.Transfer.SendProgress))
        // Both picked, the song not loading yet: already waiting, so the banner doesn't drop back to the round trip for a moment.
        : InSong && !_go || !InSong && _mine is not null && _theirs is not null ? T("online.waiting_ready")
        : _mine is not null && _theirs is null ? T("online.waiting_pick", name(session)) + " " + T("online.esc_cancel")
        : _mine is null && _theirs is { } theirs && !session.IsHost ? T("online.partner_picked", name(session), title(theirs.Song))
        : _mine is null && session.IsHost ? T("online.you_pick")
        : _mine is null && _theirs is null ? T("online.host_choosing", name(session))
        : null;

    /// <summary>Each tick: sends what's due, and takes the other player's messages.</summary>
    public void Tick()
    {
        if (shell.Room.Session is not { } session)
        {
            reset();
            return;
        }
        session.Pump();
        while (session.Messages.TryTake(out var message))
            handle(session, message);
        if (_awaiting is { } awaited && session.Transfer.TakeComplete() is { } blob)
            received(session, awaited, blob);
        if (_packing is { IsCompleted: true } packing)
        {
            _packing = null;
            if (packing.IsCompletedSuccessfully && packing.Result is { } package)
            {
                session.Transfer.Send(package);
                _sending = true;
            }
            else
            {
                Console.Error.WriteLine($"Warning ONLINE: the song wasn't sent ({packing.Exception?.GetBaseException().Message ?? "too big"}).");
                shell.Sync.Notices.AddLocal(T("online.not_sent"), NoticeSeverity.Warning);
                unshared(session);
            }
        }
        if (_sending && session.Transfer.SendProgress is null)
            _sending = false;
        if (InSong && !_go && session.IsHost && _readySince != 0 && (_theirReady || readyTimedOut()))
        {
            session.Messages.Send(static writer => writer.WriteString("kind", "go"));
            _goAt = System.Diagnostics.Stopwatch.GetTimestamp()
                + (long)((session.Link.RoundTrip ?? TimeSpan.Zero).TotalSeconds / 2 * System.Diagnostics.Stopwatch.Frequency);
            _go = true;
        }
        else if (InSong && !_go && readyTimedOut())
            _go = true;
    }

    /// <summary>
    /// Song Select has a pick pending: false to refuse it (a guest picking another song than the host's), otherwise it plays
    /// and the other player is told. A pick made outside a connected room plays as it is.
    /// </summary>
    public bool Accept(PlayRequest request)
    {
        if (connected() is not { } session || request.Players.Length != 1 || request.Review is not null || _mine is not null)
            return true;
        var player = request.Players[0];
        // The guest plays the host's song: picking another goes back to it.
        if (!session.IsHost && (_theirs is not { } hostPick || hostPick.Song != request.Song))
        {
            shell.Sync.Notices.AddLocal(T("online.pick_host_song"), NoticeSeverity.Warning);
            shell.PlayRequests.CancelPending();
            shell.Reload.Start(() =>
            {
                if (_theirs is { } host)
                    shell.Hosts.ShowOnlineSong(host.Song);
            });
            return false;
        }
        _mine = new Pick(request.Song, player.Course, player.Options.Bits, session.IsHost ? Random.Shared.Next() : 0);
        var mine = _mine;
        session.Messages.Send(writer =>
        {
            writer.WriteString("kind", "pick");
            // A guest playing their own copy names the host's song as the host knows it.
            var song = session.IsHost ? mine.Song : _hostSong ?? mine.Song;
            writer.WriteString("source", song.Source.ToString());
            writer.WriteString("song", song.StableId);
            writer.WriteNumber("course", (int)mine.Course);
            writer.WriteString("chart", player.Chart.StableId);
            writer.WriteNumber("options", mine.Options);
            writer.WriteNumber("seed", mine.Seed);
        });
        return true;
    }

    /// <summary>
    /// Under the rainbow, before the song loads: the play with the other player on the second lane, or null while their pick
    /// is still to come. Without them (they left, or don't have the song) it plays alone.
    /// </summary>
    public PlayRequest? Together(PlayRequest request)
    {
        if (request.Players.Length != 1 || _mine is null || connected() is not { } session || _partnerMissing)
            return request;
        if (_theirs is not { } theirs || theirs.Song != request.Song)
            return null;
        if (together(request, theirs, session.IsHost ? _mine.Seed : theirs.Seed) is not { } play)
        {
            shell.Sync.Notices.AddLocal(T("online.no_course"), NoticeSeverity.Warning);
            return request;
        }
        shell.PlayRequests.CancelPending();
        shell.PlayRequests.TryRequestPlay(play);
        InSong = true;
        shell.PlayOnlineAsTwoPlayers(true);
        return play;
    }

    /// <summary>The host is under the rainbow waiting for the guest's pick (Escape gives up: <see cref="Cancel"/>).</summary>
    public bool WaitingForPartner => _mine is not null && !InSong && connected() is { IsHost: true };

    /// <summary>The host stops waiting: back to Song Select, still in the room, and the guest's song goes too.</summary>
    public void Cancel()
    {
        shell.Room.Session?.Messages.Send(static writer => writer.WriteString("kind", "cancel"));
        reset();
        shell.SongSelectFlow.CancelCover();
    }

    /// <summary>The song with the other player is under the rainbow: their hits feed lane <paramref name="remoteLane"/>.</summary>
    public void Start(TaikoGameplayPresentation gameplay, int remoteLane)
    {
        if (!InSong || connected() is not { } session)
            return;
        // A little more than a hit takes to arrive, so their notes are judged where they hit them.
        var oneWay = (session.Link.RoundTrip ?? TimeSpan.FromMilliseconds(100)) / 2;
        var feed = new RemoteLaneFeed { Delay = TimeSpan.FromMilliseconds(Math.Clamp(oneWay.TotalMilliseconds + 100, 120, 400)) };
        var relay = new HitRelay();
        session.Song = (relay, feed);
        gameplay.RemoteLane = feed;
        gameplay.Relay = relay;
        gameplay.RemoteLaneIndex = remoteLane;
        Console.WriteLine($"Online song: the other player's lane runs {feed.Delay.TotalMilliseconds:F0} ms behind.");
    }

    /// <summary>
    /// The rainbow could open on the song now: false holds it until both games can. The host's own "go" waits half a round
    /// trip, as long as it takes to reach the guest, so both open together.
    /// </summary>
    public bool MayReveal()
    {
        if (!InSong)
            return true;
        if (_readySince == 0)
        {
            _readySince = System.Diagnostics.Stopwatch.GetTimestamp();
            shell.Room.Session?.Messages.Send(static writer => writer.WriteString("kind", "ready"));
        }
        return _go && System.Diagnostics.Stopwatch.GetTimestamp() >= _goAt;
    }

    private long _goAt;

    /// <summary>The song waits under the rainbow until both games can open it.</summary>
    public bool HoldsReveal => InSong && (!_go || System.Diagnostics.Stopwatch.GetTimestamp() < _goAt);

    /// <summary>The song with the other player is over: back to choosing.</summary>
    public void SongEnded()
    {
        if (shell.Room.Session is { } session)
            session.Song = null;
        reset();
    }

    private void reset()
    {
        (_mine, _theirs, _theirReady, _go, InSong, _partnerMissing, _readySince, _goAt) = (null, null, false, false, false, false, 0, 0);
        (_hostSong, _awaiting, _packing, _sending) = (null, null, null, false);
        shell.Room.Session?.Transfer.Cancel();
        shell.Hosts.ShowOnlineSong(null);
    }

    private bool readyTimedOut() => _readySince != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_readySince) > ReadyTimeout;

    private void handle(OnlineSession session, JsonElement message)
    {
        switch (RoomMessages.KindOf(message))
        {
            case "pick" when pick(message) is { } theirs:
                if (session.IsHost || _mine is not null)
                    _theirs = theirs;
                else if (local(session, theirs) is { } playable)
                {
                    _theirs = playable;
                    comeToSong(playable.Song);
                }
                break;
            // The guest lacks the host's custom song: it goes to them, once the host confirms they may share it.
            case "need" when session.IsHost && _mine is { } mine && _packing is null && !_sending:
                shell.Room.AskShare(share =>
                {
                    if (!share)
                        unshared(session);
                    else if (_mine == mine)
                        _packing = Task.Run(() => pack(mine.Song));
                });
                break;
            case "unshared" when _awaiting is not null:
                shell.Sync.Notices.AddLocal(T("online.not_shared", name(session)), NoticeSeverity.Warning);
                _awaiting = null;
                break;
            // They don't have the song: it plays alone.
            case "missing":
                shell.Sync.Notices.AddLocal(T("online.partner_missing", name(session)), NoticeSeverity.Warning);
                _partnerMissing = true;
                break;
            // The host stopped waiting: back to the usual list (unless the song is already on its way).
            case "cancel" when !InSong:
                shell.Sync.Notices.AddLocal(T("online.host_cancelled", name(session)), NoticeSeverity.Info);
                if (_mine is not null)
                    shell.SongSelectFlow.CancelCover();
                reset();
                shell.Reload.Start(() => shell.Hosts.ShowOnlineSong(null));
                break;
            case "ready":
                _theirReady = true;
                break;
            case "go":
                _go = true;
                break;
        }
    }

    /// <summary>
    /// The host's pick as this guest can play it: the same song, or their own copy of a custom one kept elsewhere (found by
    /// its chart). Without one, a custom song is asked for (null until it arrives) and the game's own songs can't be had.
    /// </summary>
    private Pick? local(OnlineSession session, Pick host)
    {
        _hostSong = null;
        if (shell.FindSong(host.Song) is not null)
            return host;
        var custom = host.Song.Source is SongSourceKind.Tja or SongSourceKind.OsuLazer;
        if (custom && host.Chart is { } chart && shell.SongCatalog.Categories.SelectMany(static category => category.Songs)
                .FirstOrDefault(entry => entry.Descriptor.Key.Source == host.Song.Source
                    && entry.Descriptor.Charts.Any(own => own.Key.StableId == chart)) is { } copy)
        {
            _hostSong = host.Song;
            return host with { Song = copy.Descriptor.Key };
        }
        if (custom)
        {
            _awaiting = host;
            session.Messages.Send(static writer => writer.WriteString("kind", "need"));
            return null;
        }
        shell.Sync.Notices.AddLocal(T("online.song_missing"), NoticeSeverity.Warning);
        session.Messages.Send(static writer => writer.WriteString("kind", "missing"));
        return null;
    }

    // The host's song arrived: kept in memory for this session, and Song Select opens on it.
    private void received(OnlineSession session, Pick host, byte[] blob)
    {
        _awaiting = null;
        if (SongPackage.Decode(blob, host.Song) is not { } package)
        {
            shell.Sync.Notices.AddLocal(T("online.song_broken"), NoticeSeverity.Warning);
            session.Messages.Send(static writer => writer.WriteString("kind", "missing"));
            return;
        }
        shell.ReceivedSongs.Add(package);
        Console.WriteLine($"Online: received {package.Title.Primary} ({blob.Length / 1024} kB).");
        _theirs = host;
        comeToSong(host.Song);
    }

    // The host's custom song, as it goes to the guest; null when it can't (too big, or unreadable).
    private async Task<byte[]?> pack(SongKey song)
    {
        if (shell.FindSong(song)?.Descriptor is not { } descriptor
            || await SongPackage.FromAsync(descriptor, shell.Assets, shell.Assets).ConfigureAwait(false) is not { } package)
            return null;
        var bytes = package.Encode();
        return bytes.Length <= SongTransfer.MaxBytes ? bytes : null;
    }

    // The song isn't going to the guest: they're told, and the host plays alone.
    private void unshared(OnlineSession session)
    {
        _partnerMissing = true;
        session.Messages.Send(static writer => writer.WriteString("kind", "unshared"));
    }

    private static int percent(double? progress) => (int)Math.Floor((progress ?? 0) * 100);

    // The host's song: the guest's Song Select opens on it.
    private void comeToSong(SongKey song)
    {
        // Song Select shows only that song, to pick a course on; the lobby makes way.
        shell.Room.ClosePanel();
        shell.Reload.Start(() => shell.Hosts.ShowOnlineSong(song));
    }

    // The chosen song for both: the lanes in drum order, this player's on their own drum's side.
    private PlayRequest? together(PlayRequest request, Pick theirs, int seed)
    {
        var mine = request.Players[0];
        var song = shell.FindSong(request.Song);
        if (song?.Descriptor.Charts.FirstOrDefault(chart => chart.Course == theirs.Course) is not { } chart)
            return null;
        var localSlot = shell.JoinedSides.Count > 0 && shell.JoinedSides.Min == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
        var partnerSlot = localSlot == LocalPlayerSlot.PlayerOne ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
        var local = new PlayerChartRequest(localSlot, mine.Chart, mine.ChartAsset, mine.Course) { Options = mine.Options };
        var partner = new PlayerChartRequest(partnerSlot, chart.Key, chart.ChartAsset, theirs.Course)
        {
            Options = TaikoPlayOptions.FromBits(theirs.Options),
        };
        return new PlayRequest(request.CatalogRevision, request.Song, request.AudioAsset,
            localSlot == LocalPlayerSlot.PlayerOne ? [local, partner] : [partner, local]) { Seed = seed };
    }

    // A pick from the other player: anything in it may be nonsense.
    private static Pick? pick(JsonElement message)
    {
        if (!message.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
            || !Enum.TryParse<SongSourceKind>(source.GetString(), out var kind) || !Enum.IsDefined(kind)
            || !message.TryGetProperty("song", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } stableId
            || !message.TryGetProperty("course", out var course) || !course.TryGetInt32(out var courseIndex) || (uint)courseIndex > 4
            || !message.TryGetProperty("options", out var options) || !options.TryGetInt32(out var bits)
            || !message.TryGetProperty("seed", out var seed) || !seed.TryGetInt32(out var seedValue))
            return null;
        var chart = message.TryGetProperty("chart", out var chartId) && chartId.ValueKind == JsonValueKind.String
            && chartId.GetString() is { Length: > 0 and <= 256 } found ? found : null;
        return new Pick(new SongKey(kind, stableId), (TaikoCourse)courseIndex, bits & 0xFFFF, seedValue, chart);
    }

    private OnlineSession? connected() => shell.Room.Session is { Partner: not null, Link.State: PeerLinkState.Direct or PeerLinkState.Relayed } session
        ? session : null;

    private static string name(OnlineSession session) => session.Partner?.DisplayName ?? "";

    private string title(SongKey song) => shell.FindSong(song)?.Descriptor.Title.Primary ?? song.StableId;
}
