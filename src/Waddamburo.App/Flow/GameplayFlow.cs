using Waddamburo.App.Audio;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// A song being played: the rainbow opens on it and the music starts; when chart and music are over,
/// the shutter closes (not in Waiwai) and the results load. Escape abandons the song.
/// </summary>
internal sealed partial class GameplayFlow(GameShell shell) : FlowScene(shell)
{
    private PlayableChart[] _sourceCharts = [];


    private SongSelectSong? _song;

    /// <summary>The song being (or last) played.</summary>
    public SongSelectSong? Song => _song;
    private int _side;
    private WaiwaiComposition? _waiwai;
    public AiBattleSession? AiSession { get; set; }
    private bool _directStart;
    public PlayRequest? CurrentRequest => _play.LastRequest;
    public bool UploadBlocked => _play.UploadProtectedCharts.Count > 0
        || _play.LastRequest?.Players.Any(player => Shell.Assets.UploadBlocked(player.ChartAsset)) == true;

    /// <summary>The song select -> gameplay rainbow, begun by Song Select and finished here.</summary>
    public RainbowTransitionSequence Rainbow { get; private set; } = new();

    /// <summary>The song title shown in the rainbow.</summary>
    public LumenNativeSurfaceKey? RainbowTitle { get; set; }

    public void ResetRainbow() => Rainbow = new RainbowTransitionSequence();

    private bool revealed => _directStart || Rainbow.State is RainbowTransitionState.Revealing or RainbowTransitionState.Complete;

    /// <summary>The next song's charts (one per player) and layout, for <see cref="Enter"/>.</summary>
    /// <summary>Plays a dan course: after <see cref="Prepare"/> with its merged chart.</summary>
    public void PrepareCourse(DanCourseTimeline course, IReadOnlyList<SongSelectSong> songs, DanCourseStatus status)
    {
        _courseStatus = status;
        _course = course;
        _courseSongs = songs;
        _restartCourse = (course, Shell.Gameplay.Dan!);
    }

    // What a restart starts over: the course stays known after its results, which end it.
    private (DanCourseTimeline Timeline, DanCourse Plan)? _restartCourse;

    private DanCourseTimeline? _course;
    private DanCourseStatus? _courseStatus;
    private IReadOnlyList<SongSelectSong> _courseSongs = [];

    public void Prepare(PlayableChart[] charts, SongSelectSong song, int side, WaiwaiComposition? waiwai)
    {
        AiSession = null;
        _course = null;
        _restartCourse = null;
        _directStart = false;
        _sourceCharts = charts;
        _song = song;
        _side = side;
        _waiwai = waiwai;
    }

    public override IndicatorScene IndicatorsFor(SceneId scene) => _demo ? IndicatorScene.Demo : IndicatorScene.Gameplay;

    // The attract loop's demo play (between its CMs): a stock song on autoplay, unsaved, no banner or pause;
    // it fades back to logo_namco after DemoLength, and a drum, coin or card (takeOver) starts a credit.
    private bool _demo;

    /// <summary>The attract loop's demo play is on.</summary>
    public bool Demo => _demo;
    private Func<FlowInput, bool>? _demoTakeOver;
    private static readonly TimeSpan DemoLength = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A demo play's lane for <paramref name="song"/> (its skin, the parts picked by <paramref name="random"/>): prefetched
    /// ahead, then shown. Safe on a worker thread.
    /// </summary>
    public SceneDefinition DemoScene(SongSelectSong song, string category, Random random) =>
        GameplaySceneComposition.Create(FlowScenes.Gameplay, random, Shell.EnsoLayout,
            Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin), 0, twoPlayers: false);

    /// <summary>Starts a demo play of <paramref name="song"/> (its Normal chart) on <paramref name="scene"/>; false when it cannot load.</summary>
    public bool StartDemo(SongSelectSong song, string category, Func<FlowInput, bool> takeOver, SceneDefinition scene)
    {
        var chart = song.Descriptor.Charts.FirstOrDefault(static chart => chart.Course is TaikoCourse.Normal);
        if (chart?.Course is not { } course)
            return false;
        PlayableChart loaded;
        try
        {
            loaded = Shell.Assets.LoadChartAsync(chart.Key, chart.ChartAsset).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidDataException
            or NotSupportedException or IOException or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine($"Demo play of {song.Descriptor.Key} unavailable: {exception.Message}");
            return false;
        }
        var request = new PlayRequest(Shell.SongCatalog.Revision, song.Descriptor.Key, song.Descriptor.AudioAsset,
            [new PlayerChartRequest(LocalPlayerSlot.PlayerOne, chart.Key, chart.ChartAsset, course)]);
        if (!Shell.PlayRequests.TryRequestPlay(request))
            return false;
        // The demo's player is the default Don (red plate, no name), not the last credit's card or account; the
        // next credit resets the players anyway.
        Shell.Players.Reset();
        if (Shell.Sounds?.Gameplay is { } sounds)
        {
            sounds.Waiwai = false;
            sounds.Tones = [0]; // the default 太鼓, not the last player's tone (#19)
            sounds.PrepareDrums(false);
        }
        Shell.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), 1, Final: false);
        Shell.Catalog.Replace(scene);
        Prepare([loaded], song, 0, null);
        _demo = true;
        _demoTakeOver = takeOver;
        _directStart = true;
        Shell.Scenes.Show(FlowScenes.Gameplay);
        // A drum hit during the demo opens Entry: it loads in the background again (the demo's lane took the slot).
        Shell.Scenes.Prefetch(Shell.Catalog.GetDefinition(FlowScenes.Entry));
        Console.WriteLine($"Demo play: {song.Descriptor.Key} ({course}).");
        return true;
    }

    // The demo's bot drums alone: the players' keys only take over (#19).
    private SdlKeyboardSnapshot drums(SdlKeyboardSnapshot keys) => _demo ? new([], null, keys.Timestamp) : keys;

    public override void Exit(SceneId scene)
    {
        Shell.Application.Squeeze43 = false; // menus stay 16:9
        Playing = false;
        PlayingChart = null;
        Presence = null;
        if (!_demo)
            return;
        _demo = false;
        _demoTakeOver = null;
        if (_play.Music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _play.Music = null;
        _play.Clock.Reset();
        _directStart = false;
        _play.Autoplay = null; _play.AiBot = null; _play.Battle = null;
        _play.Charts = [];
        Shell.Gameplay.Stop();
        Shell.PlayRequests.ClearActive();
    }

    /// <summary>A real play is on (not the attract demo or a watched replay), for the website's status.</summary>
    public bool Playing { get; private set; }

    /// <summary>The play's chart hash; null for a dan course, whose songs change along the way.</summary>
    public string? PlayingChart { get; private set; }

    /// <summary>For Discord: the play's song and course ("Senbonzakura · Oni ★10"), or its dan course; since when.</summary>
    public (string What, DateTimeOffset Since)? Presence { get; private set; }

    private static string courseName(TaikoCourse course) => course switch
    {
        TaikoCourse.Ura => "Ura Oni",
        _ => course.ToString(),
    };

    // The drum this game's own player stands at.
    private LocalPlayerSlot localSlot() => Shell.Players.JoinedSides.Count > 0 && Shell.Players.JoinedSides.Min == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;

    public override void Enter(SceneId scene)
    {
        _play.Review?.Dispose();
        _play = new Play();
        var request = Shell.PlayRequests.ActivatePending();
        _play.LastRequest = request;
        _play.CustomUploadsAtStart = Shell.Arcade.UploadCustomSongs;
        _play.UploadProtectedCharts = request.Players.Where(player => Shell.Assets.UploadBlocked(player.ChartAsset))
            .Select(static player => player.Chart).ToHashSet();
        Playing = !_demo && request.Review is null;
        PlayingChart = Playing && _course is null && Shell.Arcade.Home && _sourceCharts.Length > 0
            ? ChartHash.Compute(_sourceCharts[0], request.Players[0].Course) : null;
        Presence = !Playing ? null
            : _course is not null && Shell.Gameplay.Dan is { } dan ? ($"Dan Dojo · {dan.Name}", DateTimeOffset.UtcNow)
            // Custom (TJA) songs stay with their communities: only the official ones and osu! beatmaps are named.
            : _song is { } song ? ($"{(song.Descriptor.Key.Source == SongSourceKind.Tja ? "A custom song"
                    : song.Descriptor.Title.English ?? song.Descriptor.Title.Primary)} · {courseName(request.Players[0].Course)}"
                + (_sourceCharts.ElementAtOrDefault(0)?.Level is { } level ? $" ★{level}" : ""), DateTimeOffset.UtcNow)
            : null;
        // Each player's options on their chart; the random ones from the play's seed (a replay's own).
        _play.Seed = AiSession?.Seed ?? request.Seed ?? Random.Shared.Next();
        // An online lane has no player of its own here: it plays with the other player's options, which an echo copies.
        var players = request.Players.Length < _sourceCharts.Length ? [.. request.Players, request.Players[0]] : request.Players.ToArray();
        // AI battle's support menu: a level below いつもの is the course's chart simplified (both lanes play it).
        var pick = Shell.Hosts.AiBattle && _sourceCharts.Length == 2 && _play.Review is null ? Shell.Hosts.SongSelect?.SupportLevel ?? 0 : 0;
        var offered = Shell.Hosts.SongSelect?.ShownSupportCounts.ElementAtOrDefault((int)players[0].Course) ?? 0;
        var supportLevel = SupportInfo.SupportPick(ChartSimplifier.LevelOf(players[0].Course), offered, pick);
        _play.Intermediate = pick > 0 && ChartSimplifier.SourceCourse(supportLevel) == players[0].Course;
        if (_play.Intermediate)
            Console.WriteLine($"AI battle: level {supportLevel} (pick {pick} of {offered}).");
        // A player's own modchart for their chart (by its hash) goes over the chart's effects, before the options.
        _play.PlayedSources = [.. _sourceCharts.Select(source => _play.Intermediate ? ChartSimplifier.Simplify(source, supportLevel) : source)];
        _play.SupportLevel = _play.Intermediate ? supportLevel : 0;
        if (AiSession is { } setup && (setup.ChartSha256 != ChartHash.Compute(_play.PlayedSources[0], players[0].Course)
            || setup.Course != (int)players[0].Course || setup.Options != players[0].Options.Bits || setup.SupportLevel != _play.SupportLevel))
        {
            AiSession = null;
            _play.Seed = request.Seed ?? Random.Shared.Next();
        }
        _play.Charts = [.. _play.PlayedSources.Select((chart, lane) =>
        {
            var player = players.ElementAtOrDefault(lane);
            var authored = player is null ? chart : PlayerModcharts.Apply(EditorModchartFolder, chart, player.Course);
            // The player's layout where neither gave modchart changes; arcade, the attract's demo play and dan courses
            // (a chart of several songs) are always the stock screen.
            if (player is not null && Shell.Arcade.Home && _waiwai is null && !_demo && chart.Segments.IsEmpty)
            {
                var laid = forLane(authored, PlayerModcharts.ApplyLayout(ActiveLayoutPath, authored), lane);
                if (lane == 0) _play.LayoutShown = authored.Effects.Mods.IsEmpty && !ReferenceEquals(laid, authored); // as the chart's mods
                authored = laid;
            }
            return (player?.Options ?? default).Apply(authored, _play.Seed + (lane < request.Players.Length ? lane : 0));
        })];
        // Online, the kusudama is each player's own: the other player's lane gets balloons in its place.
        if (Shell.Match.InSong && request.Players.ToList().FindIndex(player => player.Player != localSlot()) is var remote and >= 0
            && remote < _play.Charts.Length)
            _play.Charts[remote] = TaikoPlayOptions.KusudamaAsBalloons(_play.Charts[remote]);
        // Two players on different courses have different kusudama (or one only): a shared one would cover the other
        // player's lane, so each plays a balloon of its own instead.
        else if (_play.Charts.Length == 2 && request.Players.Length == 2 && request.Players[0].Course != request.Players[1].Course)
            _play.Charts = [.. _play.Charts.Select(TaikoPlayOptions.KusudamaAsBalloons)];
        Shell.Gameplay.Options = [.. players.Select(static player => player.Options)];
        var active = Shell.Active;
        if (_song is not null)
        {
            showSongTitle(_song);
            showMode(_demo ? Strings.T("mode.demo")
                : Shell.Autoplay && request.Review is null && _course is null ? Strings.T("mode.autoplay") : ModeText(request));
        }
        _play.Timeline = _course is { } course
            ? new GameplayTimeline(course.Songs[0].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn)
            : new GameplayTimeline(_play.Charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
        _play.StartTick = Shell.Tick;
        _play.AudioOffset = TimeSpan.FromMilliseconds(Shell.Arcade.AudioOffsetMs);
        _play.InputOffset = TimeSpan.FromMilliseconds(Shell.Arcade.InputOffsetMs);
        // Home: a recorded play (a replay picked in song select) or the autoplay is reviewed (pause,
        // scrub, speed) rather than played live; it is never saved.
        _play.Review = !Shell.Arcade.Home || Shell.Headless || _course is not null ? null
            : request.Review is { } recorded && request.Players.Length == 1
                ? reviewOf(request, [recorded.Inputs], chartEnd() + TimeSpan.FromSeconds(2), recorded.InputOffset)
            : Shell.Autoplay
                ? reviewOf(request, [.. _play.Charts.Select(GameplayAutoplay.InputsFor)], chartEnd() + TimeSpan.FromSeconds(2), _play.InputOffset)
            : null;
        AutoplayReview = _play.Review is not null && request.Review is null;
        // Reviews show it too: seeking back rebuilds the lanes, which takes a shown banner down.
        Shell.Gameplay.EndBanner = !_demo && !(Shell.Hosts.AiBattle && _play.Charts.Length == 2);
        // A course saves apart, whole (saveCourse).
        _play.Voided = request.Review is not null || _demo || _course is not null || Shell.DevAutoplay;
        if (_play.Review is not null && request.Review?.Player is { } watched)
            Shell.Players.WatchAs(_side, watched);
        // AI battle: the second lane is AI-don's, drummed by the bot on the second drum's keys.
        var aiBattle = Shell.Hosts.AiBattle && _play.Charts.Length == 2 && _course is null;
        _play.Autoplay = (Shell.Autoplay || _demo || Shell.DevAutoplay) && _play.Review is null ? new GameplayAutoplay(aiBattle ? [_play.Charts[0]] : _play.Charts, _side,
            // The demo drums like a person (15 hits a second), not a stress run.
            _demo ? TimeSpan.FromSeconds(1 / 15d) : null) : null;
        _play.Echo = _demo || _play.Review is not null || aiBattle || request.Players.Length == _sourceCharts.Length ? OnlineEcho.Start(null, clear: Shell.Gameplay) : OnlineEcho.Start(Shell.Gameplay);
        if (Shell.Match.InSong)
            Shell.Match.Start(Shell.Gameplay, request.Players.ToList().FindIndex(player => player.Player != localSlot()));
        // A 4:3 layout (camera.aspect below 1): the stage drawn squeezed into a 4:3 picture.
        Shell.Application.Squeeze43 = !Shell.Headless && !Shell.Hosts.AiBattle && _play.Charts.Length > 0 && fourThree(_play.Charts[0].Effects);
        Shell.Gameplay.Start(_play.Charts, active, [.. players.Select(player => player.Course)], _side, _waiwai, _play.Review?.Timelines);
        // The provider's player.name, per lane: the drum's account, or Guest.
        for (var lane = 0; lane < _play.Charts.Length; lane++)
        {
            Shell.Gameplay.Publish("player.tone", LumenHostValue.FromNumber(Shell.Sounds?.Gameplay.Tones.ElementAtOrDefault(lane) ?? 0), lane);
            // ponytail: the パパママ support option is not in the engine; the name is there for a skin to read already.
            Shell.Gameplay.Publish("player.support", LumenHostValue.FromBoolean(false), lane);
        }
        for (var lane = 0; lane < _play.Charts.Length; lane++)
            Shell.Gameplay.Publish("player.name", LumenHostValue.FromString(
                TaikoGuest.Profiles[_play.Charts.Length == 2 ? lane : _side]?.DisplayName ?? Strings.T("setup.guest")), lane);
        var hitOffsets = _play.HitOffsets; // this play's, also for a judgement reported after the next one began
        if (_play.Review is null && _play.Charts.Length > 0)
            Shell.Gameplay.OnJudged(0, judgement =>
            {
                if (!judgement.StrongHitCompleted && !judgement.TimedOut && judgement.Offset is { } offset && judgement.Result is not null)
                    hitOffsets.Add(offset.TotalMilliseconds);
            });
        _play.Battle = aiBattle ? new AiBattlePresentation(active, _play.Charts[0], players[0].Course, Shell.Sounds is { } battleSounds ? battleSounds.Frontend.PlayCue : null) : null;
        if (_play.Battle is { } battle && _play.Review is null)
        {
            // A signed-in player's battle uploads, with or without a server setup (offline): the server replays it, so
            // it plays the frozen online tuning. Local guests may use the installation's.
            _play.AiUploads = AiSession is not null
                || TaikoGuest.Profiles[(int)players[0].Player] is { } signedIn && signedIn.Baid != ScoreProfile.LocalGuestBaid;
            _play.AiPrior = AiSession is { } session ? session.Timing : aiProgress(players[0].Player).Timing;
            var simulation = new AiBattleSimulation(_play.Charts[0], players[0].Course, _play.Seed, _play.AiPrior,
                _play.AiUploads ? AiBattleSimulation.OnlineLevel : Shell.GhostLevels.Count > 0 ? Shell.GhostLevels[0] : null);
            _play.AiBot = new AiDrummer(simulation, () => Shell.Gameplay.Replays[0].Inputs);
            battle.Simulation = simulation;
            Shell.Gameplay.FollowBranch(1, 0);
        }
        if (Shell.Sounds is { } sounds)
            // Per lane: AI-don's lane 1 is padded with the player, so it follows the player's course.
            sounds.Gameplay.RollVoicesOff = [.. players.Select(static player => player.Course >= TaikoCourse.Normal)];
        if (_directStart)
        {
            _play.Music = _play.Review is null ? startAudio(request) : null;
            _play.Clock.Restart();
            showCourseSong();
        }
        Console.WriteLine(
            $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
            + $"{_play.Charts.Sum(static chart => chart.NoteCount)} notes at tick {Shell.Tick}.");
    }

    /// <summary>What a replay or practice says in place of the song's subtitle and number (null: a play).</summary>
    public static string? ModeText(PlayRequest request) =>
        request.Review is { } review ? review.Player is { } player ? Strings.T("mode.replay_of", player.DisplayName) : Strings.T("mode.replay")
        : null;

    // song_info's corner says so after the song started as a play.
    private void showMode(string? text)
    {
        _play.SongInfoMode = text is not null;
        if (songInfoPlayer() is { } info)
            ModeLabel.Show(info, modeLabel(text));
    }

    /// <summary>A replay (one player's, not Waiwai) can turn into practice: P in it.</summary>
    private bool canPractise => _play.Review is not null && _play.Training is null && _play.Charts.Length == 1 && _waiwai is null;

    /// <summary>
    /// A replay becomes practice of the song, paused where it was (Space plays the drum live from there):
    /// the whole song is open again, the player's own offsets and look apply, and nothing is saved.
    /// </summary>
    private void startPractice()
    {
        if (_play.LastRequest is not { } request || _play.Review is not { } replay)
            return;
        var at = replay.Clock.Position;
        replay.Dispose();
        Shell.Players.StopWatching();
        _play.Voided = true;
        _play.Review = reviewOf(request, [[]], chartEnd() + TimeSpan.FromSeconds(2), _play.InputOffset);
        _play.Training = new TrainingSession(Shell, _play.Review, _play.Charts[0]);
        _play.Training.Begin(at);
        showMode(Strings.T("mode.practice"));
        Console.WriteLine($"Practice from {at.TotalSeconds:F1} s.");
    }

    private LumenNativeSurfaceKey? modeLabel(string? text)
    {
        if (text is null)
            return null;
        var key = Shell.Titles.GetGameplayText(text);
        _ = Shell.Titles.Resolve(key);
        return key;
    }

    /// <summary>
    /// The chart position: held at zero under the rainbow, then the music's (or the ticks', headless),
    /// less the audio offset.
    /// </summary>
    private TimeSpan chartTime()
    {
        if (_course is not { } course)
            return songTime();
        // A new song's audio clock starts a little behind where the last one stopped: time holds until it catches up.
        var time = course.Songs[_play.CourseSong].Zero + songTime();
        return time > _play.CourseHold ? time : _play.CourseHold;
    }

    // Traced session31: between's nextsong starts 61 ticks after the rainbow's out (163861 -> 164878 ms); the card
    // started with the reveal, so its doors closed (and their Close sound played) under the rainbow. Reported on Discord.
    private const int CourseCardDelayTicks = 61;

    private TimeSpan songTime() => _play.Review is { } review ? revealed ? review.Clock.Position : review.Clock.Start : -_play.AudioOffset + _play.Timeline?.ChartTime(
        !revealed ? TimeSpan.Zero
        : _play.MusicEndPosition is { } ended ? ended + _play.AfterMusic.Elapsed
        : _play.Music is not null ? Shell.Audio!.GetPosition(_play.Music)
        // A dan course's first card: nothing runs before its music (the stopwatch below is stopped then; ticks are not).
        : Shell.Headless && _play.CourseCardTick is not null ? TimeSpan.Zero
        : Shell.Headless ? TimeSpan.FromSeconds((Shell.Tick - _play.StartTick - _play.PausedTicks
            - (_play.Paused ? Shell.Tick - _play.PausedAtTick : 0)) / 60d)
        : _play.Clock.Elapsed) ?? TimeSpan.Zero;

    /// <summary>Every player's last note, roll, balloon and kusudama is over.</summary>
    public bool ChartOver => _play.Charts.Length != 0 && chartTime() >= chartEnd() + TimeSpan.FromMilliseconds(200);

    private TimeSpan chartEnd() => _play.Charts.Max(static chart =>
        chart.HitObjects.Select(static note => note.StartTime)
            .Concat(chart.LongNotes.Select(static note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max());

    /// <summary>A reviewed play is on screen (home autoplay): its clock is paused, sought and sped up by the keys.</summary>
    public bool Reviewing => _play.Review is not null && Shell.Active.Id == FlowScenes.Gameplay;

    /// <summary>The review is the autoplay (watched like a video: its bar hides until a key is pressed).</summary>
    public bool AutoplayReview { get; private set; }

    // Plays reported from a replay's pause menu this session: each once.
    private readonly HashSet<Guid> _reported = [];

    /// <summary>A downloaded replay of another player, watched by a logged-in one: its pause menu can report it.</summary>
    public bool CanReport => reportTarget() is not null;

    private (Guid Play, ScoreClient Client)? reportTarget() =>
        Reviewing && _play.LastRequest?.Review is { PlayId: { } play, Player: { } watched } && !_reported.Contains(play)
        && Shell.Players.Watcher is { Token: { } token } watcher && watcher.Baid != watched.Baid && Shell.Sync.ClientFor(token) is { } client
            ? (play, client) : null;

    /// <summary>Reports the watched play for <paramref name="reason"/> (a server reason) in the background; a notice shows the server's answer.</summary>
    public void Report(string reason)
    {
        if (reportTarget() is not var (play, client))
            return;
        _reported.Add(play);
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                message = await client.ReportAsync(play, reason).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                message = Strings.T("report.failed", exception.Message);
            }
            Shell.Sync.Notices.AddLocal(message);
        });
    }

    /// <summary>The reviewed play's clock, for the review bar (null when not reviewing).</summary>
    public ReviewClock? Review => _play.Review?.Clock;

    /// <summary>The training under way (null when not training), for the review bar.</summary>
    public TrainingSession? Training => _play.Training;

    public PlayableChart EditorChart => _play.Charts[0];


    /// <summary>
    /// The chart for the modchart editor: the stock screen, without the player's layout (in its place, or under a
    /// modchart), which is not the chart's own effects.
    /// </summary>
    public PlayableChart EditorChartWithoutLayout => _play.Charts[0] with
        { Effects = _play.Charts[0].Effects with { Mods = _play.LayoutShown ? [] : _play.Charts[0].Effects.Mods, Layout = [] } };

    /// <summary>The chart for the layout editor: the layout it edits takes the place of one carried under a modchart.</summary>
    public PlayableChart LayoutEditorChart => _play.Charts[0] with { Effects = _play.Charts[0].Effects with { Layout = [] } };

    public EffectEditorFiles EditorFiles()
    {
        var player = _play.LastRequest!.Players[0];
        string? songPath = null;
        try { songPath = Shell.Assets.ModchartPath(player.ChartAsset); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine($"Song modchart destination unavailable: {e.Message}"); }
        return new(PlayerModcharts.PathFor(EditorModchartFolder, _sourceCharts[0], player.Course), player.Course, songPath);
    }

    public EffectEditorFiles LayoutFiles() => new(ActiveLayoutPath!, _play.LastRequest!.Players[0].Course);

    /// <summary>The layout editor's elements: both lanes' as one (a two-player layout moves them alike).</summary>
    public IReadOnlyList<StageEffectElement> LayoutElements(TimeSpan time) => Shell.Gameplay.EffectElements(time, allLanes: true);

    /// <summary>Two lanes: the song takes the two-player layouts (applied alike to both lanes).</summary>
    public bool TwoPlayerLayouts => _sourceCharts.Length == 2;

    // A two-player layout's own changes for one lane (its *_p2 elements are the second lane's); a chart's own modchart is
    // left as it is.
    private PlayableChart forLane(PlayableChart authored, PlayableChart laid, int lane) => !TwoPlayerLayouts ? laid
        : laid with
        {
            Effects = laid.Effects with
            {
                Mods = authored.Effects.Mods.IsEmpty ? [.. ChartMods.ForLane(laid.Effects.Mods, lane)] : laid.Effects.Mods,
                Layout = [.. ChartMods.ForLane(laid.Effects.Layout, lane)],
            },
        };

    /// <summary>The folder of the layouts for this song's player count.</summary>
    public string LayoutFolder => PlayerLayouts.Folder(EditorModchartFolder, TwoPlayerLayouts);

    /// <summary>The active layout for this song's player count (null: the stock screen).</summary>
    /// <remarks>AI battle always plays the stock screen (its own lanes; no player layout yet).</remarks>
    public string? ActiveLayout => Shell.Hosts.AiBattle ? null : TwoPlayerLayouts ? Shell.Arcade.Layout2P : Shell.Arcade.Layout;

    /// <summary><paramref name="settings"/> with <paramref name="name"/> as the active layout for this song's player count.</summary>
    public ArcadeSettings WithActiveLayout(ArcadeSettings settings, string? name) =>
        TwoPlayerLayouts ? settings with { Layout2P = name } : settings with { Layout = name };

    // The active layout's file; null: the stock screen.
    private string? ActiveLayoutPath => ActiveLayout is { } name ? PlayerLayouts.PathOf(LayoutFolder, name) : null;

    /// <summary>
    /// The active layout changed (picked in the pause menu): the song on screen takes it at once, as a song start would.
    /// </summary>
    public void ReapplyLayout()
    {
        if (_play.LastRequest is not { } request || _play.Charts.Length == 0 || _sourceCharts.Length < _play.Charts.Length) return;
        for (var lane = 0; lane < _play.Charts.Length; lane++)
        {
            var course = request.Players.ElementAtOrDefault(lane)?.Course ?? request.Players[0].Course;
            var own = PlayerModcharts.Apply(EditorModchartFolder, _sourceCharts[lane], course);
            var authored = forLane(own, PlayerModcharts.ApplyLayout(ActiveLayoutPath, own), lane);
            if (lane == 0) _play.LayoutShown = _sourceCharts[0].Effects.Mods.IsEmpty && authored.Effects.Mods.Length > 0;
            _play.Charts[lane] = _play.Charts[lane] with { Effects = authored.Effects };
            if (_play.Review is { } review) review.ReplaceEffects(lane, authored.Effects);
            else Shell.Gameplay.ReplaceEffects(lane, authored.Effects);
        }
        Shell.Application.Squeeze43 = !Shell.Headless && !Shell.Hosts.AiBattle && fourThree(_play.Charts[0].Effects);
    }

    public void ReplaceEditorEffects(ChartEffects effects)
    {
        // Two players (the layout editor): each lane takes its own part of the layout (the second lane's are *_p2).
        if (_play.Charts.Length == 2)
            for (var lane = 0; lane < 2; lane++)
                _play.Review!.ReplaceEffects(lane, _play.Charts[lane].Effects with { Mods = [.. ChartMods.ForLane(effects.Mods, lane)], Layout = [] });
        else
            _play.Review!.ReplaceEffects(0, effects);
        Shell.Application.Squeeze43 = fourThree(effects); // the editor's 4:3 toggle shows at once
    }

    private static bool fourThree(ChartEffects effects) =>
        new Waddamburo.Game.Gameplay.ChartModValues(effects.Mods).Value("camera", "aspect", TimeSpan.Zero) < 1
        || new Waddamburo.Game.Gameplay.ChartModValues(effects.Layout).Value("camera", "aspect", TimeSpan.Zero) < 1;

    public IReadOnlyList<StageEffectElement> EditorElements(TimeSpan time) => Shell.Gameplay.EffectElements(time);

    /// <summary>F8 can turn a local play into an unranked authoring session, without a replay prerequisite.</summary>
    public bool CanEditEffects => CanEditLayout && _play.Charts.Length == 1;

    /// <summary>The layout editor and the Layouts menu: a local song for one or two players (not Waiwai, dan, online).</summary>
    public bool CanEditLayout => Shell.Arcade.Home && !Shell.Headless && revealed && _play.Charts.Length is 1 or 2
        && _waiwai is null && _course is null && !_demo && !Shell.Match.InSong && Shell.Active.Id == FlowScenes.Gameplay;

    /// <remarks><paramref name="autoplay"/>: the layout editor watches the song on autoplay, so every part of the
    /// screen shows up (combo, Go-Go, balloons, the gauge filling) to be placed.</remarks>
    public void BeginEffectsEditing(bool autoplay = false)
    {
        if (!(autoplay ? CanEditLayout : CanEditEffects) || _play.LastRequest is not { } request) return;
        _play.Voided = true; Playing = false; PlayingChart = null; Presence = null;
        if (autoplay)
        {
            var at = _play.Training?.Clock.Position ?? chartTime();
            if (_play.Music is { } music) Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
            _play.Music = null; _play.Autoplay = null; _play.AiBot = null; _play.Battle = null;
            _play.Review?.Dispose();
            Shell.Players.StopWatching();
            _play.Review = reviewOf(request, [.. _play.Charts.Select(GameplayAutoplay.InputsFor)], chartEnd() + TimeSpan.FromSeconds(2), _play.InputOffset);
            _play.Training = new TrainingSession(Shell, _play.Review, _play.Charts[0], watch: true);
            _play.Training.Begin(at);
        }
        else if (_play.Training is null)
        {
            var at = chartTime();
            if (_play.Music is { } music) Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
            _play.Music = null; _play.Autoplay = null; _play.AiBot = null; _play.Battle = null;
            _play.Review?.Dispose();
            Shell.Players.StopWatching();
            _play.Review = reviewOf(request, [[]], chartEnd() + TimeSpan.FromSeconds(2), _play.InputOffset);
            _play.Training = new TrainingSession(Shell, _play.Review, _play.Charts[0]);
            _play.Training.Begin(at);
        }
        showMode("Effects editor - unranked");
        if (_play.Paused) SetPaused(false);
        Console.WriteLine("Effects editor: this play is unranked and will not save a score.");
    }

    /// <summary>
    /// A training attempt is being played (live, not paused): the drum is a drum, not a menu pad. Paused,
    /// a controller is its gamepad layout again (scrub, speed, loop marks).
    /// </summary>
    public bool Attempting => Reviewing && _play.Training is { Live: true, Clock.Paused: false };

    public bool CanPause => revealed && _play.ShutterStartTick < 0 && !_play.Paused && !Shell.Overlay.IsShown && !_demo;


    /// <summary>Closes the shutter at once (home, a played song whose notes are over); false when it cannot.</summary>
    public bool SkipToShutter()
    {
        if (_play.Review is not null || !ChartOver || !CanPause)
            return false;
        _play.SkipToShutter = true;
        return true;
    }
    public bool CanQuickRestart => revealed && _play.ShutterStartTick < 0 && !Shell.Overlay.IsShown && !_demo;

    public void SetPaused(bool paused, float interpolation = 0)
    {
        if (_play.Paused == paused) return;
        if (paused)
        {
            _play.PauseInterpolation = interpolation;
            _play.PausedAtTick = Shell.Tick;
            _play.Clock.Stop();
            _play.AfterMusic.Stop();
        }
        else
        {
            _play.PausedTicks += Shell.Tick - _play.PausedAtTick;
            if (revealed) _play.Clock.Start();
            if (_play.MusicEndPosition is not null) _play.AfterMusic.Start();
        }
        if (_play.Music is { } music && Shell.Audio is { } audio)
            audio.SetPaused(music, paused);
        _play.Paused = paused;
    }

    /// <summary>
    /// Abandons the current round and loads the same match again without saving it. From a replay or
    /// practice it is the song played for real (the drum's own player again).
    /// </summary>
    public bool Restart()
    {
        if (_play.LastRequest is not { } last || _play.Charts.Length == 0) return false;
        var request = last with { Review = null, Seed = null };
        // A server setup binds one play: a second upload under it is refused (422) and blocks the upload queue.
        AiSession = null;
        // Playing a watched replay's chart: with your own options, not the replay's (they stayed on; reported on Discord).
        if (last.Review is not null)
            request = new PlayRequest(last.CatalogRevision, last.Song, last.AudioAsset,
                last.Players.Select(static player => player with { Options = TaikoGuest.Options[(int)player.Player] }));
        Shell.Players.StopWatching();
        if (_play.Paused) SetPaused(false);
        if (_play.Music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle);
        _play.Music = null;
        _play.Clock.Reset();
        Shell.Gameplay.Stop();
        // A course starts over from its first song, also from its results.
        if (_restartCourse is { } again)
        {
            dropNextAudio();
            _course = again.Timeline;
            Shell.Gameplay.Dan = again.Plan;
            Shell.Hosts.DanResult = null;
        }
        Shell.PlayRequests.ClearActive();
        if (!Shell.PlayRequests.TryRequestPlay(request))
            throw new InvalidOperationException("The restarted play request could not be queued.");
        Shell.Coordinator.Flow.CancelPendingTransition();
        Shell.Scenes.CancelFade();
        ResetRainbow();
        _directStart = true;
        Shell.Scenes.Show(FlowScenes.Gameplay);
        return true;
    }

    public void Abandon()
    {
        if (_play.Paused) SetPaused(false);
        end(finished: false);
    }

    /// <summary>The song is dropped where it is (the service menu resets the game): cleaned up, nothing saved, no scene shown.</summary>
    public void Discard()
    {
        if (_play.Paused) SetPaused(false);
        end(finished: false, show: false);
    }

    public override void Advance(LumenInputSnapshot input)
    {
        if (_play.Paused || Shell.FreezeAnimations && Shell.EffectsEditorOpen) return;
        var animationFrames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(animationFrames);
    }

    // Paused or frozen (the editors' Freeze animations), the movies stand on one tick: no blending between ticks.
    private TimeSpan? _frameTime;

    // Diagnostic (WADDAMBURO_HITCH_TRACE=1): a frame whose notes moved further or less than the time since the last
    // frame (the clock jumped).
    private static readonly bool NoteStepTrace = Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";
    private long _noteStepAt;
    private TimeSpan _noteStepTime;

    private void traceNoteStep(TimeSpan time)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_noteStepAt != 0)
        {
            var wall = System.Diagnostics.Stopwatch.GetElapsedTime(_noteStepAt, now).TotalMilliseconds;
            var chart = (time - _noteStepTime).TotalMilliseconds;
            if (Math.Abs(chart - wall) > 1) // long frames: the hitch trace and the recorder's missed refreshes
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"Note step at {time.TotalSeconds:F3} s: chart moved {chart:F1} ms in {wall:F1} ms of frame ({chart - wall:+0.0;-0.0} ms); GC gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused total {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms."));
        }
        (_noteStepAt, _noteStepTime) = (now, time);
    }

    public override LumenRenderSnapshot CreateSnapshot(float interpolation)
    {
        var time = _frameTime ?? chartTime();
        _frameTime = null; // one frame's: a frame without UpdateFrame reads the clock itself
        if (NoteStepTrace && !_play.Paused && revealed)
            traceNoteStep(time);
        if (songInfoPlayer() is { } info)
            TaikoSongInfoPresentation.UpdateVisibility(info,
                showStage: !Shell.HideArcadeHud || _course is not null || _play.SongInfoMode,
                showInfo: _play.Battle?.ResultShownAt is null);
        return Shell.Gameplay.CreateSnapshot(time, _play.Paused ? _play.PauseInterpolation
            : Shell.FreezeAnimations && Shell.EffectsEditorOpen ? 1 : interpolation);
    }

    // Live drum input is judged per display frame (headless, per tick).
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        // The notes are drawn at the chart time of the frame's start, as the movies are blended at the frame start's
        // tick fraction. Read when the frame was built instead, a frame that judged a hit (judgement, score, sounds,
        // AI-don: a few ms on a slow PC) read it later than its neighbours and drew the lane ahead: it jerked forward
        // as the note in front was hit.
        _frameTime = _play.Review is null && revealed && !_play.Paused ? chartTime() : null;
        if (CanEditEffects && !Shell.EffectsEditorOpen
            && keys.Presses.Any(static press => press.Key == SdlKeyboardKey.F8)) Shell.OpenEffectsEditor();
        if (_play.Review is { } review)
        {
            if (revealed && !_play.Paused && _play.Training is { } training)
                training.Frame(keys, keysEnabled: !Shell.HomeMenuOpen);
            else if (revealed && !_play.Paused && !Shell.HomeMenuOpen && canPractise
                && GameActions.Pressed(keys, GameAction.Practise))
                startPractice();
            else if (revealed && !_play.Paused)
                review.Frame(keys, keysEnabled: !Shell.HomeMenuOpen);
            else
                review.Hold();
            return;
        }
        if (!Shell.Headless && revealed && !_play.Paused)
        {
            var time = chartTime() - _play.InputOffset;
            var played = _play.Autoplay?.Apply(drums(keys), time) ?? keys;
            Shell.Gameplay.Advance(played, time, aiInput: _play.AiBot is { } bot ? bot.Apply : null);
            _play.Battle?.Advance(time, Shell.Gameplay.Results);
        }
        _play.Echo?.Pump();
    }

    /// <summary>Home: the song is under way and not already a review, so its pause menu can replay it.</summary>
    public bool CanInstantReplay => Shell.Arcade.Home && _play.Review is null && revealed && _play.Charts.Length == 1 && _waiwai is null
        && Shell.Active.Id == FlowScenes.Gameplay && _course is null;

    /// <summary>A dan course is under way: it cannot be resumed once paused, so a break can't be taken inside a run.</summary>
    public bool InCourse => _course is not null;

    /// <summary>
    /// Turns the song under way into a review of what was played: paused where the player paused, and
    /// it cannot go further; with no drum hit yet, straight into practice from there. The play is void
    /// (never saved); Retry plays the song again.
    /// </summary>
    public void StartInstantReplay()
    {
        if (!CanInstantReplay || _play.LastRequest is not { } request)
            return;
        var at = chartTime();
        if (_play.Music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _play.Music = null;
        _play.Voided = true;
        // Taken before the review: showing it restarts the lanes, with empty replays.
        IReadOnlyList<TaikoReplayInput>[] played = [.. Shell.Gameplay.Replays.Select(static replay => (IReadOnlyList<TaikoReplayInput>)[.. replay.Inputs])];
        var review = reviewOf(request, played, at, _play.InputOffset);
        review.Clock.Seek(at);
        review.Clock.SetPaused(true);
        review.ShowFresh(at);
        _play.Review = review;
        // No note reached yet (hit or missed; drum presses alone judge nothing): there is nothing to
        // watch, so the player lands in practice there.
        // (The bake runs the whole song, so the notes after the pause are in it as misses: only what
        // happened up to the pause counts; events are in judgement time.)
        var judgedUntil = at - _play.InputOffset;
        if (review.Timelines.All(timeline => timeline.LastEventAt(judgedUntil) < 0))
        {
            startPractice();
            SetPaused(false);
            return;
        }
        showMode(Strings.T("mode.replay"));
        SetPaused(false);
        Console.WriteLine($"Instant replay of the first {at.TotalSeconds:F1} s (the play is void).");
    }

    // inputOffset: the one the play was made with (its hits then show where its player saw them).
    private ReviewSession reviewOf(PlayRequest request, IReadOnlyList<TaikoReplayInput>[] inputs, TimeSpan end, TimeSpan inputOffset) =>
        new(Shell, request, _play.Charts, _side, _waiwai, inputs, _play.Timeline!.LeadIn, end, inputOffset,
            songStart: _play.Timeline.AudioStart - _play.Timeline.LeadIn - _play.AudioOffset);

    public override void Tick(FlowInput input)
    {
        if (_play.Paused) return;
        if (_demo)
        {
            // Exit (on the switch either makes) ends the demo. Once it's fading out, a hit waits for the logo: taking over
            // then left the fade's black over entry.
            if (!Shell.Scenes.FadePending && _demoTakeOver?.Invoke(input) == true || Shell.Scenes.FinishFade())
                return;
            // Escape ends the demo like its timer (it abandoned the song into Song Select, #19).
            if (input.Escape || revealed && (chartTime() >= DemoLength || ChartOver))
            {
                if (_play.Music is { } demoMusic)
                    Shell.Audio?.Mixer.Stop(demoMusic.Handle, TimeSpan.FromSeconds(1));
                Shell.Scenes.FadeTo(FlowScenes.Logo);
                return;
            }
        }
        var overlay = Shell.Overlay;
        // Online: both games open the rainbow together, once both could.
        if (overlay.Player is { } rainbow && Rainbow.ShouldStartReveal(Shell.Tick) && Shell.Match.MayReveal())
        {
            var request = Shell.PlayRequests.Active
                ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
            _play.StartTick = Shell.Tick;
            // A dan course's first card (and its music) waits under the opening rainbow, as Green's does; the rest start now.
            if (_course is not null && _play.Review is null)
                _play.CourseCardTick = Shell.Tick + CourseCardDelayTicks;
            else
            {
                _play.Music = _play.Review is null ? startAudio(request) : null; // a review plays its own music
                _play.Clock.Restart();
                showCourseSong();
            }
            rainbow.GotoLabel(_course is null ? RainbowTransitionComposition.RevealLabel
                : RainbowTransitionComposition.PlainRevealLabel, play: true);
            Rainbow.StartReveal();
            Console.WriteLine($"Rainbow reveal and gameplay started at tick {Shell.Tick}.");
            return;
        }
        if (overlay.Player is { } opening && Rainbow.FinishRevealWhenStopped(opening.IsPlaying))
        {
            overlay.Clear();
            Console.WriteLine($"Rainbow reveal completed at tick {Shell.Tick}.");
        }
        if (_play.CourseCardTick is { } cardAt && Shell.Tick >= cardAt)
        {
            _play.CourseCardTick = null;
            _play.Music = startAudio(Shell.PlayRequests.Active!);
            _play.Clock.Restart();
            _play.StartTick = Shell.Tick; // the headless clock (ticks) restarts with it
            showCourseSong();
        }
        if (!revealed || _play.Review is not null) // a review never ends by itself: Escape leaves it
            return;
        var elapsed = chartTime();
        // Traced session31: a requirement that can no longer be met ends the course with the song being played.
        if (_course is { } failing && _play.CourseEndsAt is null && Shell.Gameplay.DanFailed)
        {
            var failed = failing.Songs[_play.CourseSong];
            var banner = failed.Zero + TaikoResultBanner.Time(failed.Chart);
            _play.CourseEndsAt = banner > elapsed ? banner : elapsed;
            Shell.Gameplay.EndCourseAt(_play.CourseEndsAt.Value);
            dropNextAudio();
        }
        if (_course is { } playing && _play.CourseEndsAt is null && _play.CourseSong + 1 < playing.Songs.Count && elapsed >= playing.Songs[_play.CourseSong].End)
        {
            nextCourseSong(Shell.PlayRequests.Active!);
            elapsed = chartTime();
        }
        if (Shell.Headless)
        {
            var played = _play.Autoplay?.Apply(drums(input.Keys), elapsed - _play.InputOffset) ?? input.Keys;
            Shell.Gameplay.Advance(played, elapsed - _play.InputOffset, aiInput: _play.AiBot is { } bot ? bot.Apply : null);
            _play.Battle?.Advance(elapsed - _play.InputOffset, Shell.Gameplay.Results);
            _play.Echo?.Pump();
        }
        if (Shell.Audio?.Failure is { } device)
            throw new IOException("Gameplay audio failed.", device);
        // The song's decoder fell behind (or its file broke): back to Song Select, unsaved, with a notice.
        if (_play.Music?.Failure is { } song)
        {
            Console.Error.WriteLine($"Gameplay audio failed: {song.Message}");
            Shell.Sync.Notices.AddLocal(Strings.T("gameplay.audio_failed"), NoticeSeverity.Warning);
            end(finished: false);
            return;
        }
        // Online: once this player's notes are over, their result goes to the other player as it stands.
        if (Shell.Match.InSong && !_play.ResultSent && ChartOver)
        {
            _play.ResultSent = true;
            var lane = 1 - Shell.Gameplay.RemoteLaneIndex;
            if ((uint)lane < (uint)Shell.Gameplay.Results.Count)
                Shell.Match.SendResult(Shell.Gameplay.Results[lane]);
        }
        // Traced: the shutter closes 9 s after the end banner, by the chart, not the audio (whose end only
        // freezes the chart clock below). Audio running past it fades under the shutter: some songs' files
        // go on long after their chart (Stock:hanakp: silence, then more song; #4).
        var musicFinished = _play.Music is not { } music || Shell.Audio is null || !Shell.Audio.Mixer.IsPlaying(music.Handle);
        if (musicFinished && _play.Music is { } finished && _play.MusicEndPosition is null)
        {
            _play.MusicEndPosition = Shell.Audio!.GetPosition(finished);
            _play.AfterMusic.Restart();
        }
        // AI battle: by its battle result, which comes after the last section (traced, no end banner nor shutter).
        var chartFinished = _play.Battle is { } battle
            ? battle.ResultShownAt is { } shown && elapsed - _play.InputOffset >= shown + AiBattlePresentation.ResultsDelay
            : _play.Charts.Length != 0 && elapsed >= (_play.CourseEndsAt ?? _play.Charts.Max(TaikoResultBanner.Time)) + TaikoResultBanner.ShutterDelay;
        if ((!input.Escape || Shell.Arcade.Home) && _play.ShutterStartTick < 0
            && (_play.SkipToShutter || chartFinished && !Shell.Gameplay.OverlayActive))
        {
            overlay.Clear();
            if (!musicFinished && _play.Music is { } outro)
                Shell.Audio?.Mixer.Stop(outro.Handle, TimeSpan.FromSeconds(1));
            // Traced (5 Waiwai runs, every AI battle): Waiwai and AI battle never close the shutter; their results cut in.
            if (Shell.Gameplay.WaiwaiOutcome is null && _play.Battle is null)
                overlay.Show(_course is null ? FlowScenes.Shutter : FlowScenes.DanShutter);
            // Traced sessions 30 and 31, as the course's shutter closes.
            if (_course is not null && Shell.Sounds is { } sounds)
            {
                sounds.Frontend.PlayCue("VO_RESULT", 19);
                sounds.Frontend.PlayCue("SE_GAME", 15);
            }
            _play.ShutterStartTick = Shell.Tick;
            _play.ShutterClosing = false;
        }
        // Close registers on the shutter's first frame; retry until it exists.
        if (_play.ShutterStartTick >= 0 && !_play.ShutterClosing && overlay.Player is { } shutter)
            // Close(side): the shutter takes the player's colour (traced Close(1) for the right drum's
            // player, Close(2) for two players).
            _play.ShutterClosing = shutter.TryInvokeCallback("Close",
                [LumenHostValue.FromNumber(Shell.Hosts.TwoPlayers ? 2 : Shell.Hosts.PlayerSide)]);
        // ponytail: 70 ticks = traced Close -> results load (1.17 s); the close itself takes ~1 s.
        // (AI battle: no shutter; its delay already ends at the results.)
        if (input.Escape && !Shell.Arcade.Home || _play.ShutterStartTick >= 0 && (Shell.Tick - _play.ShutterStartTick >= 70 || _play.Battle is not null))
        {
            // An abandoned song gives its stage back: Song Select counts it again when the next one starts (#21).
            if (input.Escape && !Shell.Arcade.Home && Shell.Players.SongsPlayed > 0)
            {
                var tutorialSeen = Shell.Hosts.TutorialSeen; // Giving a stage back is not a new credit.
                var waiwaiTutorialSeen = Shell.Hosts.WaiwaiTutorialSeen;
                var aiTutorialSeen = Shell.Hosts.AiTutorialSeen;
                Shell.Players.SongsPlayed--;
                Shell.Hosts.TutorialSeen = tutorialSeen;
                Shell.Hosts.WaiwaiTutorialSeen = waiwaiTutorialSeen;
                Shell.Hosts.AiTutorialSeen = aiTutorialSeen;
            }
            end(finished: !input.Escape || Shell.Arcade.Home);
        }
    }

    // A course's next song: its music, on the course's clock, from under its card.
    private void nextCourseSong(PlayRequest request)
    {
        var course = _course!;
        _play.CourseHold = chartTime();
        if (_play.Music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _play.CourseSong++;
        _play.Timeline = new GameplayTimeline(course.Songs[_play.CourseSong].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn);
        _play.MusicEndPosition = null;
        _play.AfterMusic.Reset();
        _play.Clock.Restart();
        _play.Music = startAudio(request);
        showCourseSong();
    }

    // Traced session30, at each song's start: between's clip nextsong goes to "start" (its sounds are its own),
    // with the song's title, and song_info shows the song's title, genre and number.
    private void showCourseSong()
    {
        if (_course is null)
            return;
        var song = _courseSongs[_play.CourseSong];
        var card = Shell.Titles.GetTransitionTitle(song);
        _ = Shell.Titles.Resolve(card);
        if (sceneLayer("between") is { } between)
        {
            between.SetNativeFill("next_song_name", card);
            if (!between.TryGotoLabel("nextsong", "start"))
                Console.Error.WriteLine("between has no nextsong clip to start.");
        }
        // Each song's own genre, as the select screen shows it (the genre Song Select had set stayed for the whole
        // course; reported on Discord).
        Shell.SongInfo = Shell.SongInfo with
        {
            Genre = Shell.Hosts.Dan?.Genre(song.Descriptor) ?? Shell.SongInfo.Genre,
            Stage = _play.CourseSong + 1,
        };
        // song_info asks LumenSongInfo only as the song starts (main.genre.gotoAndStop(genreArray[GetGenrNumber()]),
        // main.stage.gotoAndStop(GetPlayCount())), so a course's next songs set its clips the same way here.
        if (sceneLayer("song_info") is { } info
            && !(info.TryGotoLabel("main/genre", GenreLabels[Math.Clamp(Shell.SongInfo.Genre, 0, GenreLabels.Length - 1)], play: false)
                && info.TryGotoFrame("main/stage", Shell.SongInfo.StageFrame - 1, play: false)))
            Console.Error.WriteLine("song_info has no main.genre / main.stage clip to set.");
        showSongTitle(song);
    }

    // song_info's genreArray: TaikoSongInfo.Genre's labels.
    private static readonly string[] GenreLabels = ["jpop", "anime", "vocaloid", "doyo", "variety", "classic", "game", "namco"];

    // A movie of the active scene by name (song_info, between); null when the scene has none.
    private LumenPlayer? sceneLayer(string movie)
    {
        var index = Shell.Active.Layers.ToList().FindIndex(entry => Path.GetFileNameWithoutExtension(entry.Definition.MovieId) == movie);
        return index < 0 ? null : Shell.Active.Player.Layers[index].Player;
    }

    // Both movies share the song_name, main/genre and main/stage slots.
    private LumenPlayer? songInfoPlayer() => sceneLayer("song_info") ?? sceneLayer("ai_song_info");

    // song_info's 720x64 title slot: fixed height, right-aligned, squeezed to fit.
    private void showSongTitle(SongSelectSong song)
    {
        Shell.Gameplay.Publish("song.title", LumenHostValue.FromString(song.Descriptor.Title.English ?? song.Descriptor.Title.Primary));
        Shell.Gameplay.Publish("song.subtitle", song.Descriptor.Subtitle is { Length: > 0 } subtitle
            ? LumenHostValue.FromString(subtitle) : LumenHostValue.Undefined);
        if (songInfoPlayer() is not { } info)
            return;
        var title = Shell.Titles.GetGameplayTitle(song);
        _ = Shell.Titles.Resolve(title);
        TaikoSongInfoPresentation.SetTitle(info, title);
    }

    // A finished song shows its results (the shutter stays over them until their first frames are
    // drawn); Escape goes straight back to Song Select.
    private void end(bool finished, bool show = true)
    {
        _play.ShutterStartTick = -1;
        _play.SkipToShutter = false;
        _play.Review?.Dispose();
        _play.Review = null;
        _play.Training = null;
        if (_play.Music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _play.Music = null;
        _play.Clock.Reset();
        _directStart = false;
        if (!finished)
            Shell.Overlay.Clear();
        ResetRainbow();
        Shell.ReportDiagnostics();
        Shell.Gameplay.ReportDiagnostics();
        var finishedCourse = _course;
        Shell.Hosts.DanResult = finished && _course is { } course ? danResult(course) : null;
        var dan = Shell.Hosts.DanResult is not null;
        CourseSongs = dan ? _courseSongs : [];
        Shell.Gameplay.Stop();
        Shell.Gameplay.Dan = null;
        _course = null;
        dropNextAudio();
        // Online, the other player's lane is theirs to keep (their own game saves it); this player's is kept as any play.
        var remoteLane = Shell.Match.InSong ? Shell.Gameplay.RemoteLaneIndex : -1;
        Shell.Match.SongEnded();
        // Results show the last saved play's bests and rankings otherwise.
        Array.Clear(Shell.Bests);
        Array.Clear(Shell.Placements);
        Shell.Hosts.AiNumbers = null;
        Shell.Hosts.AiSectionBest = null;
        if (finished && Shell.PlayRequests.Active is { } played)
        {
            saveScore(played, remoteLane);
            if (Shell.Hosts.DanResult is { } danPlay && finishedCourse is not null)
                saveCourse(played, danPlay, finishedCourse);
        }
        Shell.PlayRequests.ClearActive();
        if (finished && !dan)
            Shell.Sounds?.Gameplay.Play(null, GameplaySoundEvent.SongFinished);
        if (!show)
        {
            _play.Charts = [];
            _play.Autoplay = null; _play.AiBot = null; _play.Battle = null;
            return;
        }
        Shell.Hosts.AiOutcome = _play.Battle?.Outcome;
        Shell.Catalog.Replace(dan ? FlowScenes.DaniResults
            : Shell.Hosts.AiOutcome is not null ? FlowScenes.AiResults
            : Shell.Gameplay.WaiwaiOutcome is not null ? FlowScenes.WaiwaiResults : FlowScenes.Results);
        if (!finished) _play.Charts = [];
        _play.Autoplay = null; _play.AiBot = null; _play.Battle = null;
        Shell.Scenes.Show(finished ? FlowScenes.Result : FlowScenes.SongSelect);
        Console.WriteLine($"Gameplay ended at tick {Shell.Tick}; showing {Shell.Active.Id}.");
    }

    /// <summary>The songs of the course the results are showing.</summary>
    public IReadOnlyList<SongSelectSong> CourseSongs { get; private set; } = [];

    private DanCourseResult danResult(DanCourseTimeline course)
    {
        var total = Shell.Gameplay.DanTotal;
        var counts = Shell.Gameplay.DanSongs(course.Songs.Count);
        var plan = Shell.Gameplay.Dan!;
        return new DanCourseResult(plan, [.. course.Songs.Select((song, index) => new DanSongResult(
                Shell.Hosts.Dan?.Genre(_courseSongs[index].Descriptor) ?? 0, (int)song.Course, song.Chart.Level ?? 0, counts[index]))],
            _play.CourseSong + 1, total, Shell.Gameplay.DanFailed ? 0 : DanCourseResult.LevelOf(plan, total, Shell.Gameplay.DanSongEnds))
            { SongEnds = [.. Shell.Gameplay.DanSongEnds] };
    }

    // Offline too: nothing here waits for the server; it goes up with the next upload.
    private void saveCourse(PlayRequest request, DanCourseResult result, DanCourseTimeline course)
    {
        // Failed runs aren't kept, here or on the server.
        if (result.Level == 0 || Shell.Autoplay || Shell.DevAutoplay || Shell.Sync.Scores is not { } scores
            || TaikoGuest.Profiles[(int)request.Players[0].Player] is not { } profile)
            return;
        var replays = course.Split(Shell.Gameplay.Replays[0]);
        try
        {
            scores.SaveDan(new DanPlayRecord(Guid.NewGuid(), profile.Baid, result,
                [.. course.Authored.Select(static song => (string?)ChartHash.Compute(song.Chart, song.Course))], DateTimeOffset.UtcNow,
                [.. replays.Select(static replay => replay.Encode())])
            {
                AudioOffsetMs = (int)_play.AudioOffset.TotalMilliseconds,
                InputOffsetMs = (int)_play.InputOffset.TotalMilliseconds,
            });
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // A lost score must not end the credit.
            Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
        }
        var encoded = replays.Select(static replay => replay.Encode()).ToList();
        _ = Task.Run(() => checkCourseScore(result, course, encoded));
        Shell.Sync.Upload(profile);
    }

    // The server scores a course from its songs' replays and stored charts: this play should score the same there.
    private static void checkCourseScore(DanCourseResult result, DanCourseTimeline course, IReadOnlyList<byte[]> replays)
    {
        try
        {
            var stored = course.Authored.Select(static song => ChartHash.Deserialize(ChartHash.Serialize(song.Chart, song.Course), song.Chart.Key.Song.Source)).ToList();
            var first = stored[0];
            var again = PlayRescore.ScoreCourse(result.Course, DanCourseTimeline.Merge(stored), [.. replays.Select(TaikoReplay.Decode)],
                Gameplay.TaikoGameplayPresentation.WindowsFor(first.Chart, first.Course));
            var same = again.Total == result.Total && again.Level == result.Level && again.SongsPlayed == result.SongsPlayed
                && again.Songs.SequenceEqual(result.Songs.Select(static song => song.Counts));
            Console.WriteLine(same ? "Dan play rescored the same from its replays."
                : $"Error DAN_RESCORE: played {result.Total} level {result.Level} songs {result.SongsPlayed}; rescored {again.Total} level {again.Level} songs {again.SongsPlayed}.");
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or EndOfStreamException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error DAN_RESCORE: {exception.Message}");
        }
    }

    /// <summary>A drum's player's AI battle standing (rank points, wins, last timing); none without a profile or store.</summary>
    private AiProgress aiProgress(LocalPlayerSlot slot) =>
        (TaikoGuest.Profiles[(int)slot] ?? (Shell.Arcade.Home ? ScoreProfile.LocalGuest : null)) is { } profile
            && Shell.Sync.Scores is { } scores ? scores.AiProgress(profile.Baid) : AiProgress.None;

    // Each lane whose drum has a profile saves its play. ponytail: Waiwai (shared voltage, its own
    // results) is not saved until its mode is worked out.
    private void saveScore(PlayRequest request, int remoteLane)
    {
        Array.Clear(Shell.Bests);
        Array.Clear(Shell.Placements);
        // An AI battle's intermediate level saves on its own simplified chart (Green: crowns only on levels 1/5/9/13/裏, but
        // its wins count).
        if (Shell.Autoplay || Shell.DevAutoplay || _play.Voided || Shell.Sync.Scores is not { } scores || Shell.Gameplay.WaiwaiOutcome is not null)
            return;
        var saved = new List<ScoreProfile>();
        for (var lane = 0; lane < _play.Charts.Length && lane < request.Players.Length; lane++)
        {
            var player = request.Players[lane];
            if (lane == remoteLane)
                continue;
            // Home: a guest's play is kept on this PC (baid 0, never uploaded).
            if ((TaikoGuest.Profiles[(int)player.Player] ?? (Shell.Arcade.Home ? ScoreProfile.LocalGuest : null)) is not { } profile)
                continue;
            try
            {
                var played = _play.PlayedSources[lane];
                var sha = ChartHash.Compute(played, player.Course);
                var id = Guid.NewGuid();
                var shinuchi = player.Options.Shinuchi;
                if (lane < Shell.Bests.Length)
                    Shell.Bests[lane] = scores.PreviousBest(profile.Baid, sha, id, shinuchi) ?? Shell.Gameplay.Results[lane].Score;
                // Failed plays are kept on this PC only (never uploaded, see ScoreStore.PendingPlays): the results'
                // best then shows a hard song's progress; they get no crown or rank-in.
                var cleared = Shell.Gameplay.Results[lane].Cleared;
                // Online players only (the guest's baid is never uploaded), from the board song select fetched.
                // ponytail: 真打 plays get no rank-in on the results (song select fetched the normal board only).
                if (cleared && lane < Shell.Placements.Length && profile.Baid != ScoreProfile.LocalGuestBaid && _song is not null && !shinuchi
                    && Shell.Hosts.Rankings?.For(_song) is { } boards && (int)player.Course < boards.Length)
                {
                    var top = boards[(int)player.Course] ?? []; // fetched, nobody on it yet
                    var placement = Shell.Placements[lane] = RankingBoard.Place(top, profile.Baid, profile.DisplayName,
                        (int)Shell.Gameplay.Results[lane].Score);
                    if (placement.RankIn >= 0)
                        Shell.Hosts.Rankings.Record(_song, (int)player.Course, placement.Top);
                }
                // AI battle (the player's lane): its battle, and the rank points it earned on the standing before it.
                AiBattlePlay? battle = null;
                if (lane == 0 && _play.Battle?.Outcome is { Sections.Count: > 0 } outcome)
                {
                    var ranks = _play.AiUploads ? AiBattleRanks.Green : Shell.AiRanks;
                    var before = AiSession is { } setup ? new AiProgress(setup.Points, setup.Wins, setup.Timing) : scores.AiProgress(profile.Baid);
                    // Offline, a signed-in player's battle counts provisionally; the server verifies it on upload.
                    var ranked = AiSession?.Ranked ?? _play.AiUploads;
                    var award = ranks.Award(before.Points, player.Course, played.Level ?? 0, outcome.Won, cleared,
                        usual: !_play.Intermediate && ranked, firstWin: !scores.HasAiWin(profile.Baid, sha));
                    battle = new AiBattlePlay(outcome.Won, string.Concat(outcome.Sections.Select(static section => section.Won ? 'W' : 'L')),
                        _play.SupportLevel, award.Points)
                    {
                        SessionId = AiSession?.Id, Ranked = ranked,
                        BaseSha256 = ChartHash.Compute(_sourceCharts[lane], player.Course), Prior = _play.AiPrior,
                        SectionCounts = Waddamburo.Game.Gameplay.AiSectionCounts.Format(outcome.Sections.Select(static section => section.Player)),
                    };
                    Shell.Hosts.AiSectionBest = scores.AiSectionBest(profile.Baid, sha);
                    var rank = ranks.RankOf(before.Points);
                    Shell.Hosts.AiNumbers = new(rank, ranks.RankOf(before.Points + award.Points), ranks.PointsIntoRank(before.Points),
                        award.Points, award.FirstWin, award.HighLevel, ranks.StepSize(rank), before.Wins, AiBattleRanks.NextRewardWins(before.Wins))
                        { ShowProgress = ranked };
                    Console.WriteLine($"AI battle saved: {(outcome.Won ? "won" : "lost")} {battle.Sections}, {award.Points} rank point(s).");
                }
                Shell.RememberUploadChart(player.Chart, player.ChartAsset);
                scores.Save(new PlayRecord(id, profile.Baid, sha,
                    player.Chart, battle is null ? "normal" : "ai_battle", Shell.Gameplay.Results[lane], DateTimeOffset.UtcNow,
                    Shell.Gameplay.Replays[lane].Encode())
                    {
                        UploadBlocked = _play.UploadProtectedCharts.Contains(player.Chart) || Shell.Assets.UploadBlocked(player.ChartAsset),
                        AudioOffsetMs = (int)_play.AudioOffset.TotalMilliseconds,
                        InputOffsetMs = (int)_play.InputOffset.TotalMilliseconds,
                        // AI-don's decisions use the seed even without random chart options.
                        Seed = battle is not null || player.Options.Random != TaikoRandom.None ? _play.Seed + lane : null,
                        Battle = battle,
                        Timing = lane == 0 ? GhostTimingModel.TimingOf(_play.HitOffsets) : null,
                    },
                    ChartUpload.From(played, player.Course, _song?.Descriptor),
                    uploadCustomSongs: _play.CustomUploadsAtStart && Shell.Arcade.UploadCustomSongs);
                saved.Add(profile);
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                // A lost score must not end the credit.
                Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
            }
        }
        foreach (var profile in saved)
            Shell.Sync.Upload(profile);
    }

    private AudioStreamTransport? startAudio(PlayRequest request)
    {
        var playback = openAudio(request);
        prepareNextCourseSong();
        return playback;
    }

    // A course's next song decodes ahead, off this thread: starting it there would stall the game.
    private Task<BufferedAudioSource>? _nextAudio;

    private void prepareNextCourseSong()
    {
        if (_course is null || _play.CourseSong + 1 >= _courseSongs.Count || Shell.Audio is not { } audio
            || _courseSongs[_play.CourseSong + 1].Descriptor.AudioAsset is not { } asset)
            return;
        var next = _courseSongs[_play.CourseSong + 1];
        _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(next));
        _ = Shell.Titles.Resolve(Shell.Titles.GetGameplayTitle(next));
        _nextAudio = Task.Run(async () =>
        {
            SongLoudness.Volume(Shell.Assets, asset, audio.Mixer.Format); // measured now, read back when it starts
            var stream = await Shell.Assets.OpenReadAsync(asset).ConfigureAwait(false);
            BufferedAudioSource source;
            try
            {
                source = new BufferedAudioSource(stream, audio.Mixer.Format);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            await source.Ready.ConfigureAwait(false);
            return source;
        });
    }

    private void dropNextAudio()
    {
        _nextAudio?.ContinueWith(static task =>
        {
            if (task.IsCompletedSuccessfully)
                task.Result.Dispose();
        }, TaskScheduler.Default);
        _nextAudio = null;
    }

    private AudioStreamTransport? openAudio(PlayRequest request)
    {
        var playing = _course?.Songs[_play.CourseSong];
        if (_nextAudio is { } prepared && Shell.Audio is { } ready)
        {
            _nextAudio = null;
            return ready.PlayTransport(new ScheduledAudioSource(prepared.GetAwaiter().GetResult(),
                _play.Timeline!.AudioStart, _play.Timeline.LeadIn + playing!.Chart.Duration + TimeSpan.FromSeconds(1)),
                volume: SongLoudness.Volume(Shell.Assets, _courseSongs[_play.CourseSong].Descriptor.AudioAsset!, ready.Mixer.Format));
        }
        var asset = playing is null ? request.AudioAsset : _courseSongs[_play.CourseSong].Descriptor.AudioAsset;
        if (Shell.Audio is not { } audio || asset is not { } audioAsset)
            return null;
        Stream inputStream;
        try
        {
            inputStream = Shell.Assets.OpenReadAsync(audioAsset).AsTask().GetAwaiter().GetResult();
        }
        catch (IOException exception)
        {
            // A chart whose WAVE file is missing plays silent rather than leaving the load waiting forever.
            Console.Error.WriteLine($"Song audio unavailable, playing silent: {exception.Message}");
            return null;
        }
        BufferedAudioSource? source = null;
        try
        {
            try
            {
                source = new BufferedAudioSource(inputStream, audio.Mixer.Format);
            }
            catch
            {
                inputStream.Dispose();
                throw;
            }
            source.Ready.GetAwaiter().GetResult();
            var playback = audio.PlayTransport(new ScheduledAudioSource(source,
                _play.Timeline!.AudioStart, _play.Timeline.LeadIn + (playing?.Chart ?? _play.Charts[0]).Duration + TimeSpan.FromSeconds(1)),
                volume: SongLoudness.Volume(Shell.Assets, audioAsset, audio.Mixer.Format));
            source = null;
            return playback;
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>What the run printed if it stopped mid-song.</summary>
    public void ReportFinal(SceneId active)
    {
        if (active != FlowScenes.Gameplay)
            return;
        Console.WriteLine($"Loaded gameplay charts: {_play.Charts.Length}.");
        Shell.Gameplay.ReportDiagnostics();
    }
}
