using Waddamburo.Platform.Sdl.Media;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Flow;
using Waddamburo.App.Gameplay;

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

    /// <summary>The menu's artwork, for other panels drawn the same way.</summary>
    private ResumeCountdown? _resume;
    private RenderTextureId[] _resumeTextures = [];
    private readonly string _assetRoot;
    private bool _focused = true;
    private float _lastInterpolation;
    private float _pauseInterpolation;

    public HomeControls(GameShell shell, GameplayFlow gameplay, CalibrationFlow calibration, string fontPath, string assetRoot)
    {
        _calibration = calibration;
        _shell = shell;
        _gameplay = gameplay;
        _audio = new MenuAudio(shell.Audio, shell.Sounds);
        _audio.Apply(shell.Arcade);
        _menu = new HomeMenu(() => shell.Arcade, settings =>
        {
            shell.Arcade = settings;
            _audio.Apply(settings);
            shell.ApplyDisplay();
            shell.ApplyControls();
            shell.ApplyRecording();
        }, save, don => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false));
        HomeMenu.DisplayModes = shell.Headless ? [] : shell.Application.FullscreenModes();
        HomeMenu.LayoutNames = () => PlayerLayouts.Names(gameplay.ModchartFolder);
        HomeMenu.Skins = shell.Skins.Names;
        _assetRoot = assetRoot;
        _restart = new QuickRestart(gameplay.Restart, () => TimeSpan.FromMilliseconds(shell.Arcade.RetryHoldMs));
        _overlay = new HomePauseOverlay(shell.Painter);
    }

    private bool home => _shell.Arcade.Home;

    /// <summary>The Escape menu is up (over a song too: controllers are gamepads in it).</summary>
    public bool MenuOpen => _menu.IsOpen;

    // The Escape menu's Settings: the service menu's home pages, over the darkened game (the menu stays open
    // under it, so the song stays paused and the attract muted). Changes apply as they are made, as on the
    // settings page; the calibration and the bake go to the home menu's own pages.
    private bool _settingsOverlay;

    // The pause choices (and the restart and report prompts shown in their place) in the overlay look: the home menu
    // decides what they are and does what they say (Pick, Back); the overlay draws them and takes the input.
    private OverlayMenu? _pause;
    private OverlayPage? _pausePage, _bakePage, _calibrationPage;
    private HomeMenuAction? _picked;

    // The home menu shows one of its pages in the overlay look: its choices (or a prompt), the texture bake, or the
    // calibration's result (its old settings page is the only one left on its panel).
    private bool overlayPage => _menu.IsOpen && (_menu.ShowsChoices || _menu.Bake is not null || _menu.Calibration is not null);

    // The bake's page: what it is doing, a bar of how far, and Stop (Back once it's over).
    private string bakeMessage()
    {
        if (_menu.Bake is not { } bake)
            return "";
        var fraction = bake.Phase switch
        {
            TextureBake.BakePhase.Scanning => bake.ArchivesTotal == 0 ? 0 : (double)bake.ArchivesScanned / bake.ArchivesTotal,
            TextureBake.BakePhase.Done => 1,
            _ => bake.PixelsTotal == 0 ? 1 : (double)bake.PixelsDone / bake.PixelsTotal,
        };
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 30);
        var lines = HomePauseOverlay.bakeText(bake);
        return $"{lines[0]}\n[{new string('#', filled)}{new string('-', 30 - filled)}] {Math.Floor(fraction * 100):0}%\n{lines[1]}";
    }

    private OverlayMenu pause()
    {
        _pause ??= new OverlayMenu(_shell.Application, _shell.Options.ServiceFontPath ?? _shell.Options.FontPath)
        {
            Sound = don => _shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false),
        };
        _pausePage ??= new OverlayPage(() => _menu.Title,
            () => [.. _menu.Rows.Select((label, index) => new OverlayRow(label, Enter: () => _picked = _menu.Pick(index)))],
            Centred: true, Message: () => _menu.Message, Back: () => _picked = _menu.Back());
        _bakePage ??= new OverlayPage(() => _menu.Title,
            () => [new OverlayRow(Strings.T(_menu.Bake?.Running == true ? "menu.stop" : "menu.back"), Enter: () => _picked = _menu.BakeChoice())],
            Centred: true, Message: bakeMessage, Back: () => _picked = _menu.BakeChoice());
        _calibrationPage ??= new OverlayPage(() => _menu.Title,
            () => [new OverlayRow(Strings.T("menu.save"), Enter: () => _picked = _menu.FinishCalibration(true)),
                new OverlayRow(Strings.T("menu.discard"), Enter: () => _picked = _menu.FinishCalibration(false))],
            Centred: true, Message: () => _menu.Calibration is { } calibration
                ? Strings.T("calibration.result", Strings.T("value.ms",
                    calibration.AudioOffsetMs.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture))) : null,
            Back: () => _picked = _menu.FinishCalibration(false));
        var page = _menu.Calibration is not null ? _calibrationPage : _menu.Bake is not null ? _bakePage : _pausePage;
        if (_pause.Page != page)
        {
            _pause.Open(page, home: true);
            if (page == _pausePage)
                _pause.Selection = _menu.Selection;
        }
        return _pause;
    }

    private HomeMenuAction pauseInput(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        var menu = pause();
        _picked = null;
        menu.Tick(keys, held, escape);
        if (_picked is not { } action)
            return HomeMenuAction.None;
        // A prompt opening or closing moves the selection; the menu closing takes the page with it.
        menu.Selection = _menu.Selection;
        if (!_menu.IsOpen)
            menu.Close();
        return action;
    }

    /// <summary>The game under an overlay menu, frosted: dimmed to this so its text reads.</summary>
    public const float BackdropBrightness = 0.42f;

    private void openSettings()
    {
        if (_shell.Service is not { } settings)
            return;
        _settingsOverlay = true;
        settings.Health = _shell.Sync.Health;
        settings.Open(_shell.Arcade, 0, don => _shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false),
            _ => { }, applySettings, _shell.RehashLibrary, _shell.Options.TjaRoot, command, canCalibrate: _menu.CalibrationOffered);

        void command(HomeMenuAction action)
        {
            _settingsOverlay = false;
            switch (action)
            {
                // As the settings page's Calibrate: saved, the menu closes, Song Select covers and the lane opens.
                case HomeMenuAction.Calibrate:
                    save();
                    _menu.Close();
                    _audio.HoldMenuMusic(false);
                    _audio.MuteAttract(false);
                    _calibration.Requested = true;
                    break;
                case HomeMenuAction.RunBenchmark:
                    save();
                    _shell.RequestBenchmark();
                    break;
                case HomeMenuAction.BakeTextures when _shell.Upscale is { } upscale:
                    _menu.ShowBake(_shell.Bake is { Running: true } running ? running
                        : _shell.Bake = TextureBake.Start(upscale, _assetRoot));
                    break;
                default:
                    _menu.SettingsOverlayClosed();
                    if (_pause is not null) _pause.Selection = _menu.Selection;
                    break;
            }
        }
    }

    private void applySettings(ArcadeSettings settings)
    {
        _shell.Arcade = settings;
        _audio.Apply(settings);
        _shell.ApplyDisplay();
        _shell.ApplyControls();
    }

    /// <summary>The menu is capturing a binding: Backspace is that binding, not Escape.</summary>
    public bool CapturingBinding => _shell.Service?.Listening == true;

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
    /// <summary>The menu is open over the attract or its demo play, which go on under it (muted).</summary>
    public bool AttractUnderMenu => _menu.IsOpen && (AttractScenes.Contains(active) || active == FlowScenes.Gameplay && _gameplay.Demo);

    /// <summary>
    /// The service menu resets the game: the menu, its fades and the quick restart are dropped, the menus' music is
    /// let go and the output is back at full volume (a fade to black leaves it silent).
    /// </summary>
    public void Reset()
    {
        _menu.Close();
        _restart.Reset();
        _resume?.Cancel();
        _audio.HoldMenuMusic(false);
        _audio.MuteAttract(false);
        _shell.Audio?.Mixer.FadeOutput(1, TimeSpan.Zero);
    }

    /// <summary>Settings changed elsewhere (the service menu) take effect: volumes, display and controls.</summary>
    public void Apply(ArcadeSettings settings)
    {
        _shell.Arcade = settings;
        _audio.Apply(settings);
        _shell.ApplyDisplay();
        _shell.ApplyControls();
    }

    public bool Tick(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        FollowFocus();
        return _restart.UpdateMenuAction() || resumeCountdown(escape) || calibrationDone() || openMenu(escape)
            || menuInput(keys, held, escape);
    }

    /// <summary>
    /// Fades picture and sound to black, then (sounds stopped) runs <paramref name="action"/> unseen; the
    /// new scene fades in once it draws. Once: a fade under way is kept.
    /// </summary>
    public void FadeThen(Action action)
    {
        if (_restart.MenuPending)
            return;
        _shell.Audio?.Mixer.FadeOutput(0, Home.QuickRestart.MenuFade);
        _restart.FromMenu(() =>
        {
            _shell.Sounds?.StopAll();
            action();
            if (_focused || !_shell.Arcade.MuteInBackground)
                _shell.Audio?.Mixer.FadeOutput(1, Home.QuickRestart.MenuFade);
            return true;
        });
    }

    /// <summary>A switch's held frame is up: the black stays until the new scene draws.</summary>
    public void HoldBlack() => _restart.HoldBlack();

    /// <summary>The screen is black under a restart or the menu's Song Select or Title: a switch goes unseen.</summary>
    public bool Covering => _restart.Covering;

    /// <summary>The quick restart's tick (after the player setup): true when the song restarted.</summary>
    public bool QuickRestart(SdlKeyboardSnapshot held) => _restart.Update(home && (active == FlowScenes.Result
        || active == FlowScenes.Gameplay && _gameplay.CanQuickRestart), held,
        // Holding Q restarts a play only: a replay or practice keeps Q (practice's loop start).
        holdAllowed: !_gameplay.Reviewing);

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
    public RenderFrame Draw(RenderFrame frame)
    {
        if (_overlay is null)
            return frame;
        // The pause menu's own choices take the overlay look; its other pages (the old settings page, the bake, the
        // calibration's result) keep their panel.
        var pauseChoices = overlayPage && !_settingsOverlay;
        var extra = _shell.Unsqueezed((_resume is { Running: true } countdown
                ? SceneTextures.Compose(countdown.CreateSnapshot(1), _resumeTextures, "Resume", _shell.Titles.Resolve).Quads
                : [])
            .Concat(_settingsOverlay && _shell.Service is { } settings ? settings.Quads()
                : pauseChoices ? pause().Quads() : [])
            .Concat(_overlay.Quads(_restart.Black,
                _menu.IsOpen || active != FlowScenes.Calibration ? null : _calibration.Calibration)), centred: true)
            .ToList();
        // Nothing on top (most frames): keep the frame rather than copying all its quads.
        if (extra.Count == 0)
            return frame;
        // The settings overlay and the pause choices frost the game under them.
        return new RenderFrame(frame.ClearColor, frame.Quads.Concat(extra), frame.ContentAspectRatio)
        {
            // (Or what the frame frosted already: the online lobby's Song Select.)
            BlurredQuads = _settingsOverlay || pauseChoices ? frame.Quads.Length : frame.BlurredQuads,
            BackdropBrightness = BackdropBrightness,
        };
    }

    public void Dispose()
    {
        _pause?.Dispose();
        SceneTextures.Release(_shell.Application, _resumeTextures);
        _resume?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Going to the background: the sound fades out (when set); a home song pauses (openMenu).
    /// <summary>
    /// Every tick, whoever takes it: the menus' volume follows the scene, and the output fades as the
    /// window loses or regains focus (mute in background).
    /// </summary>
    public void FollowFocus()
    {
        _audio.Scope(AttractScenes.Contains(active) || active == FlowScenes.Gameplay && _gameplay.Demo ? MenuVolumeScope.Attract
            : active == FlowScenes.Entry || active == FlowScenes.SongSelect || active == FlowScenes.DaniSelect
                ? MenuVolumeScope.Menus : MenuVolumeScope.Song);
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
            _menu.Open(gameplay: true, replayable: _gameplay.CanInstantReplay, reviewing: _gameplay.Reviewing, reportable: _gameplay.CanReport, noResume: _gameplay.InCourse,
                inRoom: _shell.Match.InSong, layout: _gameplay.CanEditEffects);
        }
        else if (countdown.Advance((bank, cue) => _shell.Sounds?.Bank.Play(bank, cue, trace: false)))
            resumeSong();
        return true;
    }

    // Escape opens the menu: settings and back to the title in the attract, the entry and Song Select (a
    // cabinet's operator too); home, a song pauses under it. A home song still unpausable when the window
    // lost focus (under the rainbow) pauses once it can; once every note and long note is over (the song's
    // tail), leaving the window no longer pauses. A cabinet's song keeps Escape (it ends the song). A dan
    // course only pauses on Escape: its pause can't be resumed, which leaving the window shouldn't cost.
    private bool openMenu(bool escape)
    {
        // The effects workspace owns its own exit/unsaved prompt; Space still controls practice playback.
        if (_shell.EffectsEditorOpen) return false;
        // Online, the song goes on whatever this player does: leaving the window doesn't stop it.
        var online = _shell.Match.InSong && active == FlowScenes.Gameplay;
        if (_menu.IsOpen || !(escape || home && !online && !_focused && active == FlowScenes.Gameplay && !_gameplay.ChartOver && !_gameplay.InCourse))
            return false;
        // The attract's demo play: like an online song, it goes on muted under the attract's menu.
        if (home && active == FlowScenes.Gameplay && _gameplay.Demo)
        {
            // Leaving the window doesn't stop a demo; while it fades out, Escape waits for the logo.
            if (!escape || _shell.Scenes.FadePending)
                return escape;
            _pauseInterpolation = _lastInterpolation;
            _menu.Open(gameplay: false, attract: true, quit: true);
            _audio.MuteAttract(true);
            return true;
        }
        if (active == FlowScenes.Gameplay)
        {
            if (!home)
                return false;
            if (_restart.Black != 0 || !_gameplay.CanPause)
                return false;
            // The notes are over (the banner's up): nothing to pause, Escape goes on to the results.
            if (escape && _gameplay.SkipToShutter())
                return true;
            _restart.CancelHold();
            _pauseInterpolation = _lastInterpolation;
            // Online, Escape opens the menu over a song that keeps playing.
            if (!online)
                _gameplay.SetPaused(true, _pauseInterpolation);
            _menu.Open(gameplay: true, replayable: _gameplay.CanInstantReplay, reviewing: _gameplay.Reviewing, reportable: _gameplay.CanReport, noResume: _gameplay.InCourse,
                inRoom: _shell.Match.InSong, layout: _gameplay.CanEditEffects);
            return true;
        }
        // The attract, the entry (and its player setup) and Song Select: settings, back to the title.
        if (!AttractScenes.Contains(active) && active != FlowScenes.Entry && active != FlowScenes.SongSelect)
            return false;
        if (_shell.Overlay.IsShown || _shell.Coordinator.Flow.State == GameFlowState.TransitionPending || _restart.Black != 0)
            return true;
        _pauseInterpolation = _lastInterpolation;
        _menu.Open(gameplay: false, attract: AttractScenes.Contains(active), songSelect: active == FlowScenes.SongSelect,
            inRoom: active == FlowScenes.SongSelect && _shell.Room.Session is not null, quit: home);
        // The attract goes on muted under the menu; the entry's and Song Select's music hold.
        if (AttractScenes.Contains(active))
            _audio.MuteAttract(true);
        else
            _audio.HoldMenuMusic(true);
        return true;
    }

    // The open menu takes the input and carries out its choice.
    private bool menuInput(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        if (!_menu.IsOpen)
            return false;
        // The settings overlay takes the input; closed, the pause choices are back.
        if (_settingsOverlay && _shell.Service is { } settings)
        {
            settings.Tick(keys, held, escape);
            if (!settings.IsOpen && _settingsOverlay)
            {
                _settingsOverlay = false;
                _menu.SettingsOverlayClosed();
                if (_pause is not null) _pause.Selection = _menu.Selection;
            }
            return true;
        }
        var action = pauseInput(keys, held, escape);
        if (!_menu.IsOpen)
        {
            _audio.HoldMenuMusic(false);
            _audio.MuteAttract(false);
        }
        switch (action)
        {
            case HomeMenuAction.Settings:
                openSettings();
                break;
            // A reviewed play carries on at once: there is nothing to get ready for.
            case HomeMenuAction.Resume when active == FlowScenes.Gameplay && !_shell.Match.InSong && _resume is { } resume && !_gameplay.Reviewing
                && !_gameplay.Demo:
                resume.Start();
                break;
            case HomeMenuAction.Resume:
                resumeSong();
                break;
            case HomeMenuAction.InstantReplay when active == FlowScenes.Gameplay:
                _gameplay.StartInstantReplay();
                break;
            case HomeMenuAction.UseLayout or HomeMenuAction.EditLayout or HomeMenuAction.NewLayout or HomeMenuAction.RemoveLayout
                when active == FlowScenes.Gameplay:
                layoutAction(action, _menu.LayoutName);
                break;
            case HomeMenuAction.Report when active == FlowScenes.Gameplay:
                _gameplay.Report(_menu.ReportReason);
                resumeSong();
                break;
            case HomeMenuAction.Restart:
                resumeSong();
                _restart.FromMenu();
                break;
            // Out of a song: fade to black like Restart (the switch, and its memory clean-up, happen unseen).
            case HomeMenuAction.SongSelect when active == FlowScenes.Gameplay && _gameplay.CanQuickRestart:
                resumeSong();
                FadeThen(_gameplay.Abandon);
                break;
            case HomeMenuAction.SongSelect:
                resumeSong();
                _gameplay.Abandon();
                break;
            // Mid-song: the song ends for this player (the other is told the room is gone) and Song Select comes back.
            case HomeMenuAction.LeaveRoom when active == FlowScenes.Gameplay:
                resumeSong();
                _gameplay.Abandon();
                _shell.Room.Leave();
                break;
            // Song Select lays itself out again once the partner's gone (RoomOverlay.showPartner).
            case HomeMenuAction.LeaveRoom:
                _shell.Room.Leave();
                _shell.Hosts.ShowOnlineSong(null);
                break;
            case HomeMenuAction.Title:
                _shell.ClosePlayerSetup();
                FadeThen(() => _shell.GoToAttract());
                break;
            case HomeMenuAction.Calibrate:
                _calibration.Requested = true;
                break;
            case HomeMenuAction.EndCalibration:
                _calibration.Leave();
                break;
            case HomeMenuAction.Quit:
                _shell.Application.RequestQuit();
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

    public bool CloseForEffectsEditor()
    {
        if (_settingsOverlay || _menu.Bake is not null || _menu.Calibration is not null) return false;
        _menu.Close(); _pause?.Close(); _resume?.Cancel();
        _audio.HoldMenuMusic(false); _audio.MuteAttract(false);
        resumeSong();
        return true;
    }

    // A folder added to the official songs: another release's game data, or a Nijiiro installation; anything else is
    // said and left out.
    /// <summary>
    /// A folder added to the official songs: another release's game data, or a Nijiiro installation; anything else
    /// (or a Nijiiro one it can't read) is told to <paramref name="warn"/> and left out.
    /// </summary>
    public static ArcadeSettings AddOfficialFolder(ArcadeSettings arcade, string folder, Action<string> warn)
    {
        if (Waddamburo.Providers.Stock.StockCatalogProvider.FindDataRoot(folder) is not null)
            return arcade with { StockFolders = ArcadeSettings.JoinFolders([.. ArcadeSettings.Folders(arcade.StockFolders), folder]) };
        if (new Waddamburo.Providers.Stock.NijiiroCatalogProvider(folder) is { Exists: true } installation)
        {
            // One it can't read (a plain dump's loader has no keys) is said, and not added.
            if (HomeMenu.NijiiroProblemText(installation) is { } problem)
            {
                warn(problem);
                return arcade;
            }
            return arcade with { NijiiroFolder = ArcadeSettings.JoinFolders([.. ArcadeSettings.Folders(arcade.NijiiroFolder), folder]) };
        }
        warn(Strings.T("library.no_official"));
        return arcade;
    }

    // A layout picked in the pause menu: the active one changes (saved with the settings), the song takes it at once,
    // and Edit/New open the layout editor on it.
    private void layoutAction(HomeMenuAction action, string? name)
    {
        var folder = _gameplay.ModchartFolder;
        try
        {
            if (action == HomeMenuAction.NewLayout) name = PlayerLayouts.Create(folder);
            if (action == HomeMenuAction.RemoveLayout && name is not null) PlayerLayouts.Remove(folder, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Layout not changed: {exception.Message}");
            resumeSong();
            return;
        }
        var layout = action == HomeMenuAction.RemoveLayout ? name == _shell.Arcade.Layout ? null : _shell.Arcade.Layout : name;
        _shell.Arcade = _shell.Arcade with { Layout = layout };
        save();
        _gameplay.ReapplyLayout();
        resumeSong();
        if (action is HomeMenuAction.EditLayout or HomeMenuAction.NewLayout)
            _shell.OpenEffectsEditor(layout: true);
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
