using Waddamburo.App.Presentation;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow.Gameplay;

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
    public void PrepareCourse(DanCourseTimeline course, IReadOnlyList<SongSelectSong> songs) =>
        // The course stays the mode after its results, which end it: a restart plays it again.
        _mode = new DanCoursePlay(this, course, songs, Shell.Gameplay.Dan!);

    public void Prepare(PlayableChart[] charts, SongSelectSong song, int side, WaiwaiComposition? waiwai)
    {
        AiSession = null;
        // AI-don plays the same chart on the second lane (Song Select's startSong doubled it).
        _mode = waiwai is not null ? new WaiwaiPlay(this, waiwai)
            : Shell.Credit.AiBattle && charts.Length == 2 ? new BattlePlay(this) : new NormalPlay(this);
        _directStart = false;
        _sourceCharts = charts;
        _song = song;
        _side = side;
    }

    public override ScreenKind ScreenFor(SceneId scene) => _mode.Screen;

    /// <summary>
    /// A demo play's lane for <paramref name="song"/> (its skin, the parts picked by <paramref name="random"/>): prefetched
    /// ahead, then shown. Safe on a worker thread.
    /// </summary>
    public SceneDefinition DemoScene(SongSelectSong song, string category, Random random) =>
        Shell.Screens.Gameplay(FlowScenes.Gameplay, random, Shell.EnsoLayout,
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
        Shell.Credit.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), 1, Final: false);
        Shell.Catalog.Replace(scene);
        Prepare([loaded], song, 0, null);
        _mode = new DemoPlay(this, takeOver);
        _directStart = true;
        Shell.Scenes.Show(FlowScenes.Gameplay);
        // A drum hit during the demo opens Entry: it loads in the background again (the demo's lane took the slot).
        Shell.Scenes.Prefetch(Shell.Catalog.GetDefinition(FlowScenes.Entry));
        Console.WriteLine($"Demo play: {song.Descriptor.Key} ({course}).");
        return true;
    }

    public override void Exit(SceneId scene)
    {
        Shell.Application.Squeeze43 = false; // menus stay 16:9
        Playing = false;
        PlayingChart = null;
        Presence = null;
        dropPrepared();
        _mode.Exit();
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
        dropPrepared();
        _play = new Play();
        _mode.Begin();
        var request = Shell.PlayRequests.ActivatePending();
        _play.LastRequest = request;
        _play.CustomUploadsAtStart = Shell.Arcade.UploadCustomSongs;
        _play.UploadProtectedCharts = request.Players.Where(player => Shell.Assets.UploadBlocked(player.ChartAsset))
            .Select(static player => player.Chart).ToHashSet();
        Playing = _mode.RealPlay && request.Review is null;
        PlayingChart = Playing && !_mode.SeveralSongs && Shell.Arcade.Home && _sourceCharts.Length > 0
            ? ChartHash.Compute(_sourceCharts[0], request.Players[0].Course) : null;
        Presence = !Playing ? null
            : _mode.Presence is { } modePresence ? (modePresence, DateTimeOffset.UtcNow)
            // Custom (TJA) songs stay with their communities: only the official ones and osu! beatmaps are named.
            : _song is { } song ? ($"{(song.Descriptor.Key.Source == SongSourceKind.Tja ? "A custom song"
                    : song.Descriptor.Title.English ?? song.Descriptor.Title.Primary)} · {courseName(request.Players[0].Course)}"
                + (_sourceCharts.ElementAtOrDefault(0)?.Level is { } level ? $" ★{level}" : ""), DateTimeOffset.UtcNow)
            : null;
        // Each player's options on their chart; the random ones from the play's seed (a replay's own).
        _play.Seed = _mode.Seed(request);
        // An online lane has no player of its own here: it plays with the other player's options, which an echo copies.
        var players = request.Players.Length < _sourceCharts.Length ? [.. request.Players, request.Players[0]] : request.Players.ToArray();
        // A player's own modchart for their chart (by its hash) goes over the chart's effects, before the options.
        _play.PlayedSources = _mode.Sources(_sourceCharts, players, request);
        _play.Charts = [.. _play.PlayedSources.Select((chart, lane) =>
        {
            var player = players.ElementAtOrDefault(lane);
            var authored = player is null ? chart : PlayerModcharts.Apply(EditorModchartFolder, chart, player.Course);
            // The player's layout where neither gave modchart changes; arcade, the attract's demo play and dan courses
            // (a chart of several songs) are always the stock screen.
            if (player is not null && Shell.Arcade.Home && _mode.UsesPlayerLayout && chart.Segments.IsEmpty)
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
            showMode(_mode.ModeLabel
                ?? (Shell.Autoplay && request.Review is null && !_mode.SeveralSongs ? Strings.T("mode.autoplay") : ModeText(request)));
        }
        _play.Timeline = _mode.Timeline();
        _play.StartTick = Shell.Tick;
        _play.AudioOffset = TimeSpan.FromMilliseconds(Shell.Arcade.AudioOffsetMs);
        _play.InputOffset = TimeSpan.FromMilliseconds(Shell.Arcade.InputOffsetMs);
        // Home: a recorded play (a replay picked in song select) or the autoplay is reviewed (pause,
        // scrub, speed) rather than played live; it is never saved.
        _play.Review = !Shell.Arcade.Home || Shell.Headless && Shell.Options.FlowSmoke == FlowSmokeMode.Off || _mode.SeveralSongs ? null
            : request.Review is { } recorded && request.Players.Length == 1
                ? reviewOf(request, [recorded.Inputs], chartEnd() + TimeSpan.FromSeconds(2), recorded.InputOffset)
            : Shell.Autoplay
                ? reviewOf(request, [.. _play.Charts.Select(GameplayAutoplay.InputsFor)], chartEnd() + TimeSpan.FromSeconds(2), _play.InputOffset)
            : null;
        AutoplayReview = _play.Review is not null && request.Review is null;
        // Reviews show it too: seeking back rebuilds the lanes, which takes a shown banner down.
        Shell.Gameplay.EndBanner = _mode.EndBanner;
        // A course saves apart, whole (saveCourse).
        // A play while the skin editor runs is voided too: movies may change under it, nothing is saved or sent.
        _play.Voided = request.Review is not null || !_mode.RealPlay || _mode.SeveralSongs || Shell.DevAutoplay || Shell.SkinEditing;
        if (_play.Review is not null && request.Review?.Player is { } watched)
            Shell.Players.WatchAs(_side, watched);
        _play.Lanes.Autoplay = (Shell.Autoplay || _mode.AutoplayInterval is not null || Shell.DevAutoplay) && _play.Review is null
            ? new GameplayAutoplay(_mode.AutoplayCharts, _side, _mode.AutoplayInterval) : null;
        _play.Lanes.Echo = !_mode.EchoesPartner || _play.Review is not null || request.Players.Length == _sourceCharts.Length ? OnlineEcho.Start(null, clear: Shell.Gameplay) : OnlineEcho.Start(Shell.Gameplay);
        if (Shell.Match.InSong)
            Shell.Match.Start(Shell.Gameplay, request.Players.ToList().FindIndex(player => player.Player != localSlot()));
        // A 4:3 layout (camera.aspect below 1): the stage drawn squeezed into a 4:3 picture.
        Shell.Application.Squeeze43 = !Shell.Headless && !Shell.Credit.AiBattle && _play.Charts.Length > 0 && fourThree(_play.Charts[0].Effects);
        Shell.Gameplay.Start(_play.Charts, active, [.. players.Select(player => player.Course)], _side, _mode.Composition, _play.Review?.Timelines);
        // The provider's player.*, per lane: the drum's tone, account (or Guest), how it plays.
        for (var lane = 0; lane < _play.Charts.Length; lane++)
        {
            Shell.Gameplay.Publish("player.tone", LumenHostValue.FromNumber(Shell.Sounds?.Gameplay.Tones.ElementAtOrDefault(lane) ?? 0), lane);
            // ponytail: the パパママ support option is not in the engine; the name is there for a skin to read already.
            Shell.Gameplay.Publish("player.support", LumenHostValue.FromBoolean(false), lane);
            Shell.Gameplay.Publish("player.autoplay", LumenHostValue.FromBoolean(request.Review is null
                && (Shell.Autoplay || _mode.AutoplayInterval is not null || Shell.DevAutoplay || _mode is BattlePlay && lane == 1)), lane);
            Shell.Gameplay.Publish("player.replay", LumenHostValue.FromBoolean(request.Review is not null), lane);
            Shell.Gameplay.Publish("play.level", lane < players.Length && _song?.Level(players[lane].Course) is { } stars
                ? LumenHostValue.FromNumber(stars) : LumenHostValue.Undefined, lane);
            Shell.Gameplay.Publish("player.name", LumenHostValue.FromString(
                TaikoGuest.Profiles[_play.Charts.Length == 2 ? lane : _side]?.DisplayName ?? Strings.T("setup.guest")), lane);
        }
        var hitOffsets = _play.HitOffsets; // this play's, also for a judgement reported after the next one began
        if (_play.Review is null && _play.Charts.Length > 0)
            Shell.Gameplay.OnJudged(0, judgement =>
            {
                if (!judgement.StrongHitCompleted && !judgement.TimedOut && judgement.Offset is { } offset && judgement.Result is not null)
                    hitOffsets.Add(offset.TotalMilliseconds);
            });
        _mode.LanesStarted(active, players);
        if (Shell.Sounds is { } sounds)
            // Per lane: AI-don's lane 1 is padded with the player, so it follows the player's course.
            sounds.Gameplay.RollVoicesOff = [.. players.Select(static player => player.Course >= TaikoCourse.Normal)];
        if (_directStart)
        {
            _play.Music = _play.Review is null ? startAudio(request) : null;
            _play.Clock.Restart();
            _mode.SongStarts();
        }
        else if (_play.Review is null && !silentHeadless && Shell.Audio is { } device && request.AudioAsset is { } music)
            _prepared = (music, decodeAhead(music, device));
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
        screen.ShowMode(modeLabel(text));
    }

    /// <summary>A replay (one player's, not Waiwai) can turn into practice: P in it.</summary>
    private bool canPractise => _play.Review is not null && _play.Training is null && _play.Charts.Length == 1;

    /// <summary>A replay that can turn into practice (its touch button).</summary>
    public bool CanPractise => canPractise;

    /// <summary>
    /// A replay becomes practice of the song, paused where it was (Space plays the drum live from there):
    /// the whole song is open again, the player's own offsets and look apply, and nothing is saved.
    /// </summary>
    /// <summary>Starts practice as the practice key would (eng/flow-smoke.sh's training run); false: not yet possible.</summary>
    internal bool TryStartPractice()
    {
        if (!revealed || _play.Paused || !canPractise)
            return false;
        startPractice();
        return true;
    }

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
    private TimeSpan chartTime() => _mode.ChartTime(songTime());

    private TimeSpan songTime() => _play.Review is { } review ? revealed ? review.Clock.Position : review.Clock.Start : -_play.AudioOffset + _play.Timeline?.ChartTime(
        !revealed ? TimeSpan.Zero
        : _play.MusicEndPosition is { } ended ? ended + _play.AfterMusic.Elapsed
        : _play.Music is not null ? Shell.Audio!.GetPosition(_play.Music)
        : Shell.Headless ? TimeSpan.FromSeconds(Math.Max(0, Shell.Tick - _play.StartTick - _play.PausedTicks
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
    public string? ActiveLayout => Shell.Credit.AiBattle ? null : TwoPlayerLayouts ? Shell.Arcade.Layout2P : Shell.Arcade.Layout;

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
        Shell.Application.Squeeze43 = !Shell.Headless && !Shell.Credit.AiBattle && fourThree(_play.Charts[0].Effects);
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
        && _mode.UsesPlayerLayout && !_mode.SeveralSongs && _mode.RealPlay && !Shell.Match.InSong && Shell.Active.Id == FlowScenes.Gameplay;

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
            _play.Music = null; _play.Lanes.Stop();
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
            _play.Music = null; _play.Lanes.Stop();
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

    public bool CanPause => revealed && _play.ShutterStartTick < 0 && !_play.Paused && !Shell.Overlay.IsShown && _mode.RealPlay;


    /// <summary>Closes the shutter at once (home, a played song whose notes are over); false when it cannot.</summary>
    public bool SkipToShutter()
    {
        if (_play.Review is not null || !ChartOver || !CanPause)
            return false;
        _play.SkipToShutter = true;
        return true;
    }
    public bool CanQuickRestart => revealed && _play.ShutterStartTick < 0 && !Shell.Overlay.IsShown && _mode.RealPlay;

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
        _mode.Restarting();
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
        if (_play.Paused || Shell.Editor.FreezeAnimations && Shell.Editor.IsOpen) return;
        var animationFrames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(animationFrames);
    }

    // Paused or frozen (the editors' Freeze animations), the movies stand on one tick: no blending between ticks.
    private TimeSpan? _frameTime;

    // Diagnostic (WADDAMBURO_HITCH_TRACE=1): a frame whose notes moved further or less than the time since the last
    // frame (the clock jumped).
    private static readonly bool NoteStepTrace = DiagnosticSwitches.HitchTrace;
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
        screen.UpdateVisibility(showStage, showInfo: _play.Lanes.Battle?.ResultShownAt is null);
        showVideo(time);
        return Shell.Gameplay.CreateSnapshot(time, _play.Paused ? _play.PauseInterpolation
            : Shell.Editor.FreezeAnimations && Shell.Editor.IsOpen ? 1 : interpolation);
    }

    // Live drum input is judged per display frame (headless, per tick).
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        // The notes are drawn at the chart time of the frame's start, as the movies are blended at the frame start's
        // tick fraction. Read when the frame was built instead, a frame that judged a hit (judgement, score, sounds,
        // AI-don: a few ms on a slow PC) read it later than its neighbours and drew the lane ahead: it jerked forward
        // as the note in front was hit.
        _frameTime = _play.Review is null && revealed && !_play.Paused ? chartTime() : null;
        if (CanEditEffects && !Shell.Editor.IsOpen
            && keys.Presses.Any(static press => press.Key == SdlKeyboardKey.F8)) Shell.Editor.Open();
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
            _play.Lanes.Advance(Shell.Gameplay, keys, _mode.PlayerKeys(keys), time);
        }
        _play.Lanes.Echo?.Pump();
    }

    /// <summary>Home: the song is under way and not already a review, so its pause menu can replay it.</summary>
    public bool CanInstantReplay => Shell.Arcade.Home && _play.Review is null && revealed && _play.Charts.Length == 1
        && Shell.Active.Id == FlowScenes.Gameplay && !_mode.SeveralSongs;

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
        new(Shell, request, _play.Charts, _side, _mode.Composition, inputs, _play.Timeline!.LeadIn, end, inputOffset,
            songStart: _play.Timeline.AudioStart - _play.Timeline.LeadIn - _play.AudioOffset);

    public override void Tick(FlowInput input)
    {
        if (_play.Paused) return;
        if (_mode.TakesTick(input))
            return;
        var overlay = Shell.Overlay;
        // Online: both games open the rainbow together, once both could.
        if (overlay.Player is not null && Rainbow.ShouldStartReveal(Shell.Tick) && musicReady && Shell.Match.MayReveal())
        {
            var request = Shell.PlayRequests.Active
                ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
            _play.StartTick = Shell.Tick;
            if (_mode.Reveal())
            {
                _play.Music = _play.Review is null ? startAudio(request) : null; // a review plays its own music
                _play.Clock.Restart();
                _mode.SongStarts();
            }
            overlay.Reveal(title: _mode.RevealTitle);
            Rainbow.StartReveal();
            Console.WriteLine($"Rainbow reveal and gameplay started at tick {Shell.Tick}.");
            return;
        }
        if (overlay.Player is { } opening && Rainbow.FinishRevealWhenStopped(opening.IsPlaying))
        {
            overlay.Clear();
            Console.WriteLine($"Rainbow reveal completed at tick {Shell.Tick}.");
        }
        _mode.BeforeTick();
        if (!revealed || _play.Review is not null) // a review never ends by itself: Escape leaves it
            return;
        var elapsed = chartTime();
        elapsed = _mode.AdvanceSong(elapsed);
        if (Shell.Headless)
        {
            _play.Lanes.Advance(Shell.Gameplay, input.Keys, _mode.PlayerKeys(input.Keys), elapsed - _play.InputOffset);
            _play.Lanes.Echo?.Pump();
        }
        var bannerAt = _mode.EndsAt ?? (_play.Charts.Length == 0 ? (TimeSpan?)null : _play.Charts.Max(TaikoResultBanner.Time));
        var deviceFailure = Shell.Audio?.Failure;
        var audioFailure = deviceFailure ?? _play.Music?.Failure;
        if (audioFailure is not null)
        {
            var ending = _play.ShutterStartTick >= 0 || _play.SkipToShutter || _mode.Ending(elapsed, bannerAt);
            if (!ending)
            {
                if (deviceFailure is not null)
                    throw new IOException("Gameplay audio failed.", deviceFailure);
                Console.Error.WriteLine($"Gameplay audio failed: {audioFailure.Message}");
                Shell.Sync.Notices.AddLocal(Strings.T("gameplay.audio_failed"), NoticeSeverity.Warning);
                end(finished: false);
                return;
            }
        }
        // Online: once this player's notes are over, their result goes to the other player as it stands.
        if (Shell.Match.InSong && !_play.ResultSent && ChartOver)
        {
            _play.ResultSent = true;
            var lane = 1 - Shell.Gameplay.RemoteLaneIndex;
            if ((uint)lane < (uint)Shell.Gameplay.Results.Count)
                Shell.Match.SendResult(Shell.Gameplay.Results[lane]);
        }
        // Audio running past the chart's end fades under the shutter: some songs' files go on long after their chart
        // (Stock:hanakp: silence, then more song; #4).
        var musicFinished = audioFailure is not null || _play.Music is not { } music
            || Shell.Audio is null || !Shell.Audio.Mixer.IsPlaying(music.Handle);
        if (musicFinished && _play.Music is { } finished && _play.MusicEndPosition is null)
        {
            _play.MusicEndPosition = Shell.Audio!.GetPosition(finished);
            _play.AfterMusic.Restart();
            if (audioFailure is not null)
            {
                Console.Error.WriteLine($"Gameplay audio stopped during the ending: {audioFailure.Message}");
                Shell.Audio.Mixer.Stop(finished.Handle);
            }
        }
        var chartFinished = _mode.Finished(elapsed, bannerAt);
        if ((!input.Escape || Shell.Arcade.Home) && _play.ShutterStartTick < 0
            && (_play.SkipToShutter || chartFinished && !Shell.Gameplay.OverlayActive))
        {
            overlay.Clear();
            if (!musicFinished && _play.Music is { } outro)
                Shell.Audio?.Mixer.Stop(outro.Handle, TimeSpan.FromSeconds(1));
            if (_mode.Shutter is { } closing)
                overlay.Show(closing);
            _mode.ShutterClosing();
            _play.ShutterStartTick = Shell.Tick;
            _play.ShutterClosing = false;
        }
        // Close registers on the shutter's first frame; retry until it exists.
        if (_play.ShutterStartTick >= 0 && !_play.ShutterClosing && overlay.Player is not null)
            // The shutter takes the player's colour (traced Close(1) for the right drum's player, Close(2) for two).
            _play.ShutterClosing = overlay.Cover(side: Shell.Credit.TwoPlayers ? 2 : Shell.Credit.PlayerSide);
        // ponytail: 70 ticks = traced Close -> results load (1.17 s); the close itself takes ~1 s.
        // (AI battle: no shutter; its delay already ends at the results.)
        if (input.Escape && !Shell.Arcade.Home || _play.ShutterStartTick >= 0 && (Shell.Tick - _play.ShutterStartTick >= 70 || _mode.ResultsCutIn))
        {
            // An abandoned song gives its stage back: Song Select counts it again when the next one starts (#21).
            if (input.Escape && !Shell.Arcade.Home && Shell.Credit.SongsPlayed > 0)
            {
                var tutorialSeen = Shell.Credit.TutorialSeen; // Giving a stage back is not a new credit.
                var waiwaiTutorialSeen = Shell.Credit.WaiwaiTutorialSeen;
                var aiTutorialSeen = Shell.Credit.AiTutorialSeen;
                Shell.Credit.SongsPlayed--;
                Shell.Credit.TutorialSeen = tutorialSeen;
                Shell.Credit.WaiwaiTutorialSeen = waiwaiTutorialSeen;
                Shell.Credit.AiTutorialSeen = aiTutorialSeen;
            }
            end(finished: !input.Escape || Shell.Arcade.Home);
        }
    }

    // A course song's clock starts: the wall clock, and headless the ticks it counts from (they kept counting from the
    // course's reveal, which put every song after the first ahead of its notes).
    private void restartSongClock()
    {
        _play.Clock.Restart();
        (_play.StartTick, _play.PausedTicks) = (Shell.Tick, 0);
    }

    // The stage number shows in arcade, in a dan course and in the song-info mode; not at home.
    private bool showStage => !Shell.HideArcadeHud || _mode.SeveralSongs || _play.SongInfoMode;

    private string genreLabel() => GenreLabels[Math.Clamp(Shell.Credit.SongInfo.Genre, 0, GenreLabels.Length - 1)];

    // song_info's genreArray: TaikoSongInfo.Genre's labels.
    private static readonly string[] GenreLabels = ["jpop", "anime", "vocaloid", "doyo", "variety", "classic", "game", "namco"];

    // The gameplay screen's movies outside the lanes (the skin's).
    private IGameplayScreen screen => Shell.Hosts.Gameplay(Shell.Active);

    // song_info's 720x64 title slot: fixed height, right-aligned, squeezed to fit.
    private void showSongTitle(SongSelectSong song)
    {
        Shell.Gameplay.Publish("song.title", LumenHostValue.FromString(song.Descriptor.Title.English ?? song.Descriptor.Title.Primary));
        Shell.Gameplay.Publish("song.subtitle", song.Descriptor.Subtitle is { Length: > 0 } subtitle
            ? LumenHostValue.FromString(subtitle) : LumenHostValue.Undefined);
        Shell.Gameplay.Publish("song.artist", song.Descriptor.Artist is { Length: > 0 } artist
            ? LumenHostValue.FromString(artist) : LumenHostValue.Undefined);
        Shell.Gameplay.Publish("song.genre", LumenHostValue.FromString(genreLabel()));
        var source = song.Descriptor.Key.Source.ToString();
        Shell.Gameplay.Publish("song.source", LumenHostValue.FromString(char.ToLowerInvariant(source[0]) + source[1..]));
        Shell.Gameplay.Publish("song.stage", showStage ? LumenHostValue.FromNumber(Shell.Credit.SongInfo.Stage) : LumenHostValue.Undefined);
        Shell.Gameplay.Publish("song.finalStage", LumenHostValue.FromBoolean(Shell.Credit.SongInfo.Final));
        screen.ShowTitle(() =>
        {
            var title = Shell.Titles.GetGameplayTitle(song);
            _ = Shell.Titles.Resolve(title);
            return title;
        });
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
        Shell.Dan.Result = null;
        CourseSongs = [];
        _mode.End(finished);
        var dan = Shell.Dan.Result is not null;
        // eng/flow-smoke.sh compares these lines between builds.
        if (finished)
            Console.WriteLine("Play results: " + string.Join(" | ", Shell.Gameplay.Results.Select(static result =>
                    $"{result.Course} {result.Score} {result.Great}/{result.Good}/{result.Miss} combo {result.MaxCombo} rolls {result.Rolls} gauge {result.GaugeSegments}{(result.Cleared ? " clear" : "")}"))
                + (Shell.Dan.Result is { } danShown ? $" | dan level {danShown.Level}" : "")
                + (_play.Lanes.Battle?.Outcome is { } battle ? $" | AI {battle.SectionsWon}/{battle.Sections.Count}" : ""));
        Shell.Gameplay.Stop();
        Shell.Gameplay.Dan = null;
        // Online, the other player's lane is theirs to keep (their own game saves it); this player's is kept as any play.
        var remoteLane = Shell.Match.InSong ? Shell.Gameplay.RemoteLaneIndex : -1;
        Shell.Match.SongEnded();
        // Results show the last saved play's bests and rankings otherwise.
        Array.Clear(Shell.Credit.Bests);
        Array.Clear(Shell.Credit.Placements);
        Shell.AiBattle.Forget();
        if (finished && Shell.PlayRequests.Active is { } played)
        {
            saveScore(played, remoteLane);
            _mode.Save(played);
        }
        Shell.PlayRequests.ClearActive();
        if (finished && !dan)
            Shell.Sounds?.Gameplay.Play(null, GameplaySoundEvent.SongFinished);
        if (!show)
        {
            _play.Charts = [];
            _play.Lanes.Stop();
            return;
        }
        Shell.AiBattle.Outcome = _play.Lanes.Battle?.Outcome;
        Shell.Catalog.Replace(_mode.Results
            ?? Shell.Screens.Results);
        if (!finished) _play.Charts = [];
        _play.Lanes.Stop();
        Shell.Scenes.Show(finished ? FlowScenes.Result : FlowScenes.SongSelect);
        Console.WriteLine($"Gameplay ended at tick {Shell.Tick}; showing {Shell.Active.Id}.");
    }

    // Each lane whose drum has a profile saves its play. ponytail: Waiwai (shared voltage, its own
    // results) is not saved until its mode is worked out.
    private void saveScore(PlayRequest request, int remoteLane)
    {
        Array.Clear(Shell.Credit.Bests);
        Array.Clear(Shell.Credit.Placements);
        // An AI battle's intermediate level saves on its own simplified chart (Green: crowns only on levels 1/5/9/13/裏, but
        // its wins count).
        if (Shell.Autoplay || Shell.DevAutoplay || _play.Voided || Shell.Sync.Scores is not { } scores || !_mode.SavesSongs)
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
                if (lane < Shell.Credit.Bests.Length)
                    Shell.Credit.Bests[lane] = scores.PreviousBest(profile.Baid, sha, id, shinuchi) ?? Shell.Gameplay.Results[lane].Score;
                // Failed plays are kept on this PC only (never uploaded, see ScoreStore.PendingPlays): the results'
                // best then shows a hard song's progress; they get no crown or rank-in.
                var cleared = Shell.Gameplay.Results[lane].Cleared;
                // Online players only (the guest's baid is never uploaded), from the board song select fetched.
                // ponytail: 真打 plays get no rank-in on the results (song select fetched the normal board only).
                if (cleared && lane < Shell.Credit.Placements.Length && profile.Baid != ScoreProfile.LocalGuestBaid && _song is not null && !shinuchi
                    && Shell.Rankings?.For(_song) is { } boards && (int)player.Course < boards.Length)
                {
                    var top = boards[(int)player.Course] ?? []; // fetched, nobody on it yet
                    var placement = Shell.Credit.Placements[lane] = RankingBoard.Place(top, profile.Baid, profile.DisplayName,
                        (int)Shell.Gameplay.Results[lane].Score);
                    if (placement.RankIn >= 0)
                        Shell.Rankings.Record(_song, (int)player.Course, placement.Top);
                }
                var battle = _mode.Battle(lane, profile, scores, sha, cleared, played, player);
                Shell.RememberUploadChart(player.Chart, player.ChartAsset);
                scores.Save(new PlayRecord(id, profile.Baid, sha,
                    player.Chart, battle is null ? "normal" : "ai_battle", Shell.Gameplay.Results[lane], DateTimeOffset.UtcNow,
                    Shell.Gameplay.Replays[lane].Encode())
                    {
                        UploadBlocked = _play.UploadProtectedCharts.Contains(player.Chart) || Shell.Assets.UploadBlocked(player.ChartAsset),
                        CreditId = Shell.Arcade.Home ? null : Shell.Credit.CreditId,
                        Side = (int)player.Player,
                        SelectSeconds = Shell.Arcade.Home ? null : TaikoGuest.SelectSeconds,
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

    // headless smoke runs (eng/flow-smoke.sh) play no music, so their song time follows the ticks and every run ends on
    // the same tick.
    private bool silentHeadless => Shell.Headless && Shell.Options.FlowSmoke != FlowSmokeMode.Off;

    private AudioStreamTransport? startAudio(PlayRequest request)
    {
        var playback = silentHeadless ? null : _mode.OpenAudio(request);
        _mode.MusicOpened();
        return playback;
    }

    // The song's music, scheduled on the play's timeline for the chart's length.
    private AudioStreamTransport? openAudio(CatalogAssetKey? asset, TimeSpan duration)
    {
        if (Shell.Audio is not { } audio || asset is not { } audioAsset)
            return null;
        var ahead = _prepared is { } prepared && prepared.Asset.Equals(audioAsset) ? prepared.Source : decodeAhead(audioAsset, audio);
        if (_prepared?.Source == ahead)
            _prepared = null;
        BufferedAudioSource? source = null;
        try
        {
            source = ahead.GetAwaiter().GetResult();
            var playback = audio.PlayTransport(new ScheduledAudioSource(source,
                _play.Timeline!.AudioStart, _play.Timeline.LeadIn + duration + TimeSpan.FromSeconds(1)),
                volume: SongLoudness.Volume(Shell.Assets, audioAsset, audio.Mixer.Format));
            source = null;
            return playback;
        }
        catch (IOException exception) when (source is null)
        {
            // A chart whose WAVE file is missing plays silent rather than leaving the load waiting forever.
            Console.Error.WriteLine($"Song audio unavailable, playing silent: {exception.Message}");
            return null;
        }
        finally
        {
            source?.Dispose();
        }
    }

    // The song's music, decoding under the rainbow: opening it at the reveal stalled the game there.
    private (CatalogAssetKey Asset, Task<BufferedAudioSource> Source)? _prepared;

    // The rainbow waits for it (a headless run goes by its ticks alone: it waits at the reveal).
    private bool musicReady => Shell.Headless || _prepared is not { } prepared || prepared.Source.IsCompleted;

    private void dropPrepared()
    {
        drop(_prepared?.Source);
        _prepared = null;
    }

    /// <summary>A song's music opened and its first seconds decoded off this thread, where that would stall the game.</summary>
    private Task<BufferedAudioSource> decodeAhead(CatalogAssetKey asset, AudioEngine audio) => Task.Run(async () =>
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
        try
        {
            await source.Ready.ConfigureAwait(false);
        }
        catch
        {
            source.Dispose();
            throw;
        }
        return source;
    });

    private static void drop(Task<BufferedAudioSource>? ahead) => ahead?.ContinueWith(static task =>
    {
        if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
        else
            _ = task.Exception; // observed: a song nobody plays any more
    }, TaskScheduler.Default);

    /// <summary>What the run printed if it stopped mid-song.</summary>
    public void ReportFinal(SceneId active)
    {
        if (active != FlowScenes.Gameplay)
            return;
        Console.WriteLine($"Loaded gameplay charts: {_play.Charts.Length}.");
        Shell.Gameplay.ReportDiagnostics();
    }
}
