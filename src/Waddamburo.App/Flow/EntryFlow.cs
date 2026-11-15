using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// The entry (player join): plays its jingle; the movie itself requests Song Select, which the shell
/// applies once the last voice has finished.
/// </summary>
internal sealed class EntryFlow(GameShell shell) : FlowScene(shell)
{
    private readonly string? _jingle = shell.Options.JinglePath ?? shell.FindJingle("JINGLE_ENTRY.nub");
    private AudioPlaybackHandle? _bgm;

    public override ScreenKind ScreenFor(SceneId scene) => ScreenKind.Entry;

    public override void Enter(SceneId scene)
    {
        // Song Select comes next: its ~1,400 textures decode and upload in the background meanwhile
        // (loading it at the switch froze the game ~0.7 s). Its movies are the same for either side.
        Shell.Scenes.Prefetch(FlowScenes.SongSelectScene([0]));
        if (_jingle is null || Shell.Audio is not { } audio)
            return;
        // --play-jingle plays it once (a sync check); the scene itself loops it.
        _bgm = Shell.Options.JinglePath is null ? audio.PlayLoop(_jingle, AudioBus.Bgm) : audio.PlayOneShot(_jingle, AudioBus.Bgm);
    }

    public override void Exit(SceneId scene)
    {
        if (_bgm is { } bgm)
            Shell.Audio?.Mixer.Stop(bgm, TimeSpan.FromMilliseconds(20));
        _bgm = null;
    }

    private LumenPlayer[] _asking = [];

    // The entry's requests (WdNames scope entry), for our entry movie; the game's decides through SetNextScene.
    private string? startMode(LumenHostValue[] arguments) => arguments is [{ Kind: LumenHostValueKind.Text } mode]
        ? mode.AsString() switch
        {
            "dan" => Shell.StartDanDojo(),
            "normal" => Shell.Coordinator.Flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)) ? null : "the entry is not the active screen",
            var other => $"there is no mode '{other}' (normal, dan)",
        }
        : "session.start takes the mode's name";

    // Our entry movie (it reads the provider): set up once the screen is ready, as the game's asks with InitInfo.
    private LumenPlayer? _ours;
    private bool _ready;
    private const int EntrySeconds = 50; // the game's entry asks StartTimer(50)

    // player.join(drum): what the game's entry does inside EntryCoin, and on its first join.
    private string? join(LumenHostValue[] arguments)
    {
        if (arguments is not [{ Kind: LumenHostValueKind.Number } drum] || drum.AsNumber() is not (1 or 2))
            return "player.join takes the drum's number, 1 or 2";
        if (Shell.Hosts.EntrySession is not { } session || !_ready)
            return "the entry is not ready yet";
        return join(session, (int)drum.AsNumber() - 1);
    }

    private static string? join(EntrySession session, int side)
    {
        if (session.IsJoined(side))
            return "that drum is already in";
        var first = session.JoinedCount == 0;
        if (!session.TryJoin(side))
            return "that drum cannot join now (credits, or the setup has the drums)";
        // A card paired from the website waits for a drum: the one that joins takes it.
        session.ClaimCard(side);
        if (first)
        {
            session.StartCountdown(EntrySeconds);
            session.ResumeCountdown();
        }
        return null;
    }

    public override void Advance(LumenInputSnapshot input)
    {
        // Each showing of the entry has its own movies: they ask this screen.
        var movies = Shell.Active.Player.Layers.Select(static layer => layer.Player).ToArray();
        if (!movies.SequenceEqual(_asking))
        {
            new WdRequests("entry").Handle("session.start", startMode).Handle("player.join", join).Install(movies);
            _asking = movies;
        }
        tellOurs();
        Shell.Active.Player.Advance(input);
        Shell.Hosts.AdvanceEntry();
        Shell.DonRenderer?.Advance();
    }

    private void tellOurs()
    {
        if (Shell.Hosts.Entry is not { Movie: { UsesProvider: true } movie } host)
            return;
        if (movie != _ours)
            (_ours, _ready) = (movie, false);
        if (!_ready && host.PollReady())
        {
            _ready = true;
            host.InitInfo();
            // The drum that brought the players here joins at once (the attract's trigger: 0 the left drum, 1 the
            // right, 2 both; 3 a coin and 4 none join nobody).
            var trigger = Shell.Credit.EntryTrigger;
            if (trigger is 0 or 2) join(host.Session, 0);
            if (trigger is 1 or 2) join(host.Session, 1);
        }
        for (var side = 0; side < 2; side++)
            movie.Publish("player.joined", LumenHostValue.FromBoolean(host.Session.IsJoined(side)), side + 1);
        movie.Publish("entry.timeLeft", host.Session.TimeLeft is { } left ? LumenHostValue.FromNumber(left) : LumenHostValue.Undefined);
        movie.Publish("entry.setup", LumenHostValue.FromBoolean(host.Session.SetupMode));
    }
}
