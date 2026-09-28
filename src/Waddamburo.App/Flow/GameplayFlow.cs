using System.Diagnostics;
using Waddamburo.App.Gameplay;
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
    private int _side;
    private WaiwaiComposition? _waiwai;
    private AudioStreamTransport? _music;
    private GameplayTimeline? _timeline;
    private GameplayAutoplay? _autoplay;
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
        }
        _timeline = new GameplayTimeline(_charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
        _autoplay = Shell.Options.Autoplay ? new GameplayAutoplay(_charts, _side) : null;
        _startTick = Shell.Tick;
        _clock.Reset();
        _musicEndPosition = null;
        _afterMusic.Reset();
        _audioOffset = TimeSpan.FromMilliseconds(Shell.Arcade.AudioOffsetMs);
        _inputOffset = TimeSpan.FromMilliseconds(Shell.Arcade.InputOffsetMs);
        Shell.Gameplay.Start(_charts, active, [.. request.Players.Select(player => player.Course)], _side, _waiwai);
        if (Shell.Sounds is { } sounds)
            sounds.Gameplay.VoicesOff = [.. request.Players.Select(static player => player.Course >= TaikoCourse.Oni)];
        if (_directStart)
        {
            _music = startAudio(request);
            _clock.Restart();
        }
        Console.WriteLine(
            $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
            + $"{_charts.Sum(static chart => chart.NoteCount)} notes at tick {Shell.Tick}.");
    }

    /// <summary>
    /// The chart position: held at zero under the rainbow, then the music's (or the ticks', headless),
    /// less the audio offset.
    /// </summary>
    private TimeSpan chartTime() => -_audioOffset + _timeline?.ChartTime(
        !revealed ? TimeSpan.Zero
        : _musicEndPosition is { } ended ? ended + _afterMusic.Elapsed
        : _music is not null ? Shell.Audio!.GetPosition(_music)
        : Shell.Headless ? TimeSpan.FromSeconds((Shell.Tick - _startTick - _pausedTicks
            - (_paused ? Shell.Tick - _pausedAtTick : 0)) / 60d)
        : _clock.Elapsed) ?? TimeSpan.Zero;

    /// <summary>Every player's last note, roll, balloon and kusudama is over.</summary>
    public bool ChartOver => _charts.Length != 0 && chartTime() >= _charts.Max(static chart =>
        chart.HitObjects.Select(static note => note.StartTime)
            .Concat(chart.LongNotes.Select(static note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max()) + TimeSpan.FromMilliseconds(200);

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

    /// <summary>Abandons the current round and loads the same match again without saving it.</summary>
    public bool Restart()
    {
        if (_lastRequest is not { } request || _charts.Length == 0) return false;
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
        if (!Shell.Headless && revealed && !_paused)
        {
            var time = chartTime() - _inputOffset;
            Shell.Gameplay.Advance(_autoplay?.Apply(keys, time) ?? keys, time);
        }
    }

    public override void Tick(FlowInput input)
    {
        if (_paused) return;
        var overlay = Shell.Overlay;
        if (overlay.Player is { } rainbow && Rainbow.ShouldStartReveal(Shell.Tick))
        {
            var request = Shell.PlayRequests.Active
                ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
            _startTick = Shell.Tick;
            _music = startAudio(request);
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
        if (!revealed)
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
        Array.Clear(Shell.PreviousBests);
        if (Shell.Options.Autoplay || Shell.Scores is not { } scores || Shell.Gameplay.WaiwaiOutcome is not null)
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
                if (lane < Shell.PreviousBests.Length)
                    Shell.PreviousBests[lane] = scores.PreviousBest(profile.Baid, sha, id);
                scores.Save(new PlayRecord(id, profile.Baid, sha,
                    player.Chart, "normal", Shell.Gameplay.Results[lane], DateTimeOffset.UtcNow,
                    Shell.Gameplay.Replays[lane].Encode()),
                    ChartUpload.From(_charts[lane], player.Course, _song?.Descriptor.Title.Primary, _song?.Descriptor.Subtitle));
                saved.Add(profile);
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                // A lost score must not end the credit.
                Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
            }
        }
        foreach (var profile in saved)
            Shell.Upload(profile);
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
