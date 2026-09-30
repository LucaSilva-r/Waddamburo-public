using Waddamburo.Platform.Sdl.Media;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Flow;

namespace Waddamburo.App.Home;

/// <summary>
/// Home mode's controls over the running game: Escape's menu (pause in a song; settings and back to the
/// title in the menus), the countdown back into a paused song, the quick restart, the settings' folder
/// picker, and a scene frozen between two ticks while paused. Also, in any mode, the sound fading out
/// while the window is in the background.
/// </summary>
internal sealed class HomeControls : IDisposable
{
    private static readonly HashSet<SceneId> AttractScenes =
        [FlowScenes.Logo, FlowScenes.Title, FlowScenes.Caution, FlowScenes.Movie];

    private readonly GameShell _shell;
    private readonly GameplayFlow _gameplay;
    private readonly CalibrationFlow _calibration;
    private readonly HomeMenu _menu;
    private readonly MenuAudio _audio;
    private readonly QuickRestart _restart;
    private readonly HomePauseOverlay? _overlay;
    private ResumeCountdown? _resume;
    private RenderTextureId[] _resumeTextures = [];
    private HomeMenuAction? _pickingFolder;
    private readonly string _assetRoot;
    private bool _focused = true;
    private float _lastInterpolation;
    private float _pauseInterpolation;

    public HomeControls(GameShell shell, GameplayFlow gameplay, CalibrationFlow calibration, string fontPath, string assetRoot)
    {
        _calibration = calibration;
        _shell = shell;
        _gameplay = gameplay;
        _audio = new MenuAudio(shell.Audio, shell.Sounds, shell.SongCatalog, shell.Assets);
        _audio.Apply(shell.Arcade);
        _menu = new HomeMenu(() => shell.Arcade, settings =>
        {
            shell.Arcade = settings;
            _audio.Apply(settings);
            shell.ApplyDisplay();
            shell.ApplyControls();
        }, save, don => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false),
            shell.Options.TjaRoot, () => shell.Upscale?.CachedCount, shell.Application);
        HomeMenu.DisplayModes = shell.Headless ? [] : shell.Application.FullscreenModes();
        _assetRoot = assetRoot;
        _restart = new QuickRestart(gameplay.Restart);
        _overlay = shell.Arcade.Home
            ? new HomePauseOverlay(shell.Application, fontPath, assetRoot, shell.Upscale, shell.Arcade.UpscaleTextures) : null;
    }

    private bool home => _shell.Arcade.Home;

    /// <summary>The Escape menu is up (over a song too: controllers are gamepads in it).</summary>
    public bool MenuOpen => _menu.IsOpen;

    private SceneId active => _shell.Active.Id;

    /// <summary>Home: loads the resume countdown drawn over a song coming out of pause.</summary>
    public void LoadResume(LumenGameSceneLoader loader)
    {
        if (!home)
            return;
        _resume = new ResumeCountdown((LumenGameSceneInstance)loader
            .LoadAsync(ResumeCountdown.Definition(new SceneId("resume-countdown")), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult());
        _resumeTextures = SceneTextures.Upload(_shell.Application, _resume.Scene);
    }

    /// <summary>
    /// One tick, before the scene: focus, the resume countdown, opening the menu, the open menu's input,
    /// then the quick restart. True: the controls took the tick (the scene does not advance).
    /// </summary>
    public bool Tick(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        followFocus();
        return resumeCountdown(escape) || calibrationDone() || openMenu(escape) || menuInput(keys, held, escape);
    }

    /// <summary>The quick restart's tick (after the player setup): true when the song restarted.</summary>
    public bool QuickRestart(SdlKeyboardSnapshot held) => _restart.Update(home && (active == FlowScenes.Result
        || active == FlowScenes.Gameplay && _gameplay.CanQuickRestart), held);

    /// <summary>
    /// The results wait while a quick restart is being held or was asked for from the menu
    /// (<paramref name="switching"/>: the results asked for their next scene).
    /// </summary>
    public bool HoldsResults(SdlKeyboardSnapshot held, bool switching) => home && active == FlowScenes.Result
        && (held.IsDown(SdlKeyboardKey.Q) || (switching ? _restart.Holding : _restart.MenuPending));

    /// <summary>The frame's blend between ticks: frozen where it stopped while the menu or countdown is up.</summary>
    public float Interpolation(double fraction)
    {
        if (_menu.IsOpen || _resume?.Running == true)
            return _pauseInterpolation;
        return _lastInterpolation = (float)fraction;
    }

    /// <summary>The resume countdown, the menu and the quick restart's black over a frame (home only).</summary>
    public RenderFrame Draw(RenderFrame frame) => _overlay is null
        ? frame : new RenderFrame(frame.ClearColor,
            frame.Quads.Concat(_resume is { Running: true } countdown
                    ? SceneTextures.Compose(countdown.CreateSnapshot(1), _resumeTextures, "Resume", _shell.Titles.Resolve).Quads
                    : [])
                .Concat(_overlay.Quads(_menu.IsOpen ? _menu : null, _restart.Black,
                    _menu.IsOpen || active != FlowScenes.Calibration ? null : _calibration.Calibration)),
            frame.ContentAspectRatio);

    public void Dispose()
    {
        _overlay?.Dispose();
        SceneTextures.Release(_shell.Application, _resumeTextures);
        _resume?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Going to the background: the sound fades out (when set); a home song pauses (openMenu).
    private void followFocus()
    {
        if (_shell.Application.Focused == _focused)
            return;
        _focused = _shell.Application.Focused;
        if (_shell.Arcade.MuteInBackground || _focused)
            _shell.Audio?.Mixer.FadeOutput(_focused ? 1 : 0, TimeSpan.FromMilliseconds(300));
    }

    // A finished audio calibration shows its result in the menu (the lane stays under it).
    private bool calibrationDone()
    {
        if (_menu.IsOpen || active != FlowScenes.Calibration
            || _calibration.Calibration is not { Done: true } calibration)
            return false;
        _pauseInterpolation = _lastInterpolation;
        _menu.Open(gameplay: false);
        _menu.Calibration = calibration;
        return true;
    }

    // Resume's countdown: the song stays held until it ends; Escape or leaving the window pauses again.
    private bool resumeCountdown(bool escape)
    {
        if (_resume is not { Running: true } countdown)
            return false;
        if (escape || !_focused)
        {
            countdown.Cancel();
            _menu.Open(gameplay: true);
        }
        else if (countdown.Advance((bank, cue) => _shell.Sounds?.Bank.Play(bank, cue, trace: false)))
            resumeSong();
        return true;
    }

    // Home: Escape opens the menu (pause in gameplay; settings and back to the title in the menus). A song
    // still unpausable when the window lost focus (under the rainbow) pauses once it can; once every note
    // and long note is over (the song's tail), leaving the window no longer pauses.
    private bool openMenu(bool escape)
    {
        if (!home || _menu.IsOpen
            || !(escape || !_focused && active == FlowScenes.Gameplay && !_gameplay.ChartOver))
            return false;
        if (active == FlowScenes.Gameplay)
        {
            if (_restart.Black != 0 || !_gameplay.CanPause)
                return false;
            _restart.CancelHold();
            _pauseInterpolation = _lastInterpolation;
            _gameplay.SetPaused(true, _pauseInterpolation);
            _menu.Open(gameplay: true);
            return true;
        }
        // The attract, the entry (and its player setup) and Song Select: settings, back to the title.
        if (!AttractScenes.Contains(active) && active != FlowScenes.Entry && active != FlowScenes.SongSelect)
            return false;
        if (_shell.Overlay.IsShown || _shell.Coordinator.Flow.State == GameFlowState.TransitionPending)
            return true;
        _pauseInterpolation = _lastInterpolation;
        _menu.Open(gameplay: false, attract: AttractScenes.Contains(active), songSelect: active == FlowScenes.SongSelect);
        _audio.HoldMenuMusic(true);
        return true;
    }

    // The open menu takes the input and carries out its choice.
    private bool menuInput(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        if (!_menu.IsOpen)
            return false;
        // A folder picked in the system dialog opened from the settings.
        if (_pickingFolder is { } picking && SdlApplication.TryTakePickedFolder(out var folder))
        {
            _shell.Arcade = picking == HomeMenuAction.PickTjaFolder ? _shell.Arcade with { TjaFolder = folder }
                : _shell.Arcade with { NijiiroFolder = folder };
            save();
            _pickingFolder = null;
        }
        var action = _menu.Input(keys, escape, held);
        _audio.Sample(_menu.PreviewBus, _shell.Tick);
        if (!_menu.IsOpen)
            _audio.HoldMenuMusic(false);
        switch (action)
        {
            case HomeMenuAction.Resume when active == FlowScenes.Gameplay && _resume is { } resume:
                resume.Start();
                break;
            case HomeMenuAction.Resume:
                resumeSong();
                break;
            case HomeMenuAction.Restart:
                resumeSong();
                _restart.FromMenu();
                break;
            case HomeMenuAction.SongSelect:
                resumeSong();
                _gameplay.Abandon();
                break;
            case HomeMenuAction.PickTjaFolder or HomeMenuAction.PickNijiiroFolder:
                _pickingFolder = action;
                _shell.Application.PickFolder(action == HomeMenuAction.PickTjaFolder
                    ? _shell.Arcade.TjaFolder ?? _shell.Options.TjaRoot : _shell.Arcade.NijiiroFolder);
                break;
            case HomeMenuAction.Title:
                _shell.ClosePlayerSetup();
                _shell.ReturnToAttract();
                break;
            case HomeMenuAction.Calibrate:
                _calibration.Requested = true;
                break;
            case HomeMenuAction.EndCalibration:
                _calibration.Leave();
                break;
            case HomeMenuAction.RestartGame:
                save();
                _shell.RequestRestart();
                break;
            // A bake already running is shown again rather than started twice.
            case HomeMenuAction.BakeTextures when _shell.Upscale is { } upscale:
                _menu.Bake = _shell.Bake is { Running: true } running ? running
                    : _shell.Bake = TextureBake.Start(upscale, _assetRoot);
                break;
        }
        return true;
    }

    private void resumeSong()
    {
        if (active == FlowScenes.Gameplay)
            _gameplay.SetPaused(false);
    }

    private void save()
    {
        if (_shell.Options.ArcadePath is not { } path) return;
        try
        {
            ArcadeSettings.SaveMenuSettings(path, _shell.Arcade);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
        }
    }
}
