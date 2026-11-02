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
    private sealed record Pick(SongKey Song, TaikoCourse Course, int Options, int Seed);

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    private Pick? _mine;
    private Pick? _theirs;
    private bool _theirReady;
    private bool _partnerMissing;
    private bool _go;
    private long _readySince;

    /// <summary>A song with the other player is loading or being played.</summary>
    public bool InSong { get; private set; }

    /// <summary>What the banner says about the song being agreed on; null when there's nothing to say.</summary>
    public string? Status => connected() is not { } session ? null
        : InSong && !_go ? T("online.waiting_ready")
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
            writer.WriteString("source", mine.Song.Source.ToString());
            writer.WriteString("song", mine.Song.StableId);
            writer.WriteNumber("course", (int)mine.Course);
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
        shell.Hosts.ShowOnlineSong(null);
    }

    private bool readyTimedOut() => _readySince != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_readySince) > ReadyTimeout;

    private void handle(OnlineSession session, JsonElement message)
    {
        switch (RoomMessages.KindOf(message))
        {
            case "pick" when pick(message) is { } theirs:
                _theirs = theirs;
                if (!session.IsHost && _mine is null)
                    comeToSong(theirs.Song);
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

    // The host's song: the guest's Song Select opens on it, or says they don't have it.
    private void comeToSong(SongKey song)
    {
        if (!shell.SongCatalog.Categories.Any(category => category.Songs.Any(entry => entry.Descriptor.Key == song)))
        {
            shell.Sync.Notices.AddLocal(T("online.song_missing"), NoticeSeverity.Warning);
            shell.Room.Session?.Messages.Send(static writer => writer.WriteString("kind", "missing"));
            _theirs = null;
            return;
        }
        // Song Select shows only that song, to pick a course on; the lobby makes way.
        shell.Room.ClosePanel();
        shell.Reload.Start(() => shell.Hosts.ShowOnlineSong(song));
    }

    // The chosen song for both: the lanes in drum order, this player's on their own drum's side.
    private PlayRequest? together(PlayRequest request, Pick theirs, int seed)
    {
        var mine = request.Players[0];
        var song = shell.SongCatalog.Categories.SelectMany(static category => category.Songs)
            .FirstOrDefault(entry => entry.Descriptor.Key == request.Song);
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
        if (!message.TryGetProperty("source", out var source) || !Enum.TryParse<SongSourceKind>(source.GetString(), out var kind)
            || !Enum.IsDefined(kind) || !message.TryGetProperty("song", out var id) || id.GetString() is not { Length: > 0 } stableId
            || !message.TryGetProperty("course", out var course) || !course.TryGetInt32(out var courseIndex) || (uint)courseIndex > 4
            || !message.TryGetProperty("options", out var options) || !options.TryGetInt32(out var bits)
            || !message.TryGetProperty("seed", out var seed) || !seed.TryGetInt32(out var seedValue))
            return null;
        return new Pick(new SongKey(kind, stableId), (TaikoCourse)courseIndex, bits & 0xFFFF, seedValue);
    }

    private OnlineSession? connected() => shell.Room.Session is { Partner: not null, Link.State: PeerLinkState.Direct or PeerLinkState.Relayed } session
        ? session : null;

    private static string name(OnlineSession session) => session.Partner?.DisplayName ?? "";

    private string title(SongKey song) => shell.SongCatalog.Categories.SelectMany(static category => category.Songs)
        .FirstOrDefault(entry => entry.Descriptor.Key == song)?.Descriptor.Title.Primary ?? song.StableId;
}
