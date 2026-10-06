using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
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
    private PlayableChart[] _charts = [];
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
    private readonly Stopwatch _afterMusic = new();

    /// <summary>The song select -> gameplay rainbow, begun by Song Select and finished here.</summary>
    public RainbowTransitionSequence Rainbow { get; private set; } = new();

    /// <summary>The song title shown in the rainbow.</summary>
    public LumenNativeSurfaceKey? RainbowTitle { get; set; }

    public void ResetRainbow() => Rainbow = new RainbowTransitionSequence();

    private bool revealed => _directStart || Rainbow.State is RainbowTransitionState.Revealing or RainbowTransitionState.Complete;

    /// <summary>The next song's charts (one per player) and layout, for <see cref="Enter"/>.</summary>
    public void Prepare(PlayableChart[] charts, SongSelectSong song, int side, WaiwaiComposition? waiwai)
    {
        _directStart = false;
        _charts = charts;
        _song = song;
        _side = side;
        _waiwai = waiwai;
    }

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Gameplay;

    public override void Enter(SceneId scene)
    {
        var request = Shell.PlayRequests.ActivatePending();
        _lastRequest = request;
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
            ModeLabel.Show(active.Player.Layers[songInfoIndex].Player, modeLabel(ModeText(request)));
        }
        _timeline = new GameplayTimeline(_charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
        _startTick = Shell.Tick;
        _clock.Reset();
        _musicEndPosition = null;
        _afterMusic.Reset();
        _audioOffset = TimeSpan.FromMilliseconds(Shell.Arcade.AudioOffsetMs);
        _inputOffset = TimeSpan.FromMilliseconds(Shell.Arcade.InputOffsetMs);
        _review?.Dispose();
        // Home: a recorded play (a replay picked in song select) or the autoplay is reviewed (pause,
        // scrub, speed) rather than played live; it is never saved.
        _review = !Shell.Arcade.Home || Shell.Headless ? null
            : request.Review is { } recorded && request.Players.Length == 1
                ? reviewOf(request, [recorded.Inputs], chartEnd() + TimeSpan.FromSeconds(2), recorded.InputOffset)
            : request.Training && request.Players.Length == 1
                ? reviewOf(request, [[]], chartEnd() + TimeSpan.FromSeconds(2), _inputOffset)
            : Shell.Options.Autoplay
                ? reviewOf(request, [.. _charts.Select(GameplayAutoplay.InputsFor)], chartEnd() + TimeSpan.FromSeconds(2), _inputOffset)
            : null;
        _training = request.Training && _review is not null ? new TrainingSession(Shell, _review, _charts[0]) : null;
        _voided = request.Review is not null || request.Training;
        if (_review is not null && request.Review?.Player is { } watched)
            Shell.WatchAs(_side, watched);
        _autoplay = Shell.Options.Autoplay && _review is null ? new GameplayAutoplay(_charts, _side) : null;
        Shell.Gameplay.Start(_charts, active, [.. request.Players.Select(player => player.Course)], _side, _waiwai, _review?.Timelines);
        _training?.Begin();
        if (Shell.Sounds is { } sounds)
            sounds.Gameplay.RollVoicesOff = [.. request.Players.Select(static player => player.Course >= TaikoCourse.Normal)];
        if (_directStart)
        {
            _music = _review is null ? startAudio(request) : null;
            _clock.Restart();
        }
        Console.WriteLine(
            $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
            + $"{_charts.Sum(static chart => chart.NoteCount)} notes at tick {Shell.Tick}.");
    }

    /// <summary>What a replay or practice says in place of the song's subtitle and number (null: a play).</summary>
    public static string? ModeText(PlayRequest request) =>
        request.Review is { } review ? review.Player is { } player ? Strings.T("mode.replay_of", player.DisplayName) : Strings.T("mode.replay")
        : request.Training ? Strings.T("mode.practice")
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
        Shell.StopWatching();
        _voided = true;
        _review = reviewOf(request, [[]], chartEnd() + TimeSpan.FromSeconds(2), _inputOffset);
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
    private TimeSpan chartTime() => _review is { } review ? revealed ? review.Clock.Position : review.Clock.Start : -_audioOffset + _timeline?.ChartTime(
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

    /// <summary>The reviewed play's clock, for the review bar (null when not reviewing).</summary>
    public ReviewClock? Review => _review?.Clock;

    /// <summary>The training under way (null when not training), for the review bar.</summary>
    public TrainingSession? Training => _training;

    /// <summary>A training attempt is played live: the drum is a drum, not a menu pad.</summary>
    public bool Attempting => Reviewing && _training is { Live: true };

    public bool CanPause => revealed && _shutterStartTick < 0 && !_paused && !Shell.Overlay.IsShown;
    public bool CanQuickRestart => revealed && _shutterStartTick < 0 && !Shell.Overlay.IsShown;

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
        var request = last with { Review = null, Training = false };
        Shell.StopWatching();
        if (_paused) SetPaused(false);
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle);
        _music = null;
        _clock.Reset();
        Shell.Gameplay.Stop();
        Shell.PlayRequests.ClearActive();
        if (!Shell.PlayRequests.TryRequestPlay(request))
            throw new InvalidOperationException("The restarted play request could not be queued.");
        Shell.Coordinator.Flow.CancelPendingTransition();
        Shell.CancelFade();
        ResetRainbow();
        _directStart = true;
        Shell.Show(FlowScenes.Gameplay);
        return true;
    }

    public void Abandon()
    {
        if (_paused) SetPaused(false);
        end(finished: false);
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
                && keys.Presses.Any(static press => press.Key == SdlKeyboardKey.P))
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
            Shell.Gameplay.Advance(_autoplay?.Apply(keys, time) ?? keys, time);
        }
    }

    /// <summary>Home: the song is under way and not already a review, so its pause menu can replay it.</summary>
    public bool CanInstantReplay => Shell.Arcade.Home && _review is null && revealed && _charts.Length == 1 && _waiwai is null
        && Shell.Active.Id == FlowScenes.Gameplay;

    /// <summary>
    /// Turns the song under way into a review of what was played: paused where the player paused, and
    /// it cannot go further. The play is void (never saved); Retry plays the song again.
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
        var review = reviewOf(request, [.. Shell.Gameplay.Replays.Select(static replay => (IReadOnlyList<TaikoReplayInput>)[.. replay.Inputs])], at, _inputOffset);
        review.Clock.Seek(at);
        review.Clock.SetPaused(true);
        review.ShowFresh(at);
        _review = review;
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
        var overlay = Shell.Overlay;
        if (overlay.Player is { } rainbow && Rainbow.ShouldStartReveal(Shell.Tick))
        {
            var request = Shell.PlayRequests.Active
                ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
            _startTick = Shell.Tick;
            _music = _review is null ? startAudio(request) : null; // a review plays its own music
            _clock.Restart();
            rainbow.GotoLabel(RainbowTransitionComposition.RevealLabel, play: true);
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
        if (Shell.Headless)
            Shell.Gameplay.Advance(_autoplay?.Apply(input.Keys, elapsed - _inputOffset) ?? input.Keys, elapsed - _inputOffset);
        if (_music?.Failure is not null || Shell.Audio?.Failure is not null)
            throw new IOException("Gameplay audio failed.", _music?.Failure ?? Shell.Audio?.Failure);
        // Traced: the shutter closes 9 s after the end banner (the audio had ended by then); a longer
        // outro still plays out first.
        var musicFinished = _music is not { } music || Shell.Audio is null || !Shell.Audio.Mixer.IsPlaying(music.Handle);
        if (musicFinished && _music is { } finished && _musicEndPosition is null)
        {
            _musicEndPosition = Shell.Audio!.GetPosition(finished);
            _afterMusic.Restart();
        }
        var chartFinished = _charts.Length != 0
            && elapsed >= _charts.Max(TaikoResultBanner.Time) + TaikoResultBanner.ShutterDelay;
        if ((!input.Escape || Shell.Arcade.Home) && _shutterStartTick < 0 && chartFinished && musicFinished && !Shell.Gameplay.OverlayActive)
        {
            overlay.Clear();
            // Traced (5 Waiwai runs): Waiwai never closes the shutter; its results cut in.
            if (Shell.Gameplay.WaiwaiOutcome is null)
                overlay.Show(FlowScenes.Shutter);
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
            end(finished: !input.Escape || Shell.Arcade.Home);
    }

    // A finished song shows its results (the shutter stays over them until their first frames are
    // drawn); Escape goes straight back to Song Select.
    private void end(bool finished)
    {
        _shutterStartTick = -1;
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
        Shell.Gameplay.Stop();
        if (finished && Shell.PlayRequests.Active is { } played)
            saveScore(played);
        Shell.PlayRequests.ClearActive();
        if (finished)
            Shell.Sounds?.Gameplay.Play(null, GameplaySoundEvent.SongFinished);
        Shell.Catalog.Replace(Shell.Gameplay.WaiwaiOutcome is not null ? FlowScenes.WaiwaiResults : FlowScenes.Results);
        if (!finished) _charts = [];
        _autoplay = null;
        Shell.Show(finished ? FlowScenes.Result : FlowScenes.SongSelect);
        Console.WriteLine($"Gameplay ended at tick {Shell.Tick}; showing {Shell.Active.Id}.");
    }

    // Each lane whose drum has a profile saves its play. ponytail: Waiwai (shared voltage, its own
    // results) is not saved until its mode is worked out.
    private void saveScore(PlayRequest request)
    {
        Array.Clear(Shell.Bests);
        Array.Clear(Shell.Placements);
        if (Shell.Options.Autoplay || _voided || Shell.Sync.Scores is not { } scores || Shell.Gameplay.WaiwaiOutcome is not null)
            return;
        var saved = new List<ScoreProfile>();
        for (var lane = 0; lane < _charts.Length && lane < request.Players.Length; lane++)
        {
            var player = request.Players[lane];
            // Home: a guest's play is kept on this PC (baid 0, never uploaded).
            if ((TaikoGuest.Profiles[(int)player.Player] ?? (Shell.Arcade.Home ? ScoreProfile.LocalGuest : null)) is not { } profile)
                continue;
            try
            {
                var sha = ChartHash.Compute(_charts[lane], player.Course);
                var id = Guid.NewGuid();
                if (lane < Shell.Bests.Length)
                    Shell.Bests[lane] = scores.PreviousBest(profile.Baid, sha, id) ?? Shell.Gameplay.Results[lane].Score;
                // Online players only (the guest's baid is never uploaded), from the board song select fetched.
                if (lane < Shell.Placements.Length && profile.Baid != ScoreProfile.LocalGuestBaid && _song is not null
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
                    },
                    ChartUpload.From(_charts[lane], player.Course, _song?.Descriptor));
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
        if (Shell.Audio is not { } audio || request.AudioAsset is not { } audioAsset)
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
                _timeline!.AudioStart, _timeline.LeadIn + _charts[0].Duration + TimeSpan.FromSeconds(1)));
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
