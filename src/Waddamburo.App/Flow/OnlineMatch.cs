using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scores;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Flow;

/// <summary>
/// A song played with the room's other player. Whoever's turn it is picks a song and course and waits (<see cref="MyTurn"/>:
/// the host's room setting, <see cref="Choice"/>, says whose); the other's Song Select comes to that song and they pick
/// their course; then both games load it with the other player's chart, options and the host's seed
/// on the second lane, wait for each other under the rainbow, and start together. Each player keeps their own lane's play, as any.
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
    private SongKey? _chooserSong; // this player plays their own copy of the chooser's song, under this key on the chooser's side
    private Pick? _awaiting; // receiving the chooser's song for this pick
    private Task<byte[]?>? _packing; // packing this player's song for the other
    private Task<SongPackage?>? _unpacking; // reading the song that arrived, off the game's thread
    private (SongKey Song, string Title)? _title; // the last song named on the banner (looked up once, not every frame)
    private bool _sending;
    private OnlineSession? _session; // the room the turns below are for
    private ScoreProfile? _partner; // the guest they're for
    private bool _guestsTurn; // taking turns: the guest picks next
    private bool _guestChooses; // as the host last said (a guest's copy)
    private bool? _sentTurn; // what the host last told the guest

    /// <summary>Who picks the songs in this room (the host's setting; Quick Play takes turns).</summary>
    public SongChoice Choice { get; set; }

    /// <summary>Connected, and this player picks the next song.</summary>
    public bool MyTurn => connected() is { } session && (session.IsHost ? !guestChooses : _guestChooses);

    /// <summary>
    /// Connected, and the other player is choosing: Song Select isn't this player's to browse (the lobby stays over it)
    /// until their song comes.
    /// </summary>
    public bool WaitingForTheirPick => connected() is not null && !MyTurn && !InSong && _theirs is null;

    private bool guestChooses => Choice == SongChoice.Guest || Choice == SongChoice.Turns && _guestsTurn;

    /// <summary>A song with the other player is loading or being played.</summary>
    public bool InSong { get; private set; }

    /// <summary>The other player's own result of the last song together, as their game counted it (null until it arrives).</summary>
    public TaikoPlayResult? PartnerResult { get; private set; }

    /// <summary>
    /// This player's notes are over: their result goes to the other player, whose results screen shows it as it is,
    /// rather than as their copy of this lane counted it.
    /// </summary>
    public void SendResult(TaikoPlayResult result)
    {
        if (connected() is not { } session)
            return;
        session.Messages.Send(writer =>
        {
            writer.WriteString("kind", "result");
            writer.WriteNumber("score", result.Score);
            writer.WriteNumber("great", result.Great);
            writer.WriteNumber("good", result.Good);
            writer.WriteNumber("miss", result.Miss);
            writer.WriteNumber("combo", result.MaxCombo);
            writer.WriteNumber("rolls", result.Rolls);
            writer.WriteNumber("gauge", result.GaugeSegments);
            writer.WriteBoolean("cleared", result.Cleared);
        });
    }

    /// <summary>The song's results, the other player's lane as their own game counted it when that has arrived.</summary>
    public IReadOnlyList<TaikoPlayResult> WithPartnerResult(IReadOnlyList<TaikoPlayResult> results)
    {
        var lane = shell.Gameplay.RemoteLaneIndex;
        if (PartnerResult is not { } partner || results.Count != 2 || (uint)lane >= 2)
            return results;
        var merged = results.ToArray();
        merged[lane] = merged[lane] with
        {
            Score = partner.Score, Great = partner.Great, Good = partner.Good, Miss = partner.Miss, MaxCombo = partner.MaxCombo,
            Rolls = partner.Rolls, GaugeSegments = partner.GaugeSegments, Cleared = partner.Cleared,
        };
        return merged;
    }

    /// <summary>What the banner says about the song being agreed on; null when there's nothing to say.</summary>
    public string? Status => connected() is not { } session ? null
        : _awaiting is not null ? T("online.receiving", name(session), percent(session.Transfer.ReceiveProgress))
        : _packing is not null || _sending ? T("online.sending", name(session), percent(session.Transfer.SendProgress))
        // Both picked, the song not loading yet: already waiting, so the banner doesn't drop back to the round trip for a moment.
        : InSong && !_go || !InSong && _mine is not null && _theirs is not null ? T("online.waiting_ready")
        : _mine is not null && _theirs is null ? T("online.waiting_pick", name(session)) + " " + T("online.esc_cancel")
        : _mine is null && _theirs is { } theirs && !MyTurn ? T("online.partner_picked", name(session), title(theirs.Song))
        : _mine is null && MyTurn ? T("online.you_pick")
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
        turns(session);
        session.Pump();
        while (session.Messages.TryTake(out var message))
            handle(session, message);
        if (_awaiting is { } awaited && _unpacking is null && session.Transfer.TakeComplete() is { } blob)
            _unpacking = Task.Run(() => SongPackage.Decode(blob, awaited.Song));
        if (_awaiting is { } song && _unpacking is { IsCompleted: true } unpacking)
        {
            _unpacking = null;
            received(session, song, unpacking.IsCompletedSuccessfully ? unpacking.Result : null);
        }
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
        // Not their turn: they play the other's song, and picking another goes back to it.
        if (!MyTurn && (_theirs is not { } chosen || chosen.Song != request.Song))
        {
            shell.Sync.Notices.AddLocal(T("online.pick_host_song", name(session)), NoticeSeverity.Warning);
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
            // Playing their own copy names the chooser's song as the chooser knows it.
            var song = _chooserSong ?? mine.Song;
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
        PartnerResult = null;
        shell.Players.PlayOnlineAsTwoPlayers(true);
        return play;
    }

    /// <summary>The chooser is under the rainbow waiting for the other's pick (Escape gives up: <see cref="Cancel"/>).</summary>
    public bool WaitingForPartner => _mine is not null && !InSong && MyTurn;

    /// <summary>The chooser stops waiting: back to Song Select, still in the room, and the other's song goes too.</summary>
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
        // Their lane runs in time with this player's song (LiveLaneSource): their hits are judged as they arrive.
        var feed = new RemoteLaneFeed { Delay = TimeSpan.Zero };
        var relay = new HitRelay();
        session.Song = (relay, feed);
        gameplay.RemoteLane = feed;
        gameplay.Relay = relay;
        gameplay.RemoteLaneIndex = remoteLane;
        Console.WriteLine($"Online song: the other player's lane runs live ({(session.Link.State == PeerLinkState.Relayed ? "relayed" : "direct")}, round trip {session.Link.RoundTrip?.TotalMilliseconds ?? 0:F0} ms).");
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

    /// <summary>The song with the other player is over: back to choosing, the other player's turn when taking turns.</summary>
    public void SongEnded()
    {
        if (shell.Room.Session is { } session)
        {
            session.Song = null;
            if (InSong && session.IsHost)
                _guestsTurn = !_guestsTurn;
        }
        reset();
    }

    // The host says who picks: again whenever that changes, and to each new guest. A new room starts over.
    private void turns(OnlineSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            (_session, _partner, _guestsTurn, _guestChooses, _sentTurn) = (session, null, false, false, null);
            Choice = session.Room is { IsQuick: true } ? SongChoice.Turns : SongChoice.Host;
        }
        if (connected() is null)
            return;
        if (!ReferenceEquals(session.Partner, _partner))
            (_partner, _guestsTurn, _guestChooses, _sentTurn) = (session.Partner, false, false, null);
        if (!session.IsHost || _sentTurn == guestChooses)
            return;
        var guest = guestChooses;
        _sentTurn = guest;
        session.Messages.Send(writer =>
        {
            writer.WriteString("kind", "turn");
            writer.WriteBoolean("guest", guest);
        });
    }

    private void reset()
    {
        (_mine, _theirs, _theirReady, _go, InSong, _partnerMissing, _readySince, _goAt) = (null, null, false, false, false, false, 0, 0);
        (_chooserSong, _awaiting, _packing, _unpacking, _sending) = (null, null, null, null, false);
        shell.Room.Session?.Transfer.Cancel();
        shell.Hosts.ShowOnlineSong(null);
    }

    private bool readyTimedOut() => _readySince != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_readySince) > ReadyTimeout;

    private void handle(OnlineSession session, JsonElement message)
    {
        switch (RoomMessages.KindOf(message))
        {
            case "pick" when pick(message) is { } theirs:
                if (MyTurn || _mine is not null)
                    _theirs = theirs;
                else if (local(session, theirs) is { } playable)
                {
                    _theirs = playable;
                    comeToSong(playable.Song);
                }
                break;
            // The other player lacks this player's custom song: it goes to them, once this player confirms they may share it.
            case "need" when MyTurn && _mine is { } mine && _packing is null && !_sending:
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
            // They don't have the song: back to Song Select to choose another.
            case "missing" when _mine is not null && !InSong:
                shell.Sync.Notices.AddLocal(T("online.partner_missing", name(session)), NoticeSeverity.Warning);
                reset();
                shell.SongSelectFlow.CancelCover();
                break;
            case "turn" when !session.IsHost && message.TryGetProperty("guest", out var guest) && guest.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _guestChooses = guest.GetBoolean();
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
            case "result" when partnerResult(message) is { } result:
                PartnerResult = result;
                break;
            // They left the room: gone now rather than once the server or the link notices.
            case "bye":
                session.PartnerLeft();
                break;
            case "go":
                _go = true;
                break;
        }
    }

    /// <summary>
    /// The chooser's pick as this player can play it: the same song, or their own copy of a custom one kept elsewhere (found by
    /// its chart). Without one, a custom song is asked for (null until it arrives) and the game's own songs can't be had.
    /// </summary>
    private Pick? local(OnlineSession session, Pick host)
    {
        _chooserSong = null;
        if (shell.FindSong(host.Song) is not null)
            return host;
        var custom = host.Song.Source is SongSourceKind.Tja or SongSourceKind.OsuLazer or SongSourceKind.OsuStable;
        if (custom && host.Chart is { } chart && shell.SongCatalog.Categories.SelectMany(static category => category.Songs)
                .FirstOrDefault(entry => entry.Descriptor.Key.Source == host.Song.Source
                    && entry.Descriptor.Charts.Any(own => own.Key.StableId == chart)) is { } copy)
        {
            _chooserSong = host.Song;
            return host with { Song = copy.Descriptor.Key };
        }
        if (custom)
        {
            _awaiting = host;
            session.Messages.Send(static writer => writer.WriteString("kind", "need"));
            return null;
        }
        shell.Sync.Notices.AddLocal(T("online.song_missing", name(session)), NoticeSeverity.Warning);
        session.Messages.Send(static writer => writer.WriteString("kind", "missing"));
        return null;
    }

    // The host's song arrived: kept in memory for this session, and Song Select opens on it.
    private void received(OnlineSession session, Pick host, SongPackage? package)
    {
        _awaiting = null;
        if (package is null)
        {
            shell.Sync.Notices.AddLocal(T("online.song_broken"), NoticeSeverity.Warning);
            session.Messages.Send(static writer => writer.WriteString("kind", "missing"));
            return;
        }
        shell.ReceivedSongs.Add(package);
        Console.WriteLine($"Online: received {package.Title.Primary} ({package.Audio.Length / 1024} kB of audio).");
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
        var localSlot = shell.Players.JoinedSides.Count > 0 && shell.Players.JoinedSides.Min == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
        var partnerSlot = localSlot == LocalPlayerSlot.PlayerOne ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
        var local = new PlayerChartRequest(localSlot, mine.Chart, mine.ChartAsset, mine.Course) { Options = mine.Options };
        var partner = new PlayerChartRequest(partnerSlot, chart.Key, chart.ChartAsset, theirs.Course)
        {
            Options = TaikoPlayOptions.FromBits(theirs.Options),
        };
        return new PlayRequest(request.CatalogRevision, request.Song, request.AudioAsset,
            localSlot == LocalPlayerSlot.PlayerOne ? [local, partner] : [partner, local]) { Seed = seed };
    }

    // The other player's result: only its numbers are taken (the course and options are this game's own copy's).
    private static TaikoPlayResult? partnerResult(JsonElement message)
    {
        int number(string name) => message.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) && parsed is >= 0 and <= 100_000
            ? parsed : -1;
        if (!message.TryGetProperty("score", out var scoreValue) || !scoreValue.TryGetInt64(out var score) || score is < 0 or > 100_000_000
            || !message.TryGetProperty("cleared", out var clearedValue) || clearedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        int[] counts = [number("great"), number("good"), number("miss"), number("combo"), number("rolls"), number("gauge")];
        return counts.Any(static count => count < 0) ? null
            : new TaikoPlayResult(TaikoCourse.Oni, score, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], clearedValue.GetBoolean());
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

    private string title(SongKey song)
    {
        if (_title is not { } named || named.Song != song)
            _title = named = (song, shell.FindSong(song)?.Descriptor.Title.Primary ?? song.StableId);
        return named.Title;
    }
}

/// <summary>Who picks the songs in a room.</summary>
internal enum SongChoice { Host, Guest, Turns }
