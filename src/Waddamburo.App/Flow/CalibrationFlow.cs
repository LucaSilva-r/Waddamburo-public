using System.Diagnostics;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// The Audio Offset calibration (from Song Select's settings), on a gameplay lane of its own: a don on every
/// beat over a click track, no song, judgement or score. <see cref="LatencyCalibration"/> says what it
/// measures. Song Select covers the screen with the plain rainbow, which opens on it; its result shows in
/// the home menu (<see cref="Calibration"/> done), and saved or not, it goes back to Song Select.
/// </summary>
internal sealed class CalibrationFlow(GameShell shell) : FlowScene(shell)
{
    // Drum tone 018's don (the high BIP, each bar's first beat) and ka (the bap, the other beats).
    private const string ClickBank = "SE_GAME_NEIRO_018_C";
    private const int AccentCue = 0, BeatCue = 1;
    // A big don (both centres this close) confirms: drumming along cannot.
    private static readonly TimeSpan BigHitWindow = TimeSpan.FromMilliseconds(60);

    private AudioStreamTransport? _transport; // plays the click track (the mixer disposes it)
    private GameplayTimeline? _timeline;
    private readonly Stopwatch _clock = new(); // without audio (headless)
    private TimeSpan _lastLeftCentre, _lastRightCentre;
    // The next note to flash (each once: a nudge back does not replay it).
    private int _nextFlash;

    /// <summary>Asked for from Song Select's settings: Song Select covers the screen, then shows this scene.</summary>
    public bool Requested { get; set; }

    /// <summary>The calibration being played, or null.</summary>
    public LatencyCalibration? Calibration { get; private set; }

    /// <summary>The lane's scene, for Song Select to load under its rainbow.</summary>
    public SceneDefinition Scene() =>
        GameplaySceneComposition.Create(FlowScenes.Calibration, Random.Shared, Shell.EnsoLayout, null, 0, twoPlayers: false);

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Gameplay;

    public override void Enter(SceneId scene)
    {
        Requested = false;
        // The lead-in is a whole bar, so the click track's beats and bars (from its frame 0) fall on the chart's.
        _timeline = new GameplayTimeline(TimeSpan.Zero, LatencyCalibration.Beat * 4);
        Shell.Gameplay.Start([LatencyCalibration.Chart()], Shell.Active, [TaikoCourse.Easy]);
        Calibration = new LatencyCalibration(TimeSpan.Zero, Shell.Arcade.AudioOffsetMs);
        _lastLeftCentre = _lastRightCentre = TimeSpan.MinValue;
        _nextFlash = 0;
        _clock.Restart();
        if (Shell.Audio is { } audio)
        {
            // BIP bap bap bap, from a drum tone's cues; plain tones without the sound banks. Only the master
            // volume applies.
            var bank = Shell.Sounds?.Bank;
            var clicks = new ClickTrack(audio.Mixer.Format, LatencyCalibration.Beat,
                bank?.Clip(ClickBank, AccentCue) ?? ClickTrack.Tone(audio.Mixer.Format, 2000),
                bank?.Clip(ClickBank, BeatCue) ?? ClickTrack.Tone(audio.Mixer.Format, 1000));
            clicks.SetAudibleFrom(Calibration.FirstClick + _timeline.LeadIn);
            _transport = audio.PlayTransport(new ScheduledAudioSource(clicks, TimeSpan.Zero, TimeSpan.Zero), AudioBus.Metronome);
        }
        Console.WriteLine($"Audio calibration started at tick {Shell.Tick}.");
    }

    public override void Exit(SceneId scene)
    {
        stopClicks();
        Shell.Gameplay.Stop();
        Calibration = null;
        Console.WriteLine($"Audio calibration ended at tick {Shell.Tick}.");
    }

    /// <summary>Back to Song Select (the result was saved or discarded, or the player left).</summary>
    public void Leave() => Shell.Show(FlowScenes.SongSelect);

    // The clock songs use: the click track's position, less the lead-in.
    private TimeSpan chartTime() => _timeline!.ChartTime(_transport is { } transport && Shell.Audio is { } audio
        ? audio.GetPosition(transport) : _clock.Elapsed);

    public override void Advance(LumenInputSnapshot input)
    {
        var animationFrames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(animationFrames);
    }

    public override void Tick(FlowInput input)
    {
        // Opened by Song Select's plain rainbow: cleared once it has played.
        if (Shell.Overlay.Player is { IsPlaying: false })
            Shell.Overlay.Clear();
        // Escape leaves it (nothing to pause: it can simply be started again).
        if (input.Escape)
            Leave();
    }

    // The lane is drawn at the offset being tried (the judgement clock goes on unshifted: its time must not
    // step back when the offset is nudged).
    public override LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        Shell.Gameplay.CreateSnapshot(chartTime() - shift(), interpolation);

    private TimeSpan shift() => TimeSpan.FromMilliseconds(Calibration?.AudioOffsetMs ?? 0);

    // Each note flashes the target's hit ring and leaves the lane as it reaches the circle as drawn (a quick
    // flash is easier to match with a click than a sliding note; no judgement is shown). From the first click
    // on (the player may still be drumming as the lane opens), the rims move the notes earlier / later and a
    // big don or Enter confirms.
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        if (Calibration is not { Done: false } calibration)
            return;
        var time = chartTime();
        Shell.Gameplay.Advance(SdlKeyboardSnapshot.Empty, time);
        if (LatencyCalibration.LastNoteBy(time - shift()) is var reached && reached >= _nextFlash)
        {
            _nextFlash = reached + 1;
            Shell.Gameplay.RemoveNote(reached);
            Shell.Gameplay.FlashTarget();
        }
        if (time < calibration.FirstClick)
            return;
        foreach (var press in keys.Presses)
        {
            // The keyboard's arrows nudge too.
            switch (press.Key == SdlKeyboardKey.Left ? Drum.LeftRim : press.Key == SdlKeyboardKey.Right ? Drum.RightRim
                : drum(press.Key))
            {
                case Drum.LeftRim: calibration.Nudge(-1); break;
                case Drum.RightRim: calibration.Nudge(1); break;
                case Drum.LeftCentre: _lastLeftCentre = press.Timestamp; break;
                case Drum.RightCentre: _lastRightCentre = press.Timestamp; break;
            }
            var bigDon = _lastLeftCentre != TimeSpan.MinValue && _lastRightCentre != TimeSpan.MinValue
                && (_lastLeftCentre - _lastRightCentre).Duration() <= BigHitWindow;
            if (!bigDon && press.Key != SdlKeyboardKey.Enter)
                continue;
            calibration.Confirm();
            stopClicks();
            return;
        }
    }

    private enum Drum { LeftRim, LeftCentre, RightCentre, RightRim }

    // Either drum: left D/F/J/K, right Z/X/C/V.
    private static Drum? drum(SdlKeyboardKey key) => key switch
    {
        SdlKeyboardKey.D or SdlKeyboardKey.Z => Drum.LeftRim,
        SdlKeyboardKey.F or SdlKeyboardKey.X => Drum.LeftCentre,
        SdlKeyboardKey.J or SdlKeyboardKey.C => Drum.RightCentre,
        SdlKeyboardKey.K or SdlKeyboardKey.V => Drum.RightRim,
        _ => null,
    };

    private void stopClicks()
    {
        if (_transport is { } transport)
            Shell.Audio?.Mixer.Stop(transport.Handle);
        _transport = null;
    }
}
