using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select (normal or Waiwai): the mode-switch folder, and a chosen song's handoff: the rainbow
/// intermission covers the screen (song title in it), the charts load under it, gameplay starts.
/// Also the how-to-play folder's tutorial movie, which returns here.
/// </summary>
internal sealed class SongSelectFlow(GameShell shell, GameplayFlow gameplay, CalibrationFlow calibration) : FlowScene(shell)
{
    public override IndicatorScene IndicatorsFor(SceneId scene) =>
        scene == FlowScenes.Tutorial ? IndicatorScene.Tutorial
            : scene == FlowScenes.DaniSelect ? IndicatorScene.DaniSelect : IndicatorScene.SongSelect;

    private const string DanMusicCue = "JINGLE_DANI";

    public override void Enter(SceneId scene)
    {
        Shell.Players.StopWatching();
        if (scene == FlowScenes.SongSelect)
            Shell.Previews?.StartBackground(Shell.Hosts.Waiwai, Shell.Hosts.AiBattle);
        else if (scene == FlowScenes.DaniSelect) // traced session30: bgm/nub/JINGLE_DANI opened as it loads
            Shell.Sounds?.Bank.PlayMusic(DanMusicCue, DanMusicCue, loop: true);
        else // the tutorial's music (traced: bgm/nub/JINGLE_DOJO opened as it loads)
            Shell.Sounds?.Bank.PlayMusic("Tutorial", "JINGLE_DOJO", loop: true);
    }

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Tutorial || scene == FlowScenes.DaniSelect)
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
    private Task<AiBattleSession?>? _aiSetup;
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
        var dojo = Shell.Gameplay.Dan is not null;
        _prefetch = null;
        _composing = Task.Run(() =>
        {
            var theme = waiwai ? null : Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin);
            var scene = waiwai ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
                : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side, twoPlayers, dojo);
            return new Prefetched(request.Song, side, twoPlayers, waiwai, theme, scene);
        });
    }

    // The list's fast scrolling (when set) sees the tick's input before the movie and what it did after.
    public override void Advance(LumenInputSnapshot input)
    {
        var scroll = Shell.Arcade.FastSongScroll && Shell.Active.Id == FlowScenes.SongSelect ? Shell.Hosts.SongSelect?.Scroll : null;
        base.Advance(scroll?.Before(input, listActive: Shell.Hosts.SongSelect?.CourseSelectSong is null,
            keepWheel: Shell.Hosts.SongSelect?.LeavingCourseSelect == true) ?? input);
        scroll?.After();
        Shell.Hosts.SongSelect?.Poll();
        if (Shell.Active.Id == FlowScenes.DaniSelect)
            Shell.Hosts.DaniSelect?.Poll();
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
        if (Shell.Active.Id == FlowScenes.DaniSelect)
        {
            if (input.Escape || Shell.Hosts.DaniSelect is { Terminated: true, Back: true })
            {
                Shell.Reload.Start(static () => { }, FlowScenes.SongSelect, FlowScenes.DanExitCover); // traced session31
                return;
            }
            if (Shell.Hosts.DaniSelect is { Terminated: true, Selected: { } board } && Shell.PlayRequests.Pending is null)
                playCourse(Shell.Hosts.DanBoards[board]!);
            if (Shell.PlayRequests.Pending is { } course)
                coverForGameplay(course);
            return;
        }
        // A second player joins from Song Select (arcade; one player in, the other drum's join paid for or
        // free play): back to the entry, which joins them (traced session28: SE_SELECT 0 as Song Select
        // stops, then the entry with the first player in, SetPrevious(SCENE_SONGSELECT, that drum)), under
        // the plain rainbow a folder change uses (seen on the cabinet).
        if (!Shell.Arcade.Home && Shell.Players.JoinedSides.Count == 1 && input.DrumSide is { } joining
            && !Shell.Players.JoinedSides.Contains(joining) && Shell.PlayRequests.Pending is null
            && (Shell.Coins is null || Shell.Coins.Missing(1) == 0))
        {
            // As on the cabinet, also from the difficulty selector; Song Select comes back on that song.
            Shell.Hosts.SongSelect?.RememberCurrentSong();
            Shell.Sounds?.StopAll();
            Shell.Sounds?.Bank.Play("SE_SELECT", 0);
            var first = Shell.Players.JoinedSides.Min;
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
            // Home: once is enough (Green offers it every credit until watched; a PC has one endless credit).
            if (Shell.Arcade.Home && !Shell.Arcade.TutorialSeen)
                Shell.SaveArcade(Shell.Arcade with { TutorialSeen = true });
            Shell.Scenes.Show(FlowScenes.Tutorial);
            return;
        }
        if (Shell.Hosts.SongSelect?.DanRequested == true)
        {
            Shell.Sounds?.Bank.Play("SE_SELECT", 7); // traced session30, as Song Select ends
            // The series spine picked at home; a cabinet has only the one its operator set, and no spines.
            Shell.Hosts.DanSeries = Shell.Hosts.SongSelect.DanSeriesPicked ?? Shell.Arcade.DanSeries;
            Shell.Catalog.Replace(FlowScenes.DaniSelectScene(Shell.Hosts.PlayerSide));
            Shell.Reload.Start(static () => { }, FlowScenes.DaniSelect, FlowScenes.DanCover);
            return;
        }
        // The rainbow's song title rasterizes in the background (large at 4K): started as the
        // difficulty selector opens, so it is there when the rainbow closes.
        if (Shell.Hosts.SongSelect?.CourseSelectSong is { } picked && _titled != picked.Descriptor.Key)
        {
            _titled = picked.Descriptor.Key;
            _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(picked));
        }
        finishComposing();
        // The mode-switch folder: reload as the other song select (normal <-> Waiwai).
        if (Shell.Hosts.SongSelect?.ModeSwitchRequested == true)
        {
            Shell.Hosts.Waiwai = !Shell.Hosts.Waiwai;
            showSongSelect();
            return;
        }
        if (switchLibrary() || calibrate())
            return;
        // A Group or Sort spine (the osu!lazer library): the next grouping or order, saved, Song Select
        // reloading on that spine.
        // The Online spine: the lobby opens over Song Select.
        if (Shell.Hosts.SongSelect?.BrowseRequested == SongBrowseControl.Online)
        {
            Shell.Room.OpenLobby();
            return;
        }
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
        // Online, a guest only plays the host's song.
        if (Shell.PlayRequests.Pending is { } request && Shell.Match.Accept(request))
            coverForGameplay(request);
    }

    // The gameplay layout, composed in the background, starts decoding.
    private void finishComposing()
    {
        if (_composing is not { IsCompleted: true } composed)
            return;
        _composing = null;
        if (composed.IsCompletedSuccessfully)
        {
            _prefetch = composed.Result;
            Shell.Scenes.Prefetch(_prefetch.Scene);
            Console.WriteLine($"Prefetching gameplay for '{_prefetch.Song}' at tick {Shell.Tick}.");
        }
    }

    /// <summary>The rainbow over a pick goes, and Song Select comes back without it (an online host giving up waiting).</summary>
    public void CancelCover()
    {
        AbandonCover();
        Shell.Scenes.Show(FlowScenes.SongSelect);
    }

    /// <summary>
    /// The rainbow over a pick goes with its pending play (back to the title mid-cover too): the next pick starts a fresh
    /// transition. A rainbow left mid-cover made every later pick wait on it, with no song ever starting.
    /// </summary>
    public void AbandonCover()
    {
        Shell.PlayRequests.CancelPending();
        Shell.Overlay.Clear();
        gameplay.ResetRainbow();
        _coveredAt = null;
        _prefetch = null;
        _musicUnderCover = false;
        _aiSetup = null;
    }

    private bool _musicUnderCover;

    private void stopMusic()
    {
        Shell.Previews?.Silence();
        Shell.Audio?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
        Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(20));
    }

    // The rainbow's song title is placed and drawn on the stage (it comes in partway through the cover).
    private static bool titleShown(Waddamburo.Lumen.Runtime.LumenPlayer rainbow) =>
        rainbow.TryGetInstanceBounds(RainbowTransitionComposition.SongTitleFill, out var title)
        && title is { Width: > 0, Height: > 0 } && title.X + title.Width > 0 && title.X < OverlayStageWidth;

    private const float OverlayStageWidth = 1280;

    // The pending play's rainbow, and gameplay under it once it covers the screen.
    private void coverForGameplay(PlayRequest request)
    {
        finishComposing();
        var rainbow = gameplay.Rainbow;
        if (rainbow.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete)
        {
            Shell.ReportDiagnostics();
            gameplay.RainbowTitle = Shell.Titles.GetTransitionTitle(find(request.Song).Song, GameplayFlow.ModeText(request));
            _ = Shell.Titles.Resolve(gameplay.RainbowTitle.Value);
            rainbow.Begin(Shell.Tick, Shell.Hosts.Waiwai ? RainbowTransitionSequence.WaiwaiLeadInTicks : null);
            _titleReported = false;
            composeGameplay(request);
            _aiSetup = null;
            Console.WriteLine($"Queued rainbow cover for '{request.Song}' at tick {Shell.Tick}.");
        }
        if (rainbow.ShouldStartCover(Shell.Tick))
        {
            var player = Shell.Overlay.Show(FlowScenes.Rainbow);
            player.SetNativeFill(
                RainbowTransitionComposition.SongTitleFill,
                gameplay.RainbowTitle ?? throw new InvalidOperationException("Rainbow transition has no song title."));
            // Traced session30: a course's first song opens under the plain rainbow, no title.
            player.GotoLabel(Shell.Gameplay.Dan is null ? RainbowTransitionComposition.CoverLabel
                : RainbowTransitionComposition.PlainCoverLabel, play: true);
            rainbow.StartCover();
            // The song's preview plays on under the closing rainbow and stops as its title appears (#17); a course's
            // plain rainbow has no title: it stops now.
            _musicUnderCover = Shell.Gameplay.Dan is null;
            if (!_musicUnderCover)
                stopMusic();
            Console.WriteLine($"Rainbow cover started at tick {Shell.Tick}.");
        }
        if (_musicUnderCover && (Shell.Overlay.Player is not { } closing || titleShown(closing) || _coveredAt is not null
            || rainbow.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete))
        {
            _musicUnderCover = false;
            stopMusic();
            Console.WriteLine($"Song Select music stopped as the rainbow's title appeared, at tick {Shell.Tick}.");
        }
        var titleReady = gameplay.RainbowTitle is { } title && Shell.Titles.Resolve(title) is not null;
        if (titleReady && !_titleReported && rainbow.State is not RainbowTransitionState.Idle)
        {
            _titleReported = true;
            Console.WriteLine($"Rainbow title ready at tick {Shell.Tick}.");
        }
        if (Shell.Overlay.Player is { } cover && rainbow.FinishCoverWhenStopped(cover.IsPlaying, Shell.Tick))
            _coveredAt = Shell.Tick;
        // Online: held on the song title until the other player has picked, then it plays with them.
        if (_coveredAt is not null)
        {
            if (Shell.Match.Together(request) is not { } together)
                return;
            request = together;
            if (Shell.Hosts.AiBattle)
            {
                _aiSetup ??= prepareBattle(request, Shell.Hosts.SongSelect?.SupportLevel ?? 0,
                    Shell.Hosts.SongSelect?.ShownSupportCounts.ElementAtOrDefault((int)request.Players[0].Course) ?? 0);
                if (!_aiSetup.IsCompleted) return;
            }
        }
        // Held on the song title (shown) until the song's scene is decoded and uploaded (then its switch is quick).
        if (_coveredAt is { } coveredAt && ((titleReady && _prefetch is not null && _composing is null && Shell.Scenes.PrefetchReady)
                || Shell.Tick - coveredAt >= CoverHoldLimitTicks))
        {
            if (Shell.Tick - coveredAt >= CoverHoldLimitTicks)
                Console.Error.WriteLine("The song's scene was not ready in time; loading the rest now.");
            Console.WriteLine($"Rainbow held covered {Shell.Tick - coveredAt} tick(s) for the song's scene.");
            _coveredAt = null;
            startSong(request);
        }
    }

    private async Task<AiBattleSession?> prepareBattle(PlayRequest request, int pick, int offered)
    {
        if (Shell.Autoplay || Shell.DevAutoplay || request.Review is not null) return null;
        var player = request.Players[0];
        try
        {
            var source = await Shell.Assets.LoadChartAsync(player.Chart, player.ChartAsset).ConfigureAwait(false);
            var level = Waddamburo.Game.Lumen.SongSelectHostBinding.SupportPick(ChartSimplifier.LevelOf(player.Course), offered, pick);
            var support = pick > 0 && ChartSimplifier.SourceCourse(level) == player.Course ? level : 0;
            var chart = support == 0 ? source : ChartSimplifier.Simplify(source, support);
            var session = await Shell.Sync.BeginAiBattleAsync(TaikoGuest.Profiles[(int)player.Player],
                ChartHash.Compute(chart, player.Course), ChartHash.Compute(source, player.Course), player.Course,
                player.Options.Bits, support).ConfigureAwait(false);
            if (session?.Ranked != true)
                Shell.Sync.Notices.Add(new Waddamburo.Game.Online.Notice("ai-unranked", Waddamburo.Game.Online.NoticeSource.Local,
                    Waddamburo.Game.Online.NoticeSeverity.Info,
                    Strings.T("notice.ai_unranked"), DateTimeOffset.UtcNow));
            return session;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException
            or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine($"AI battle setup unavailable: {exception.Message}");
            return null;
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
        Shell.Scenes.Show(FlowScenes.Calibration);
        Shell.Overlay.Player?.GotoLabel(RainbowTransitionComposition.PlainRevealLabel, play: true);
        return true;
    }

    private void showSongSelect()
    {
        // A launch straight into Song Select joined nobody: keep the player's own board.
        Shell.Catalog.Replace(FlowScenes.SongSelectScene(
            Shell.Players.JoinedSides.Count > 0 ? [.. Shell.Players.JoinedSides] : [Shell.Hosts.PlayerSide], Shell.Hosts.Waiwai, Shell.Hosts.AiBattle));
        Shell.Scenes.Show(FlowScenes.SongSelect);
    }

    private DanCourseStatus? _course;

    private void playCourse(DanCourseStatus course)
    {
        _course = course;
        var first = course.Songs[0];
        var key = first.Chart!;
        var song = find(key.Song).Song.Descriptor;
        var chart = song.Charts.First(candidate => candidate.Key == key);
        Shell.Gameplay.Dan = course.Course;
        var challenge = Shell.Hosts.DanChallenge = Shell.Hosts.Dan!.ChallengeNumber(Shell.Hosts.DanSeries, course.Course);
        Shell.Gameplay.DanChallengeArt = challenge == 0 ? null : DanChallengeArt.Key(Shell.Hosts.DanSeries, challenge, "mini");
        var side = Shell.Hosts.PlayerSide == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
        Shell.PlayRequests.TryRequestPlay(new PlayRequest(Shell.SongCatalog.Revision, song.Key, song.AudioAsset,
            [new PlayerChartRequest(side, key, chart.ChartAsset, (TaikoCourse)first.Song.Course)]));
    }

    private (string Category, SongSelectSong Song) find(SongKey song) => Shell.SongCatalog.Categories
        .SelectMany(category => category.Songs.Select(entry => (category.Name, entry)))
        .FirstOrDefault(entry => entry.entry.Descriptor.Key == song) is ({ } name, { } found) ? (name, found)
        : ("Online", Shell.FindSong(song) ?? throw new InvalidOperationException($"Song '{song}' is in no library."));

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
            Shell.Scenes.Show(FlowScenes.SongSelect);
            return;
        }
        // A course: all its songs as one chart.
        DanCourseTimeline? timeline = null;
        var courseSongs = new List<SongSelectSong>();
        var courseStatus = _course;
        if (_course is { } picked)
        {
            _course = null;
            var loaded = new List<(PlayableChart, TaikoCourse)>();
            foreach (var entry in picked.Songs)
            {
                var (_, courseSong) = find(entry.Chart!.Song);
                courseSongs.Add(courseSong);
                var descriptor = courseSong.Descriptor.Charts.First(candidate => candidate.Key == entry.Chart);
                loaded.Add((Shell.Assets.LoadChartAsync(entry.Chart, descriptor.ChartAsset).AsTask().GetAwaiter().GetResult(),
                    (TaikoCourse)entry.Song.Course));
            }
            timeline = DanCourseTimeline.Merge(loaded);
            charts = [timeline.Chart];
        }
        var (category, song) = find(request.Song);
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        // Testing online play alone: this player's own hits come back as the second lane's (OnlineEcho).
        if (Gameplay.OnlineEcho.Delay is not null && charts.Length == 1 && timeline is null && !Shell.Hosts.Waiwai)
            charts = [charts[0], charts[0]];
        // AI battle: AI-don plays the same chart on a second lane.
        if (Shell.Hosts.AiBattle && charts.Length == 1 && timeline is null)
            charts = [charts[0], charts[0]];
        var twoPlayers = charts.Length == 2;
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
            sounds.AiBattle = Shell.Hosts.AiBattle && twoPlayers;
            sounds.Tones = [.. request.Players.Select(static player => TaikoGuest.Tones[(int)player.Player])];
            sounds.PrepareDrums(request.Players.Length == 2 || sounds.AiBattle);
        }
        // The credit's song number (reset when a credit ends); a home session's endless credit stays on 1
        // (the counters only have art for songs 1-4).
        var stage = Shell.Arcade.Home ? 1 : ++Shell.Players.SongsPlayed;
        Shell.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), stage,
            Final: !Shell.Arcade.Home && stage >= Shell.Arcade.SongsPerSession);
        Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
        Shell.Catalog.Replace(Shell.Hosts.AiBattle && twoPlayers ? GameplaySceneComposition.CreateAiBattle(FlowScenes.Gameplay, Shell.EnsoLayout)
            : prefetched?.Scene ?? (waiwai is not null
            ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side,
                twoPlayers: twoPlayers, dojo: Shell.Gameplay.Dan is not null)));
        gameplay.Prepare(charts, song, side, waiwai);
        gameplay.AiSession = _aiSetup is { IsCompletedSuccessfully: true } setup ? setup.Result : null;
        _aiSetup = null;
        if (timeline is not null)
            gameplay.PrepareCourse(timeline, courseSongs, courseStatus!);
        Shell.Scenes.Show(FlowScenes.Gameplay);
    }
}
