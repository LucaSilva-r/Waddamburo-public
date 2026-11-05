using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
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
internal sealed class GameplayFlow(GameShell shell) : FlowScene(shell)
{
    // The charts as played (each player's options applied) and as authored (hashed, uploaded, saved under).
    private PlayableChart[] _charts = [];
    private PlayableChart[] _sourceCharts = [];
    private int _seed;
    private SongSelectSong? _song;

    /// <summary>The song being (or last) played.</summary>
    public SongSelectSong? Song => _song;
    private int _side;
    private WaiwaiComposition? _waiwai;
    private AudioStreamTransport? _music;
    private GameplayTimeline? _timeline;
    private GameplayAutoplay? _autoplay;
    // Home, autoplay: a reviewed play (pause, seek, speed); its clock replaces the music's.
    private ReviewSession? _review;
    // Home, training: attempts on the review (_review is its playback).
    private TrainingSession? _training;
    // An instant replay made this play void: it is never saved.
    private bool _voided;
    private readonly Stopwatch _clock = new();
    private int _startTick;
    private int _shutterStartTick = -1;
    private bool _shutterClosing;
    private bool _paused;
    private bool _directStart;
    private PlayRequest? _lastRequest;
    private int _pausedAtTick;
    private int _pausedTicks;
    private float _pauseInterpolation;
    // The player's offsets, fixed for a play (judgement time must not step back mid-song).
    private TimeSpan _audioOffset;
    private TimeSpan _inputOffset;
    // The music's position stops with the music: after it, the song's time goes on by a stopwatch.
    private TimeSpan? _musicEndPosition;
    private bool _resultSent;
    private readonly Stopwatch _afterMusic = new();

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
        _courseSong = 0;
        _courseHold = TimeSpan.Zero;
        _courseEndsAt = null;
        _restartCourse = (course, Shell.Gameplay.Dan!);
    }

    // What a restart starts over: the course stays known after its results, which end it.
    private (DanCourseTimeline Timeline, DanCourse Plan)? _restartCourse;

    private DanCourseTimeline? _course;
    private DanCourseStatus? _courseStatus;
    private IReadOnlyList<SongSelectSong> _courseSongs = [];
    private int _courseSong;

    public void Prepare(PlayableChart[] charts, SongSelectSong song, int side, WaiwaiComposition? waiwai)
    {
        _course = null;
        _restartCourse = null;
        _directStart = false;
        _charts = _sourceCharts = charts;
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
    private OnlineEcho? _echo;
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
        Playing = false;
        PlayingChart = null;
        Presence = null;
        if (!_demo)
            return;
        _demo = false;
        _demoTakeOver = null;
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _music = null;
        _clock.Reset();
        _directStart = false;
        _autoplay = null;
        _charts = [];
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
        var request = Shell.PlayRequests.ActivatePending();
        _lastRequest = request;
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
        _seed = request.Seed ?? Random.Shared.Next();
        // An online lane has no player of its own here: it plays with the other player's options, which an echo copies.
        var players = request.Players.Length < _sourceCharts.Length ? [.. request.Players, request.Players[0]] : request.Players.ToArray();
        _charts = [.. _sourceCharts.Select((chart, lane) => (players.ElementAtOrDefault(lane)?.Options ?? default)
            .Apply(chart, _seed + (lane < request.Players.Length ? lane : 0)))];
        // Online, the kusudama is each player's own: the other player's lane gets balloons in its place.
        if (Shell.Match.InSong && request.Players.ToList().FindIndex(player => player.Player != localSlot()) is var remote and >= 0
            && remote < _charts.Length)
            _charts[remote] = TaikoPlayOptions.KusudamaAsBalloons(_charts[remote]);
        // Two players on different courses have different kusudama (or one only): a shared one would cover the other
        // player's lane, so each plays a balloon of its own instead.
        else if (_charts.Length == 2 && request.Players.Length == 2 && request.Players[0].Course != request.Players[1].Course)
            _charts = [.. _charts.Select(TaikoPlayOptions.KusudamaAsBalloons)];
        Shell.Gameplay.Options = [.. players.Select(static player => player.Options)];
        _paused = false;
        _pausedTicks = 0;
        _shutterStartTick = -1;
        _shutterClosing = false;
        var active = Shell.Active;
        // song_info's 720x64 title slot: fixed height, right-aligned, squeezed to fit.
        var songInfoIndex = active.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "song_info");
        if (songInfoIndex >= 0 && _song is not null)
        {
            var title = Shell.Titles.GetGameplayTitle(_song);
            _ = Shell.Titles.Resolve(title);
            active.Player.Layers[songInfoIndex].Player.SetNativeFill("song_name", title);
            ModeLabel.Show(active.Player.Layers[songInfoIndex].Player, modeLabel(_demo ? Strings.T("mode.demo") : ModeText(request)));
        }
        _timeline = _course is { } course
            ? new GameplayTimeline(course.Songs[0].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn)
            : new GameplayTimeline(_charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
        _startTick = Shell.Tick;
        _clock.Reset();
        _musicEndPosition = null;
        _resultSent = false;
        _afterMusic.Reset();
        _audioOffset = TimeSpan.FromMilliseconds(Shell.Arcade.AudioOffsetMs);
        _inputOffset = TimeSpan.FromMilliseconds(Shell.Arcade.InputOffsetMs);
        _review?.Dispose();
        // Home: a recorded play (a replay picked in song select) or the autoplay is reviewed (pause,
        // scrub, speed) rather than played live; it is never saved.
        _review = !Shell.Arcade.Home || Shell.Headless || _course is not null ? null
            : request.Review is { } recorded && request.Players.Length == 1
                ? reviewOf(request, [recorded.Inputs], chartEnd() + TimeSpan.FromSeconds(2), recorded.InputOffset)
            : Shell.Options.Autoplay
                ? reviewOf(request, [.. _charts.Select(GameplayAutoplay.InputsFor)], chartEnd() + TimeSpan.FromSeconds(2), _inputOffset)
            : null;
        // A replay or autoplay review is scrubbed back and forth: the end banner would stay up once reached.
        Shell.Gameplay.EndBanner = _review is null && !_demo;
        _training = null;
        // A course saves apart, whole (saveCourse).
        _voided = request.Review is not null || _demo || _course is not null;
        if (_review is not null && request.Review?.Player is { } watched)
            Shell.Players.WatchAs(_side, watched);
        _autoplay = (Shell.Options.Autoplay || _demo) && _review is null ? new GameplayAutoplay(_charts, _side,
            // The demo drums like a person (15 hits a second), not a stress run.
            _demo ? TimeSpan.FromSeconds(1 / 15d) : null) : null;
        _echo = _demo || _review is not null || request.Players.Length == _sourceCharts.Length ? OnlineEcho.Start(null, clear: Shell.Gameplay) : OnlineEcho.Start(Shell.Gameplay);
        if (Shell.Match.InSong)
            Shell.Match.Start(Shell.Gameplay, request.Players.ToList().FindIndex(player => player.Player != localSlot()));
        Shell.Gameplay.Start(_charts, active, [.. players.Select(player => player.Course)], _side, _waiwai, _review?.Timelines);
        if (Shell.Sounds is { } sounds)
            sounds.Gameplay.RollVoicesOff = [.. request.Players.Select(static player => player.Course >= TaikoCourse.Normal)];
        if (_directStart)
        {
            _music = _review is null ? startAudio(request) : null;
            _clock.Restart();
            showCourseSong();
        }
        Console.WriteLine(
            $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
            + $"{_charts.Sum(static chart => chart.NoteCount)} notes at tick {Shell.Tick}.");
    }

    /// <summary>What a replay or practice says in place of the song's subtitle and number (null: a play).</summary>
    public static string? ModeText(PlayRequest request) =>
        request.Review is { } review ? review.Player is { } player ? Strings.T("mode.replay_of", player.DisplayName) : Strings.T("mode.replay")
        : null;

    // song_info's corner says so after the song started as a play.
    private void showMode(string text)
    {
        var songInfo = Shell.Active.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "song_info");
        if (songInfo >= 0)
            ModeLabel.Show(Shell.Active.Player.Layers[songInfo].Player, modeLabel(text));
    }

    /// <summary>A replay (one player's, not Waiwai) can turn into practice: P in it.</summary>
    private bool canPractise => _review is not null && _training is null && _charts.Length == 1 && _waiwai is null;

    /// <summary>
    /// A replay becomes practice of the song, paused where it was (Space plays the drum live from there):
    /// the whole song is open again, the player's own offsets and look apply, and nothing is saved.
    /// </summary>
    private void startPractice()
    {
        if (_lastRequest is not { } request || _review is not { } replay)
            return;
        var at = replay.Clock.Position;
        replay.Dispose();
        Shell.Players.StopWatching();
        _voided = true;
        _review = reviewOf(request, [[]], chartEnd() + TimeSpan.FromSeconds(2), _inputOffset);
        Shell.Gameplay.EndBanner = false;
        _training = new TrainingSession(Shell, _review, _charts[0]);
        _training.Begin(at);
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
        var time = course.Songs[_courseSong].Zero + songTime();
        return time > _courseHold ? time : _courseHold;
    }

    private TimeSpan _courseHold;
    private TimeSpan? _courseEndsAt;

    private TimeSpan songTime() => _review is { } review ? revealed ? review.Clock.Position : review.Clock.Start : -_audioOffset + _timeline?.ChartTime(
        !revealed ? TimeSpan.Zero
        : _musicEndPosition is { } ended ? ended + _afterMusic.Elapsed
        : _music is not null ? Shell.Audio!.GetPosition(_music)
        : Shell.Headless ? TimeSpan.FromSeconds((Shell.Tick - _startTick - _pausedTicks
            - (_paused ? Shell.Tick - _pausedAtTick : 0)) / 60d)
        : _clock.Elapsed) ?? TimeSpan.Zero;

    /// <summary>Every player's last note, roll, balloon and kusudama is over.</summary>
    public bool ChartOver => _charts.Length != 0 && chartTime() >= chartEnd() + TimeSpan.FromMilliseconds(200);

    private TimeSpan chartEnd() => _charts.Max(static chart =>
        chart.HitObjects.Select(static note => note.StartTime)
            .Concat(chart.LongNotes.Select(static note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max());

    /// <summary>A reviewed play is on screen (home autoplay): its clock is paused, sought and sped up by the keys.</summary>
    public bool Reviewing => _review is not null && Shell.Active.Id == FlowScenes.Gameplay;

    // Plays reported from a replay's pause menu this session: each once.
    private readonly HashSet<Guid> _reported = [];

    /// <summary>A downloaded replay of another player, watched by a logged-in one: its pause menu can report it.</summary>
    public bool CanReport => reportTarget() is not null;

    private (Guid Play, ScoreClient Client)? reportTarget() =>
        Reviewing && _lastRequest?.Review is { PlayId: { } play, Player: { } watched } && !_reported.Contains(play)
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
    public ReviewClock? Review => _review?.Clock;

    /// <summary>The training under way (null when not training), for the review bar.</summary>
    public TrainingSession? Training => _training;

    /// <summary>
    /// A training attempt is being played (live, not paused): the drum is a drum, not a menu pad. Paused,
    /// a controller is its gamepad layout again (scrub, speed, loop marks).
    /// </summary>
    public bool Attempting => Reviewing && _training is { Live: true, Clock.Paused: false };

    public bool CanPause => revealed && _shutterStartTick < 0 && !_paused && !Shell.Overlay.IsShown && !_demo;

    // Escape after the last note: the shutter closes now rather than after the banner and the outro.
    private bool _skipToShutter;

    /// <summary>Closes the shutter at once (home, a played song whose notes are over); false when it cannot.</summary>
    public bool SkipToShutter()
    {
        if (_review is not null || !ChartOver || !CanPause)
            return false;
        _skipToShutter = true;
        return true;
    }
    public bool CanQuickRestart => revealed && _shutterStartTick < 0 && !Shell.Overlay.IsShown && !_demo;

    public void SetPaused(bool paused, float interpolation = 0)
    {
        if (_paused == paused) return;
        if (paused)
        {
            _pauseInterpolation = interpolation;
            _pausedAtTick = Shell.Tick;
            _clock.Stop();
            _afterMusic.Stop();
        }
        else
        {
            _pausedTicks += Shell.Tick - _pausedAtTick;
            if (revealed) _clock.Start();
            if (_musicEndPosition is not null) _afterMusic.Start();
        }
        if (_music is { } music && Shell.Audio is { } audio)
            audio.SetPaused(music, paused);
        _paused = paused;
    }

    /// <summary>
    /// Abandons the current round and loads the same match again without saving it. From a replay or
    /// practice it is the song played for real (the drum's own player again).
    /// </summary>
    public bool Restart()
    {
        if (_lastRequest is not { } last || _charts.Length == 0) return false;
        var request = last with { Review = null, Seed = null };
        Shell.Players.StopWatching();
        if (_paused) SetPaused(false);
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle);
        _music = null;
        _clock.Reset();
        Shell.Gameplay.Stop();
        // A course starts over from its first song, also from its results.
        if (_restartCourse is { } again)
        {
            dropNextAudio();
            _course = again.Timeline;
            Shell.Gameplay.Dan = again.Plan;
            Shell.Hosts.DanResult = null;
            _courseSong = 0;
            _courseHold = TimeSpan.Zero;
            _courseEndsAt = null;
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
        if (_paused) SetPaused(false);
        end(finished: false);
    }

    /// <summary>The song is dropped where it is (the service menu resets the game): cleaned up, nothing saved, no scene shown.</summary>
    public void Discard()
    {
        if (_paused) SetPaused(false);
        end(finished: false, show: false);
    }

    public override void Advance(LumenInputSnapshot input)
    {
        if (_paused) return;
        var animationFrames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(animationFrames);
    }

    public override LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        Shell.Gameplay.CreateSnapshot(chartTime(), _paused ? _pauseInterpolation : interpolation);

    // Live drum input is judged per display frame (headless, per tick).
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        if (_review is { } review)
        {
            if (revealed && !_paused && _training is { } training)
                training.Frame(keys, keysEnabled: !Shell.HomeMenuOpen);
            else if (revealed && !_paused && !Shell.HomeMenuOpen && canPractise
                && GameActions.Pressed(keys, GameAction.Practise))
                startPractice();
            else if (revealed && !_paused)
                review.Frame(keys, keysEnabled: !Shell.HomeMenuOpen);
            else
                review.Hold();
            return;
        }
        if (!Shell.Headless && revealed && !_paused)
        {
            var time = chartTime() - _inputOffset;
            Shell.Gameplay.Advance(_autoplay?.Apply(drums(keys), time) ?? keys, time);
        }
        _echo?.Pump();
    }

    /// <summary>Home: the song is under way and not already a review, so its pause menu can replay it.</summary>
    public bool CanInstantReplay => Shell.Arcade.Home && _review is null && revealed && _charts.Length == 1 && _waiwai is null
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
        if (!CanInstantReplay || _lastRequest is not { } request)
            return;
        var at = chartTime();
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _music = null;
        _voided = true;
        // Taken before the review: showing it restarts the lanes, with empty replays.
        IReadOnlyList<TaikoReplayInput>[] played = [.. Shell.Gameplay.Replays.Select(static replay => (IReadOnlyList<TaikoReplayInput>)[.. replay.Inputs])];
        var review = reviewOf(request, played, at, _inputOffset);
        review.Clock.Seek(at);
        review.Clock.SetPaused(true);
        review.ShowFresh(at);
        _review = review;
        // No note reached yet (hit or missed; drum presses alone judge nothing): there is nothing to
        // watch, so the player lands in practice there.
        // (The bake runs the whole song, so the notes after the pause are in it as misses: only what
        // happened up to the pause counts; events are in judgement time.)
        var judgedUntil = at - _inputOffset;
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
        new(Shell, request, _charts, _side, _waiwai, inputs, _timeline!.LeadIn, end, inputOffset,
            songStart: _timeline.AudioStart - _timeline.LeadIn - _audioOffset);

    public override void Tick(FlowInput input)
    {
        if (_paused) return;
        if (_demo)
        {
            // Exit (on the switch either makes) ends the demo. Once it's fading out, a hit waits for the logo: taking over
            // then left the fade's black over entry.
            if (!Shell.Scenes.FadePending && _demoTakeOver?.Invoke(input) == true || Shell.Scenes.FinishFade())
                return;
            // Escape ends the demo like its timer (it abandoned the song into Song Select, #19).
            if (input.Escape || revealed && (chartTime() >= DemoLength || ChartOver))
            {
                if (_music is { } demoMusic)
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
            _startTick = Shell.Tick;
            _music = _review is null ? startAudio(request) : null; // a review plays its own music
            _clock.Restart();
            showCourseSong();
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
        if (!revealed || _review is not null) // a review never ends by itself: Escape leaves it
            return;
        var elapsed = chartTime();
        // Traced session31: a requirement that can no longer be met ends the course with the song being played.
        if (_course is { } failing && _courseEndsAt is null && Shell.Gameplay.DanFailed)
        {
            var failed = failing.Songs[_courseSong];
            var banner = failed.Zero + TaikoResultBanner.Time(failed.Chart);
            _courseEndsAt = banner > elapsed ? banner : elapsed;
            Shell.Gameplay.EndCourseAt(_courseEndsAt.Value);
            dropNextAudio();
        }
        if (_course is { } playing && _courseEndsAt is null && _courseSong + 1 < playing.Songs.Count && elapsed >= playing.Songs[_courseSong].End)
        {
            nextCourseSong(Shell.PlayRequests.Active!);
            elapsed = chartTime();
        }
        if (Shell.Headless)
        {
            Shell.Gameplay.Advance(_autoplay?.Apply(drums(input.Keys), elapsed - _inputOffset) ?? input.Keys, elapsed - _inputOffset);
            _echo?.Pump();
        }
        if (Shell.Audio?.Failure is { } device)
            throw new IOException("Gameplay audio failed.", device);
        // The song's decoder fell behind (or its file broke): back to Song Select, unsaved, with a notice.
        if (_music?.Failure is { } song)
        {
            Console.Error.WriteLine($"Gameplay audio failed: {song.Message}");
            Shell.Sync.Notices.AddLocal(Strings.T("gameplay.audio_failed"), NoticeSeverity.Warning);
            end(finished: false);
            return;
        }
        // Online: once this player's notes are over, their result goes to the other player as it stands.
        if (Shell.Match.InSong && !_resultSent && ChartOver)
        {
            _resultSent = true;
            var lane = 1 - Shell.Gameplay.RemoteLaneIndex;
            if ((uint)lane < (uint)Shell.Gameplay.Results.Count)
                Shell.Match.SendResult(Shell.Gameplay.Results[lane]);
        }
        // Traced: the shutter closes 9 s after the end banner, by the chart, not the audio (whose end only
        // freezes the chart clock below). Audio running past it fades under the shutter: some songs' files
        // go on long after their chart (Stock:hanakp: silence, then more song; #4).
        var musicFinished = _music is not { } music || Shell.Audio is null || !Shell.Audio.Mixer.IsPlaying(music.Handle);
        if (musicFinished && _music is { } finished && _musicEndPosition is null)
        {
            _musicEndPosition = Shell.Audio!.GetPosition(finished);
            _afterMusic.Restart();
        }
        var chartFinished = _charts.Length != 0
            && elapsed >= (_courseEndsAt ?? _charts.Max(TaikoResultBanner.Time)) + TaikoResultBanner.ShutterDelay;
        if ((!input.Escape || Shell.Arcade.Home) && _shutterStartTick < 0
            && (_skipToShutter || chartFinished && !Shell.Gameplay.OverlayActive))
        {
            overlay.Clear();
            if (!musicFinished && _music is { } outro)
                Shell.Audio?.Mixer.Stop(outro.Handle, TimeSpan.FromSeconds(1));
            // Traced (5 Waiwai runs): Waiwai never closes the shutter; its results cut in.
            if (Shell.Gameplay.WaiwaiOutcome is null)
                overlay.Show(_course is null ? FlowScenes.Shutter : FlowScenes.DanShutter);
            // Traced sessions 30 and 31, as the course's shutter closes.
            if (_course is not null && Shell.Sounds is { } sounds)
            {
                sounds.Frontend.PlayCue("VO_RESULT", 19);
                sounds.Frontend.PlayCue("SE_GAME", 15);
            }
            _shutterStartTick = Shell.Tick;
            _shutterClosing = false;
        }
        // Close registers on the shutter's first frame; retry until it exists.
        if (_shutterStartTick >= 0 && !_shutterClosing && overlay.Player is { } shutter)
            // Close(side): the shutter takes the player's colour (traced Close(1) for the right drum's
            // player, Close(2) for two players).
            _shutterClosing = shutter.TryInvokeCallback("Close",
                [LumenHostValue.FromNumber(Shell.Hosts.TwoPlayers ? 2 : Shell.Hosts.PlayerSide)]);
        // ponytail: 70 ticks = traced Close -> results load (1.17 s); the close itself takes ~1 s.
        if (input.Escape && !Shell.Arcade.Home || _shutterStartTick >= 0 && Shell.Tick - _shutterStartTick >= 70)
        {
            // An abandoned song gives its stage back: Song Select counts it again when the next one starts (#21).
            if (input.Escape && !Shell.Arcade.Home && Shell.Players.SongsPlayed > 0)
            {
                var tutorialSeen = Shell.Hosts.TutorialSeen; // back at 0 would offer the tutorial again
                Shell.Players.SongsPlayed--;
                Shell.Hosts.TutorialSeen = tutorialSeen;
            }
            end(finished: !input.Escape || Shell.Arcade.Home);
        }
    }

    // A course's next song: its music, on the course's clock, from under its card.
    private void nextCourseSong(PlayRequest request)
    {
        var course = _course!;
        _courseHold = chartTime();
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _courseSong++;
        _timeline = new GameplayTimeline(course.Songs[_courseSong].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn);
        _musicEndPosition = null;
        _afterMusic.Reset();
        _clock.Restart();
        _music = startAudio(request);
        showCourseSong();
    }

    // Traced session30, at each song's start: between's clip nextsong goes to "start" (its sounds are its own),
    // with the song's title, and song_info shows the song's title, genre and number.
    private void showCourseSong()
    {
        if (_course is null)
            return;
        var song = _courseSongs[_courseSong];
        LumenPlayer? layer(string movie)
        {
            var index = Shell.Active.Layers.ToList().FindIndex(entry => Path.GetFileNameWithoutExtension(entry.Definition.MovieId) == movie);
            return index < 0 ? null : Shell.Active.Player.Layers[index].Player;
        }
        var card = Shell.Titles.GetTransitionTitle(song);
        _ = Shell.Titles.Resolve(card);
        if (layer("between") is { } between)
        {
            between.SetNativeFill("next_song_name", card);
            if (!between.TryGotoLabel("nextsong", "start"))
                Console.Error.WriteLine("between has no nextsong clip to start.");
        }
        Shell.SongInfo = Shell.SongInfo with { Stage = _courseSong + 1 };
        if (layer("song_info") is { } info)
        {
            var title = Shell.Titles.GetGameplayTitle(song);
            _ = Shell.Titles.Resolve(title);
            info.SetNativeFill("song_name", title);
        }
    }

    // A finished song shows its results (the shutter stays over them until their first frames are
    // drawn); Escape goes straight back to Song Select.
    private void end(bool finished, bool show = true)
    {
        _shutterStartTick = -1;
        _skipToShutter = false;
        _review?.Dispose();
        _review = null;
        _training = null;
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _music = null;
        _clock.Reset();
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
            _charts = [];
            _autoplay = null;
            return;
        }
        Shell.Catalog.Replace(dan ? FlowScenes.DaniResults
            : Shell.Gameplay.WaiwaiOutcome is not null ? FlowScenes.WaiwaiResults : FlowScenes.Results);
        if (!finished) _charts = [];
        _autoplay = null;
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
            _courseSong + 1, total, Shell.Gameplay.DanFailed ? 0 : DanCourseResult.LevelOf(plan, total, Shell.Gameplay.DanSongEnds))
            { SongEnds = [.. Shell.Gameplay.DanSongEnds] };
    }

    // Offline too: nothing here waits for the server; it goes up with the next upload.
    private void saveCourse(PlayRequest request, DanCourseResult result, DanCourseTimeline course)
    {
        // Failed runs aren't kept, here or on the server.
        if (result.Level == 0 || Shell.Options.Autoplay || Shell.Sync.Scores is not { } scores
            || TaikoGuest.Profiles[(int)request.Players[0].Player] is not { } profile)
            return;
        var replays = course.Split(Shell.Gameplay.Replays[0]);
        try
        {
            scores.SaveDan(new DanPlayRecord(Guid.NewGuid(), profile.Baid, result,
                [.. course.Authored.Select(static song => (string?)ChartHash.Compute(song.Chart, song.Course))], DateTimeOffset.UtcNow,
                [.. replays.Select(static replay => replay.Encode())])
            {
                AudioOffsetMs = (int)_audioOffset.TotalMilliseconds,
                InputOffsetMs = (int)_inputOffset.TotalMilliseconds,
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

    // Each lane whose drum has a profile saves its play. ponytail: Waiwai (shared voltage, its own
    // results) is not saved until its mode is worked out.
    private void saveScore(PlayRequest request, int remoteLane)
    {
        Array.Clear(Shell.Bests);
        Array.Clear(Shell.Placements);
        if (Shell.Options.Autoplay || _voided || Shell.Sync.Scores is not { } scores || Shell.Gameplay.WaiwaiOutcome is not null)
            return;
        var saved = new List<ScoreProfile>();
        for (var lane = 0; lane < _charts.Length && lane < request.Players.Length; lane++)
        {
            var player = request.Players[lane];
            if (lane == remoteLane)
                continue;
            // Home: a guest's play is kept on this PC (baid 0, never uploaded).
            if ((TaikoGuest.Profiles[(int)player.Player] ?? (Shell.Arcade.Home ? ScoreProfile.LocalGuest : null)) is not { } profile)
                continue;
            try
            {
                var sha = ChartHash.Compute(_sourceCharts[lane], player.Course);
                var id = Guid.NewGuid();
                var shinuchi = player.Options.Shinuchi;
                if (lane < Shell.Bests.Length)
                    Shell.Bests[lane] = scores.PreviousBest(profile.Baid, sha, id, shinuchi) ?? Shell.Gameplay.Results[lane].Score;
                // Failed plays aren't kept, here or on the server; the results still show them.
                if (!Shell.Gameplay.Results[lane].Cleared)
                    continue;
                // Online players only (the guest's baid is never uploaded), from the board song select fetched.
                // ponytail: 真打 plays get no rank-in on the results (song select fetched the normal board only).
                if (lane < Shell.Placements.Length && profile.Baid != ScoreProfile.LocalGuestBaid && _song is not null && !shinuchi
                    && Shell.Hosts.Rankings?.For(_song) is { } boards && (int)player.Course < boards.Length)
                {
                    var top = boards[(int)player.Course] ?? []; // fetched, nobody on it yet
                    var placement = Shell.Placements[lane] = RankingBoard.Place(top, profile.Baid, profile.DisplayName,
                        (int)Shell.Gameplay.Results[lane].Score);
                    if (placement.RankIn >= 0)
                        Shell.Hosts.Rankings.Record(_song, (int)player.Course, placement.Top);
                }
                scores.Save(new PlayRecord(id, profile.Baid, sha,
                    player.Chart, "normal", Shell.Gameplay.Results[lane], DateTimeOffset.UtcNow,
                    Shell.Gameplay.Replays[lane].Encode())
                    {
                        AudioOffsetMs = (int)_audioOffset.TotalMilliseconds,
                        InputOffsetMs = (int)_inputOffset.TotalMilliseconds,
                        Seed = player.Options.Random == TaikoRandom.None ? null : _seed + lane,
                    },
                    ChartUpload.From(_sourceCharts[lane], player.Course, _song?.Descriptor));
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
        if (_course is null || _courseSong + 1 >= _courseSongs.Count || Shell.Audio is not { } audio
            || _courseSongs[_courseSong + 1].Descriptor.AudioAsset is not { } asset)
            return;
        var next = _courseSongs[_courseSong + 1];
        _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(next));
        _ = Shell.Titles.Resolve(Shell.Titles.GetGameplayTitle(next));
        _nextAudio = Task.Run(async () =>
        {
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
        var playing = _course?.Songs[_courseSong];
        if (_nextAudio is { } prepared && Shell.Audio is { } ready)
        {
            _nextAudio = null;
            return ready.PlayTransport(new ScheduledAudioSource(prepared.GetAwaiter().GetResult(),
                _timeline!.AudioStart, _timeline.LeadIn + playing!.Chart.Duration + TimeSpan.FromSeconds(1)));
        }
        var asset = playing is null ? request.AudioAsset : _courseSongs[_courseSong].Descriptor.AudioAsset;
        if (Shell.Audio is not { } audio || asset is not { } audioAsset)
            return null;
        var inputStream = Shell.Assets.OpenReadAsync(audioAsset).AsTask().GetAwaiter().GetResult();
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
                _timeline!.AudioStart, _timeline.LeadIn + (playing?.Chart ?? _charts[0]).Duration + TimeSpan.FromSeconds(1)));
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
        Console.WriteLine($"Loaded gameplay charts: {_charts.Length}.");
        Shell.Gameplay.ReportDiagnostics();
    }
}
