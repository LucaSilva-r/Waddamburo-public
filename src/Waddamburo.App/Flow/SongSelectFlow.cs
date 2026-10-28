using Waddamburo.App.Scenes;
using Waddamburo.App.Presentation;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select (normal or Waiwai): the mode-switch folder, and a chosen song's handoff: the rainbow
/// intermission covers the screen (song title in it), the charts load under it, gameplay starts.
/// Also the how-to-play folder's tutorial movie, which returns here.
/// </summary>
internal sealed class SongSelectFlow(GameShell shell, GameplayFlow gameplay, CalibrationFlow calibration) : FlowScene(shell)
{
    private SongPanelTransitionPresentation _panelTransition = new();

    private readonly bool _traceControls = Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";

    public override SdlKeyboardSnapshot MapKeys(SdlKeyboardSnapshot keys)
    {
        if (_traceControls && keys.PressedKeys.Any())
        {
            var traceHost = Shell.Hosts.SongSelect;
            var traceMovie = Shell.Active.Player.Layers[0].Player;
            var traceFrame = traceMovie.InstanceFrame("main/genreSelect_");
            Console.WriteLine($"[song-controls] tick={Shell.Tick} keys={string.Join(',', keys.PressedKeys)} "
                + $"panel={_panelTransition.Active} busy={traceHost?.BrowserFolderBusy} "
                + $"course={traceHost?.CourseSelectSong?.Descriptor.Title.Primary} custom={traceHost?.ChartCarousel is not null} "
                + $"list={traceFrame?.Frame}/{traceFrame?.Playing}");
        }
        if (_panelTransition.Active) return SdlKeyboardSnapshot.Empty;
        if (Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: null } browserHost)
        {
            if (browserHost.BrowserFolderBusy)
            {
                if (keys.IsDown(SdlKeyboardKey.Backspace)) browserHost.CloseBrowserFolder();
                return SdlKeyboardSnapshot.Empty;
            }
            if (keys.PressedKeys.Any(key => key is SdlKeyboardKey.D or SdlKeyboardKey.Z or SdlKeyboardKey.Left or SdlKeyboardKey.Up or SdlKeyboardKey.WheelUp))
                browserHost.SetBrowserScrollDirection(-1);
            else if (keys.PressedKeys.Any(key => key is SdlKeyboardKey.K or SdlKeyboardKey.V or SdlKeyboardKey.Right or SdlKeyboardKey.Down or SdlKeyboardKey.WheelDown))
                browserHost.SetBrowserScrollDirection(1);
            if (keys.PressedKeys.Any(key => key is SdlKeyboardKey.F or SdlKeyboardKey.J
                or SdlKeyboardKey.X or SdlKeyboardKey.C or SdlKeyboardKey.Enter))
                browserHost.BeginBrowserCategorySelection();
            var confirmReturn = browserHost.BrowserOnReturn && keys.PressedKeys.Any(key => key is
                SdlKeyboardKey.F or SdlKeyboardKey.J or SdlKeyboardKey.X or SdlKeyboardKey.C or SdlKeyboardKey.Enter);
            if ((keys.IsDown(SdlKeyboardKey.Backspace) || confirmReturn) && browserHost.CloseBrowserFolder())
                return SdlKeyboardSnapshot.Empty;
        }
        if (Shell.Active.Id == FlowScenes.SongSelect && Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: null }
            && (keys.IsDown(SdlKeyboardKey.Up) || keys.IsDown(SdlKeyboardKey.Down)))
        {
            keys = new SdlKeyboardSnapshot(keys.PressedKeys.Select(key => key switch
                { SdlKeyboardKey.Up => SdlKeyboardKey.Left, SdlKeyboardKey.Down => SdlKeyboardKey.Right, _ => key }),
                keys.Presses.Select(press => press with { Key = press.Key switch
                    { SdlKeyboardKey.Up => SdlKeyboardKey.Left, SdlKeyboardKey.Down => SdlKeyboardKey.Right, _ => press.Key } }), keys.Timestamp);
        }
        if (Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: null, BrowserSongPanel: not null }
            && Shell.Active.Player.Layers[0].Player.InstanceFrame("main/genreSelect_") is { Playing: false }
            && (keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J) || keys.IsDown(SdlKeyboardKey.Enter)
                || keys.IsDown(SdlKeyboardKey.X) || keys.IsDown(SdlKeyboardKey.C)))
            _panelTransition.Begin(Shell.Active.Player.Layers[0].Player.InstanceFrame("main/genreSelect_")?.Frame);
        if (Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: not null, ChartCarousel: null, LeavingCourseSelect: false } nativeHost
            && ((keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J) || keys.IsDown(SdlKeyboardKey.Enter)) && nativeHost.ChartReturnSelected(0)
                || (keys.IsDown(SdlKeyboardKey.X) || keys.IsDown(SdlKeyboardKey.C) || keys.IsDown(SdlKeyboardKey.Enter)) && nativeHost.ChartReturnSelected(1)))
        {
            _panelTransition.BeginReturn();
            nativeHost.CancelChartSelection();
            return SdlKeyboardSnapshot.Empty;
        }
        if (Shell.Active.Id != FlowScenes.SongSelect || Shell.Hosts.SongSelect is not { ChartCarousel: { } choices } host)
            return keys;
        if (!host.ChartCarouselReady || host.LeavingCourseSelect) return SdlKeyboardSnapshot.Empty;
        if (Shell.PlayRequests.Pending is not null) return SdlKeyboardSnapshot.Empty;
        host.RefreshChartUtilities();
        return ChartCarouselControls.Route(keys, choices, host.ChartReturnSelected,
            cancel: () =>
            {
                _panelTransition.BeginReturn();
                if (!host.CancelChartSelection())
                {
                    Shell.ChartCards.Close();
                    Shell.Reload.Start(static () => { });
                }
            },
            undo: host.UndoChartConfirmations,
            confirm: side =>
            {
                Console.WriteLine($"Chart selected P{side + 1}: {choices.Choice(side).DifficultyName} [{choices.Choice(side).Key}]");
                host.ConfirmChart(side);
            });
    }

    public override LumenRenderSnapshot CreateSnapshot(float interpolation)
    {
        if (Shell.Hosts.SongSelect is not { } host) return base.CreateSnapshot(interpolation);
        var custom = host.ChartCarouselReady && host.ChartCarousel is not null;
        if (!host.VerticalLayout && !custom)
            return SongTitleMarqueePresentation.Compose(base.CreateSnapshot(interpolation), host, interpolation, scrollTitles: false);
        var index = Shell.Active.Layers.ToList().FindIndex(layer => layer.Content.Name.EndsWith("song_select.lm", StringComparison.Ordinal));
        if (index < 0) return base.CreateSnapshot(interpolation);
        var layer = Shell.Active.Player.Layers[index];
        if (host.VerticalLayout)
            SongBrowserFramePresentation.Register(layer.Player, Shell.Active.Layers[index].Content);
        var excluded = new List<string>();
        if (custom) excluded.AddRange(ChartCarouselPresentation.OriginalBoards.Where(path =>
            path != "courseSelect_/cursor_1p_" || !host.ChartCarousel!.Sides.Contains(0) || !host.ChartCarousel.IsUtility(0))
            .Where(path => path != "courseSelect_/cursor_2p_" || !host.ChartCarousel!.Sides.Contains(1) || !host.ChartCarousel.IsUtility(1)));
        if (host.VerticalLayout && _panelTransition.Active) excluded.AddRange(SongPanelTransitionPresentation.Excluded);
        else if (host.VerticalLayout && host.CourseSelectSong is null)
            excluded.Add("main/genreSelect_");
        var snapshot = Shell.Active.Player.CreateRenderSnapshot(interpolation,
            excludedInstances: player => ReferenceEquals(player, layer.Player) ? excluded : null);
        var cards = custom ? Shell.ChartCards.Snapshot(host.ChartCarousel!, layer, Shell.Active.Layers[index].Content,
            interpolation, host.VerticalLayout) : null;
        if (!host.VerticalLayout)
            return SongTitleMarqueePresentation.Compose(
                SongPanelTransitionPresentation.InsertCourseCards(snapshot, cards!, layer, excluded, interpolation),
                host, interpolation, scrollTitles: false);
        if (_panelTransition.Active)
            return SongBrowserFramePresentation.ScaleCourseMarkers(
                _panelTransition.Compose(snapshot, layer, interpolation, cards, courseReady(layer.Player), host), layer);
        if (host.CourseSelectSong is not null)
            snapshot = SongBrowserFramePresentation.ComposeCourse(snapshot, layer, interpolation);
        _panelTransition.Capture(layer, interpolation, host, cards);
        if (host.CourseSelectSong is null)
        {
            var source = SongPanelTransitionPresentation.BrowserSnapshot(layer, interpolation, host, scrollTitles: false);
            var browser = SongTitleMarqueePresentation.Compose(source, host, interpolation);
            _panelTransition.CaptureBrowser(browser, layer, interpolation, source);
            return SongPanelTransitionPresentation.InsertBrowser(snapshot, browser, layer, interpolation);
        }
        if (cards is null) return SongBrowserFramePresentation.ScaleCourseMarkers(
            SongTitleMarqueePresentation.Compose(snapshot, host, interpolation), layer);
        return SongBrowserFramePresentation.ScaleCourseMarkers(SongTitleMarqueePresentation.Compose(
            SongPanelTransitionPresentation.InsertCourseCards(snapshot, cards, layer, excluded, interpolation), host, interpolation), layer);
    }

    private static bool courseReady(LumenPlayer player) => player.InstanceFrame("main") is { } frame
        && player.TryGetLabelFrame("main", "course", out var at) && frame.Frame == at;

    public override IndicatorScene IndicatorsFor(SceneId scene) =>
        scene == FlowScenes.Tutorial ? IndicatorScene.Tutorial : IndicatorScene.SongSelect;

    public override void Enter(SceneId scene)
    {
        Shell.StopWatching();
        if (scene == FlowScenes.SongSelect)
            Shell.Previews?.StartBackground(Shell.Hosts.Waiwai);
        else // the tutorial's music (traced: bgm/nub/JINGLE_DOJO opened as it loads)
            Shell.Sounds?.Bank.PlayMusic("Tutorial", "JINGLE_DOJO", loop: true);
    }

    public override void Exit(SceneId scene)
    {
        _panelTransition = new();
        Shell.ChartCards.Close();
        if (scene == FlowScenes.Tutorial)
            Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(300));
    }

    // The gameplay layout picked (skin included) when a song is chosen, its movies decoding and going
    // up to the GPU while the rainbow plays; the rainbow holds covered until they are all ready, as the
    // game does (traced: the song's scene loads under the closed rainbow, out_extra ~60 ms after).
    private sealed record Prefetched(SongKey Song, int Side, bool TwoPlayers, bool Waiwai,
        GameplaySceneComposition.ThemedSkin? Theme, SceneDefinition Scene);
    private Prefetched? _prefetch;
    private Task<Prefetched>? _composing;
    private int? _coveredAt;
    // Covered this long without the prefetch ready: load the rest the slow way.
    private const int CoverHoldLimitTicks = 600;

    // The skin lookup reads its whole archive: off the main thread, like the movies' decoding.
    private void composeGameplay(PlayRequest request)
    {
        var (category, song) = find(request.Song);
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        var twoPlayers = request.Players.Length == 2;
        // Waiwai's fixed layout (its charts only reshape the notes).
        var waiwai = Shell.Hosts.Waiwai && twoPlayers && song.Descriptor.WaiwaiComposition is not null;
        _prefetch = null;
        _composing = Task.Run(() =>
        {
            var theme = waiwai ? null : Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin);
            var scene = waiwai ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
                : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side, twoPlayers);
            return new Prefetched(request.Song, side, twoPlayers, waiwai, theme, scene);
        });
    }

    // The list's fast scrolling (when set) sees the tick's input before the movie and what it did after.
    private DifficultySelectScroll? _difficultyScroll;
    private LumenPlayer? _difficultyPlayer;

    public override void Advance(LumenInputSnapshot input)
    {
        using var trace = StartupWorkTrace.Measure("tick.song-select-advance", Shell.Tick);
        input = Shell.Hosts.SongSelect?.RestoreInitialSong(input) ?? input;
        if (Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: null, Scroll: { } folderScroll })
        {
            input = folderScroll.ConstrainToFolder(input);
        }
        var scroll = Shell.Arcade.FastSongScroll && Shell.Active.Id == FlowScenes.SongSelect ? Shell.Hosts.SongSelect?.Scroll : null;
        input = scroll?.Before(input, listActive: Shell.Hosts.SongSelect?.CourseSelectSong is null,
            keepWheel: Shell.Hosts.SongSelect?.LeavingCourseSelect == true) ?? input;
        var nativeDifficulty = Shell.Arcade.FastSongScroll && !_panelTransition.Active
            && Shell.Hosts.SongSelect is { VerticalLayout: true, CourseSelectSong: not null, ChartCarousel: null, LeavingCourseSelect: false }
            && courseReady(Shell.Active.Player.Layers[0].Player);
        if (nativeDifficulty && Shell.Hosts.SongSelect is { } difficultyHost)
        {
            var player = Shell.Active.Player.Layers[0].Player;
            if (!ReferenceEquals(player, _difficultyPlayer))
            {
                _difficultyPlayer = player;
                _difficultyScroll = new(side => player.CallScriptNumber(
                    "main.courseScene.courseSelect", "GetCursorIndex", null, side));
            }
            input = _difficultyScroll!.Before(input, difficultyHost.NativeChartFocused);
        }
        else { _difficultyScroll = null; _difficultyPlayer = null; }
        if (Shell.Active.Id == FlowScenes.SongSelect && Shell.Hosts.SongSelect is { VerticalLayout: true })
            _panelTransition.AdvanceOpening(Shell.Active.Player.Layers[0].Player);
        base.Advance(input);
        _difficultyScroll?.After();
        scroll?.After();
        using (StartupWorkTrace.Measure("tick.browser-host-poll", Shell.Tick))
            Shell.Hosts.SongSelect?.Poll();
        if (Shell.Active.Id == FlowScenes.SongSelect && Shell.Hosts.SongSelect is { VerticalLayout: true } host)
        {
            var player = Shell.Active.Player.Layers[0].Player;
            var browser = player.InstanceFrame("main/genreSelect_");
            _panelTransition.Advance(host, courseReady(player), browser?.Frame, browser?.Playing == true,
                !_panelTransition.Returning || SongPanelTransitionPresentation.BrowserTitleReady(player));
        }
        if (Shell.Hosts.SongSelect?.ChartCarousel is { } choices) Shell.ChartCards.Advance(choices, Shell.Arcade.FastSongScroll);
    }

    private SongKey? _titled;
    private bool _titleReported;

    public override void Tick(FlowInput input)
    {
        // Traced: the tutorial cuts in and out (its movie fades itself); Song Select comes back
        // without the folder, the cursor on the first genre.
        if (Shell.Active.Id == FlowScenes.Tutorial)
        {
            if (input.Escape || RetryGameHostBinding.IsEnd(Shell.Active.Player.Layers[0].Player))
                showSongSelect();
            return;
        }
        // A second player joins from Song Select (arcade; one player in, the other drum's join paid for or
        // free play): back to the entry, which joins them (traced session28: SE_SELECT 0 as Song Select
        // stops, then the entry with the first player in, SetPrevious(SCENE_SONGSELECT, that drum)), under
        // the plain rainbow a folder change uses (seen on the cabinet).
        if (Shell.JoinedSides.Count == 1 && input.DrumSide is { } joining
            && !Shell.JoinedSides.Contains(joining) && Shell.PlayRequests.Pending is null
            && (Shell.Coins is null || Shell.Coins.Missing(1) == 0))
        {
            if (Shell.Arcade.Home)
            {
                Shell.Hosts.SongSelect?.RememberCurrentSong();
                Shell.JoinPlayer(joining);
                Shell.Sounds?.Bank.Play("SE_SELECT", 0);
                Shell.Reload.Start(static () => { });
                Console.WriteLine($"Player {joining + 1} joined from Song Select; both drums remain active.");
                return;
            }
            Shell.Sounds?.StopAll();
            Shell.Sounds?.Bank.Play("SE_SELECT", 0);
            var first = Shell.JoinedSides.Min;
            Shell.Reload.Start(() =>
            {
                Shell.Hosts.EntryTrigger = joining; // SCENE_TRIGGER_DON_1P / _2P
                Shell.Hosts.EntryRejoined = first;
                if (Shell.Indicators is { } indicators)
                    indicators.Rejoin = true;
            }, FlowScenes.Entry);
            return;
        }
        if (Shell.Hosts.SongSelect?.TutorialRequested == true)
        {
            Shell.Hosts.TutorialSeen = true;
            Shell.Show(FlowScenes.Tutorial);
            return;
        }
        // The rainbow's song title rasterizes in the background (large at 4K): started as the
        // difficulty selector opens, so it is there when the rainbow closes.
        if (Shell.Hosts.SongSelect?.CourseSelectSong is { } picked && _titled != picked.Descriptor.Key)
        {
            _titled = picked.Descriptor.Key;
            _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(picked));
        }
        if (_composing is { IsCompleted: true } composed)
        {
            _composing = null;
            if (composed.IsCompletedSuccessfully)
            {
                _prefetch = composed.Result;
                Shell.Prefetch(_prefetch.Scene);
                Console.WriteLine($"Prefetching gameplay for '{_prefetch.Song}' at tick {Shell.Tick}.");
            }
        }
        // The mode-switch folder: reload as the other song select (normal <-> Waiwai).
        if (Shell.Hosts.SongSelect?.ModeSwitchRequested == true)
        {
            Shell.Hosts.Waiwai = Shell.Arcade.WaiwaiEnabled && !Shell.Hosts.Waiwai;
            showSongSelect();
            return;
        }
        if (switchLibrary() || calibrate())
            return;
        // A Group or Sort spine (the osu!lazer library): the next grouping or order, saved, Song Select
        // reloading on that spine.
        if (Shell.Hosts.SongSelect?.BrowseRequested is { } control)
        {
            Shell.Reload.Start(() =>
            {
                var browse = Shell.Hosts.CycleBrowse(control);
                Shell.Arcade = Shell.Arcade with { OsuBrowse = browse };
                if (Shell.Options.ArcadePath is { } path)
                {
                    try
                    {
                        ArcadeSettings.SaveMenuSettings(path, Shell.Arcade);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
                    }
                }
                Console.WriteLine($"osu!lazer library: group by {SongBrowse.Name(browse.Group)}, sort by {SongBrowse.Name(browse.Sort)}.");
            });
            return;
        }
        // P marks the song under the cursor favourite (or unmarks it); the keys are one-tick pulses.
        if (GameActions.Down(input.Keys, GameAction.Favourite) && Shell.Hosts.SongSelect?.ToggleFavourite() == false)
            Console.WriteLine("Favourite: the cursor is on no song.");
        if (Shell.PlayRequests.Pending is not { } request)
            return;
        var rainbow = gameplay.Rainbow;
        if (rainbow.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete)
        {
            Shell.ReportDiagnostics();
            gameplay.RainbowTitle = Shell.Titles.GetTransitionTitle(find(request.Song).Song, GameplayFlow.ModeText(request));
            _ = Shell.Titles.Resolve(gameplay.RainbowTitle.Value);
            rainbow.Begin(Shell.Tick, Shell.Hosts.Waiwai ? RainbowTransitionSequence.WaiwaiLeadInTicks : null);
            _titleReported = false;
            composeGameplay(request);
            Console.WriteLine($"Queued rainbow cover for '{request.Song}' at tick {Shell.Tick}.");
        }
        if (rainbow.ShouldStartCover(Shell.Tick))
        {
            var player = Shell.Overlay.Show(FlowScenes.Rainbow);
            player.SetNativeFill(
                RainbowTransitionComposition.SongTitleFill,
                gameplay.RainbowTitle ?? throw new InvalidOperationException("Rainbow transition has no song title."));
            player.GotoLabel(RainbowTransitionComposition.CoverLabel, play: true);
            rainbow.StartCover();
            Shell.Audio?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
            Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(20));
            Console.WriteLine($"Rainbow cover started at tick {Shell.Tick}.");
        }
        var titleReady = gameplay.RainbowTitle is { } title && Shell.Titles.Resolve(title) is not null;
        if (titleReady && !_titleReported && rainbow.State is not RainbowTransitionState.Idle)
        {
            _titleReported = true;
            Console.WriteLine($"Rainbow title ready at tick {Shell.Tick}.");
        }
        if (Shell.Overlay.Player is { } cover && rainbow.FinishCoverWhenStopped(cover.IsPlaying, Shell.Tick))
            _coveredAt = Shell.Tick;
        // Held on the song title (shown) until the song's scene is decoded and uploaded (then its switch is quick).
        if (_coveredAt is { } coveredAt && ((titleReady && _prefetch is not null && _composing is null && Shell.PrefetchReady)
                || Shell.Tick - coveredAt >= CoverHoldLimitTicks))
        {
            if (Shell.Tick - coveredAt >= CoverHoldLimitTicks)
                Console.Error.WriteLine("The song's scene was not ready in time; loading the rest now.");
            Console.WriteLine($"Rainbow held covered {Shell.Tick - coveredAt} tick(s) for the song's scene.");
            _coveredAt = null;
            startSong(request);
        }
    }

    // A library folder (home mode): Song Select reloads listing that library.
    private bool switchLibrary()
    {
        if (Shell.Hosts.SongSelect?.LibraryRequested is not { } library)
            return false;
        Shell.Reload.Start(() => Shell.Hosts.Library = library);
        return true;
    }

    // The audio calibration (asked for in the settings): the plain rainbow covers the screen and the
    // music stops (only its clicks should sound), the calibration lane loads under it, and it opens there.
    private bool _coveringForCalibration;

    private bool calibrate()
    {
        if (!_coveringForCalibration)
        {
            if (!calibration.Requested)
                return false;
            _coveringForCalibration = true;
            Shell.Audio?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
            Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(300));
            Shell.Overlay.Show(FlowScenes.Rainbow).GotoLabel(RainbowTransitionComposition.PlainCoverLabel, play: true);
            return true;
        }
        if (Shell.Overlay.Player is { IsPlaying: true })
            return true;
        _coveringForCalibration = false;
        Shell.Catalog.Replace(calibration.Scene());
        Shell.Show(FlowScenes.Calibration);
        Shell.Overlay.Player?.GotoLabel(RainbowTransitionComposition.PlainRevealLabel, play: true);
        return true;
    }

    private void showSongSelect()
    {
        // A launch straight into Song Select joined nobody: keep the player's own board.
        Shell.Catalog.Replace(FlowScenes.SongSelectScene(
            Shell.JoinedSides.Count > 0 ? [.. Shell.JoinedSides] : [Shell.Hosts.PlayerSide], Shell.Hosts.Waiwai));
        Shell.Show(FlowScenes.SongSelect);
    }

    private (string Category, SongSelectSong Song) find(SongKey song) => Shell.SongCatalog.Categories
        .SelectMany(category => category.Songs.Select(entry => (category.Name, entry)))
        .First(entry => entry.entry.Descriptor.Key == song);

    // Under the closed rainbow: load every player's chart and the song's scene, then show gameplay.
    private void startSong(PlayRequest request)
    {
        PlayableChart[] charts;
        try
        {
            charts = [.. request.Players.Select(player => Shell.Assets.LoadChartAsync(player.Chart, player.ChartAsset)
                .AsTask().GetAwaiter().GetResult())];
        }
        catch (Exception exception) when (exception is InvalidDataException
            or NotSupportedException or IOException or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine($"Cannot play selected chart: {exception.Message}");
            Shell.PlayRequests.CancelPending();
            Shell.Overlay.Clear();
            gameplay.ResetRainbow();
            Shell.Show(FlowScenes.SongSelect);
            return;
        }
        var (category, song) = find(request.Song);
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        var twoPlayers = request.Players.Length == 2;
        // Waiwai: both players on the song's Waiwai layout (its duet charts reshaped by section).
        WaiwaiComposition? waiwai = null;
        if (Shell.Hosts.Waiwai && charts.Length == 2 && song.Descriptor.WaiwaiComposition is { } compositionAsset)
        {
            using var compositionStream = Shell.Assets.OpenReadAsync(compositionAsset).AsTask().GetAwaiter().GetResult();
            waiwai = WaiwaiComposition.Parse(compositionStream);
            // A song gets its rare (heart) note by chance (traced 1 of 4 runs, and 2 of 2 earlier).
            // ponytail: waiwaicollabo/00_taiko.xml prob_rare_0 = 16 read as a percent per song.
            var (left, right) = waiwai.Apply(charts[0], charts[1],
                rareNote: Random.Shared.Next(100) < 16
                    || Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RARE") == "1"); // diagnostic: force it
            charts = [left, right];
        }
        var prefetched = _prefetch is { } ready && ready.Song == request.Song && ready.Side == side
            && ready.TwoPlayers == twoPlayers && ready.Waiwai == waiwai is not null ? ready : null;
        _prefetch = null;
        // Every song gets its themed skin or a fresh random original mix.
        var theme = prefetched is not null ? prefetched.Theme : waiwai is not null ? null
            : Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin);
        if (Shell.Sounds?.Gameplay is { } sounds)
        {
            sounds.Waiwai = waiwai is not null;
            sounds.Tones = [.. request.Players.Select(static player => TaikoGuest.Tones[(int)player.Player])];
            sounds.PrepareDrums(request.Players.Length == 2);
        }
        // The credit's song number (reset when a credit ends); a home session's endless credit stays on 1
        // (the counters only have art for songs 1-4).
        var stage = Shell.Arcade.Home ? 1 : ++Shell.SongsPlayed;
        Shell.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), stage,
            Final: !Shell.Arcade.Home && stage >= Shell.Arcade.SongsPerSession);
        Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
        Shell.Catalog.Replace(prefetched?.Scene ?? (waiwai is not null
            ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side,
                twoPlayers: twoPlayers)));
        gameplay.Prepare(charts, song, side, waiwai);
        Shell.Show(FlowScenes.Gameplay);
    }
}
