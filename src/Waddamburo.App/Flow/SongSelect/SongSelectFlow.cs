using Waddamburo.Features;
using Waddamburo.Skins.Green.Art;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Library;
using Waddamburo.Game.Sound;
using Waddamburo.Lumen.Provider;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow.SongSelect;

/// <summary>
/// Song Select (normal or Waiwai): the mode-switch folder, and a chosen song's handoff: the rainbow
/// intermission covers the screen (song title in it), the charts load under it, gameplay starts.
/// Also the how-to-play folder's tutorial movie, which returns here.
/// </summary>
internal sealed class SongSelectFlow(GameShell shell, GameplayFlow gameplay, CalibrationFlow calibration) : FlowScene(shell)
{
    public override ScreenKind ScreenFor(SceneId scene) =>
        (scene == FlowScenes.Tutorial || scene == FlowScenes.WaiwaiTutorial || scene == FlowScenes.AiTutorial) ? ScreenKind.Tutorial
            : scene == FlowScenes.DaniSelect ? ScreenKind.DanSelect : ScreenKind.Select;

    public override void Enter(SceneId scene)
    {
        Shell.Players.StopWatching();
        if (scene == FlowScenes.SongSelect)
        {
            Shell.Previews?.StartBackground(Shell.Credit.Waiwai, Shell.Credit.AiBattle);
            if (Shell.Arcade.Home)
                foreach (var side in Shell.Players.JoinedSides)
                    Shell.RefreshPlayer(side); // rewards earned meanwhile (an AI battle win) show in the menu
            if (Shell.Arcade.Home && !Shell.Arcade.CustomUploadNoticeSeen
                && Shell.Select.Memory.Library is SongSourceKind.Tja or SongSourceKind.OsuLazer or SongSourceKind.OsuStable)
                Shell.CustomUploadPrompt.Open(static () => { });
        }
        else if (scene == FlowScenes.DaniSelect) // traced session30: bgm/nub/JINGLE_DANI opened as it loads
            Shell.Sounds?.PlayMusic("music.dan", loop: true);
        else if (scene == FlowScenes.WaiwaiTutorial)
            Shell.Sounds?.PrepareMusic("music.tutorial.waiwai", loop: true)?.Invoke();
        else if (scene == FlowScenes.AiTutorial)
            Shell.Sounds?.PrepareMusic("music.tutorial.ai", loop: true)?.Invoke();
        else // the tutorial's music (traced: bgm/nub/JINGLE_DOJO opened as it loads)
            Shell.Sounds?.PlayMusic("music.tutorial", loop: true);
    }

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Tutorial || scene == FlowScenes.WaiwaiTutorial || scene == FlowScenes.AiTutorial || scene == FlowScenes.DaniSelect)
            Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(300));
    }

    // The gameplay layout picked (skin included) when a song is chosen, its movies decoding and going
    // up to the GPU while the rainbow plays; the rainbow holds covered until they are all ready, as the
    // game does (traced: the song's scene loads under the closed rainbow, out_extra ~60 ms after).
    private sealed record Prefetched(SongKey Song, int Side, bool TwoPlayers, bool Waiwai,
        GameplaySceneComposition.ThemedSkin? Theme, SceneDefinition Scene);
    // One pick's rainbow cover: made fresh as each cover begins (and when one is abandoned), so nothing carries
    // over to the next pick (#17: a left-over preview flag cut the next song's preview).
    private sealed class Cover
    {
        public Prefetched? Prefetch;
        public Task<Prefetched>? Composing;
        public int? CoveredAt;
        public Task<AiBattleSession?>? AiSetup;
        public bool TitleReported;
        // The preview plays on under the closing rainbow until its title shows.
        public bool MusicUnderCover;
    }
    private Cover _cover = new();
    // Covered this long without the prefetch ready: load the rest the slow way.
    private const int CoverHoldLimitTicks = 600;

    // The skin lookup reads its whole archive: off the main thread, like the movies' decoding.
    private void composeGameplay(PlayRequest request)
    {
        var (category, song) = find(request.Song);
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        // AI-don plays a lane of its own, on the battle's fixed layout (startSong picks the same scene).
        var aiBattle = Shell.Credit.AiBattle && Shell.Gameplay.Dan is null;
        var twoPlayers = request.Players.Length == 2 || aiBattle;
        // Waiwai's fixed layout (its charts only reshape the notes).
        var waiwai = Shell.Credit.Waiwai && twoPlayers && song.Descriptor.WaiwaiComposition is not null;
        var dojo = Shell.Gameplay.Dan is not null;
        _cover.Composing = Task.Run(() =>
        {
            var theme = waiwai || aiBattle ? null : Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin);
            var scene = aiBattle ? GameplaySceneComposition.CreateAiBattle(FlowScenes.Gameplay, Shell.EnsoLayout)
                : waiwai ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
                : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side, twoPlayers, dojo);
            return new Prefetched(request.Song, side, twoPlayers, waiwai, theme, scene);
        });
    }

    // The list's fast scrolling (when set) sees the tick's input before the movie and what it did after.
    public override void Advance(LumenInputSnapshot input)
    {
        var scroll = Shell.Arcade.FastSongScroll && Shell.Active.Id == FlowScenes.SongSelect ? Shell.Hosts.SongSelect?.Scroll : null;
        base.Advance(scroll?.Before(input, listActive: Shell.Hosts.SongSelect?.Session.CourseSelectSong is null,
            keepWheel: Shell.Hosts.SongSelect?.Session.LeavingCourseSelect == true) ?? input);
        scroll?.After();
        Shell.Hosts.Poll(Shell.Active.Id);
    }

    private SongKey? _titled;

    public override void Tick(FlowInput input)
    {
        // Song Select's "New rewards" fades out to the reward animation (GameShell.ShowRewards).
        if (Shell.Scenes.FinishFade())
            return;
        // Traced: the tutorial cuts in and out (its movie fades itself); Song Select comes back
        // without the folder, the cursor on the first genre.
        if (Shell.Active.Id == FlowScenes.WaiwaiTutorial || Shell.Active.Id == FlowScenes.AiTutorial)
        {
            if (input.Escape || Shell.ScreenEnded) showSongSelect();
            return;
        }
        if (Shell.Active.Id == FlowScenes.Tutorial)
        {
            if (input.Escape || Shell.ScreenEnded)
                showSongSelect();
            return;
        }
        if (Shell.Active.Id == FlowScenes.DaniSelect)
        {
            if (Shell.Active.Player.Layers.Select(static layer => layer.Player).FirstOrDefault(static movie => movie.UsesProvider) is { } ours)
                tellDanSelect(ours);
            if (input.Escape || Shell.Hosts.DanSelectChoice is { Back: true })
            {
                Shell.Reload.Start(static () => { }, FlowScenes.SongSelect, FlowScenes.DanExitCover); // traced session31
                return;
            }
            if (Shell.Hosts.DanSelectChoice is { Board: { } board } && Shell.PlayRequests.Pending is null)
                PlayCourse(Shell.Hosts.DanBoards[board]!);
            if (Shell.PlayRequests.Pending is { } course)
                coverForGameplay(course);
            return;
        }
        if (Shell.Active.Id == FlowScenes.SongSelect && Shell.Hosts.SongSelect is { } host
            && Shell.Active.Player.Layers.Select(static layer => layer.Player).FirstOrDefault(static movie => movie.UsesProvider) is { } own)
            tellSongSelect(own, host);
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
            Shell.Sounds?.Play("select.join");
            var first = Shell.Players.JoinedSides.Min;
            Shell.Reload.Start(() =>
            {
                Shell.Credit.EntryTrigger = joining; // SCENE_TRIGGER_DON_1P / _2P
                Shell.Credit.EntryRejoined = first;
                if (Shell.Indicators is { } indicators)
                    indicators.Rejoin = true;
            }, FlowScenes.Entry);
            return;
        }
        if (Shell.Hosts.SongSelect?.Session.TutorialRequested == true)
        {
            if (Shell.Credit.Waiwai)
            {
                Shell.Credit.WaiwaiTutorialSeen = true;
                Shell.Scenes.Show(FlowScenes.WaiwaiTutorial);
                return;
            }
            if (Shell.Credit.AiBattle)
            {
                Shell.Credit.AiTutorialSeen = true;
                Shell.Scenes.Show(FlowScenes.AiTutorial);
                return;
            }
            Shell.Credit.TutorialSeen = true;
            // Home: once is enough (Green offers it every credit until watched; a PC has one endless credit).
            if (Shell.Arcade.Home && !Shell.Arcade.TutorialSeen)
                Shell.SaveArcade(Shell.Arcade with { TutorialSeen = true });
            Shell.Scenes.Show(FlowScenes.Tutorial);
            return;
        }
        if (Shell.Hosts.SongSelect?.Session.DanRequested == true)
        {
            Shell.Sounds?.Play("select.dan");
            // The series spine picked at home; a cabinet has only the one its operator set, and no spines.
            Shell.Dan.Series = Shell.Hosts.SongSelect!.Session.DanSeriesPicked ?? Shell.Arcade.DanSeries;
            Shell.Catalog.Replace(FlowScenes.DaniSelectScene(Shell.Credit.PlayerSide));
            Shell.Reload.Start(static () => { }, FlowScenes.DaniSelect, FlowScenes.DanCover);
            return;
        }
        // The rainbow's song title rasterizes in the background (large at 4K): started as the
        // difficulty selector opens, so it is there when the rainbow closes.
        if (Shell.Hosts.SongSelect?.Session.CourseSelectSong is { } picked && _titled != picked.Descriptor.Key)
        {
            _titled = picked.Descriptor.Key;
            _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(picked));
        }
        finishComposing();
        // The mode-switch folder: reload as the other song select (normal <-> Waiwai).
        if (Shell.Hosts.SongSelect?.Session.ModeSwitchRequested == true)
        {
            Shell.Credit.Waiwai = !Shell.Credit.Waiwai;
            showSongSelect();
            return;
        }
        if (switchLibrary() || calibrate())
            return;
        // The Online spine: the lobby opens over Song Select.
        if (Shell.Hosts.SongSelect?.Session.BrowseRequested == SongBrowseControl.Online)
        {
            Shell.Room.OpenLobby();
            return;
        }
        // A choice in a Group or Sort folder (osu! and Wii libraries): applied, saved, Song Select reloading on that folder.
        if (Shell.Hosts.SongSelect?.Session.BrowsePicked is (var control, var value))
        {
            Shell.Reload.Start(() =>
            {
                var browse = Shell.SelectMemory.PickBrowse(control, value);
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
                if (Shell.SelectMemory.Library is not (SongSourceKind.Wii or SongSourceKind.WiiU))
                    Console.WriteLine($"osu!lazer library: group by {SongBrowse.Name(browse.Group)}, sort by {SongBrowse.Name(browse.Sort)}.");
            });
            return;
        }
        // P marks the song under the cursor (or in course select) favourite, or unmarks it, and says so in a
        // short-lived notice; the keys are one-tick pulses.
        if (GameActions.Down(input.Keys, GameAction.Favourite) && Shell.Hosts.SongSelect is { } select)
        {
            if (select.ToggleFavourite() is { } toggled)
                Shell.Sync.Notices.AddLocal(Strings.T(toggled.Added ? "favourites.added" : "favourites.removed",
                    toggled.Song.Descriptor.Title.Primary), lifetime: TimeSpan.FromSeconds(6));
            else
                Console.WriteLine("Favourite: the cursor is on no song.");
        }
        // H copies the song's charts as custom dan dojo entries ({ "hash": ... } per course) for the Dani folder's files.
        // ponytail: a plain keyboard key, not a bindable button; hashing an unhashed chart blocks for its parse.
        if (input.Keys.IsDown(Waddamburo.Platform.Sdl.SdlKeyboardKey.H) && Shell.Hosts.SongSelect?.SongUnderCursor is { } hashed)
        {
            var lines = hashed.Descriptor.Charts.Where(static chart => chart.Course is not null)
                .Select(chart => (chart, hash: Shell.ChartHashes.HashAsync(chart).AsTask().GetAwaiter().GetResult()))
                .Where(static entry => entry.hash is not null)
                .Select(entry => $"{{ \"hash\": \"{entry.hash}\", \"title\": \"{System.Text.Json.JsonEncodedText.Encode($"{hashed.Descriptor.Title.Primary} ({entry.chart.Course})")}\" }},");
            if (Waddamburo.Platform.Sdl.SdlApplication.SetClipboard(string.Join('\n', lines)))
                Shell.Sync.Notices.AddLocal(Strings.T("song_select.hashes_copied", hashed.Descriptor.Title.Primary), lifetime: TimeSpan.FromSeconds(6));
        }
        // Online, a guest only plays the host's song.
        if (Shell.PlayRequests.Pending is { } request && Shell.Match.Accept(request))
            coverForGameplay(request);
    }

    // The gameplay layout, composed in the background, starts decoding.
    private void finishComposing()
    {
        if (_cover.Composing is not { IsCompleted: true } composed)
            return;
        // Slow storage: the files are read once the rainbow stands still, where loading cannot be seen (its cover stuttered).
        if (_cover.CoveredAt is null)
            return;
        _cover.Composing = null;
        if (composed.IsCompletedSuccessfully)
        {
            _cover.Prefetch = composed.Result;
            Shell.Scenes.Prefetch(_cover.Prefetch.Scene);
            Console.WriteLine($"Prefetching gameplay for '{_cover.Prefetch.Song}' at tick {Shell.Tick}.");
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
        _cover = new Cover();
    }

    private void stopMusic()
    {
        Shell.Previews?.Silence();
        Shell.Audio?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
        Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(20));
    }


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
            rainbow.Begin(Shell.Tick, Shell.Credit.Waiwai ? RainbowTransitionSequence.WaiwaiLeadInTicks : null);
            _cover = new Cover();
            composeGameplay(request);
            Console.WriteLine($"Queued rainbow cover for '{request.Song}' at tick {Shell.Tick}.");
        }
        if (rainbow.ShouldStartCover(Shell.Tick))
        {
            var player = Shell.Overlay.Show(FlowScenes.Rainbow);
            var song = find(request.Song).Song.Descriptor;
            player.Publish("song.title", LumenHostValue.FromString(song.Title.English ?? song.Title.Primary));
            player.Publish("song.subtitle", song.Subtitle is { Length: > 0 } subtitle ? LumenHostValue.FromString(subtitle) : LumenHostValue.Undefined);
            Shell.Skin.Transitions.ShowTitle(player,
                gameplay.RainbowTitle ?? throw new InvalidOperationException("Rainbow transition has no song title."));
            // Traced session30: a course's first song opens under the plain rainbow, no title.
            Shell.Overlay.Cover(title: Shell.Gameplay.Dan is null);
            rainbow.StartCover();
            // The song's preview plays on under the closing rainbow and stops as its title appears (#17); a course's
            // plain rainbow has no title: it stops now.
            _cover.MusicUnderCover = Shell.Gameplay.Dan is null;
            if (!_cover.MusicUnderCover)
                stopMusic();
            Console.WriteLine($"Rainbow cover started at tick {Shell.Tick}.");
        }
        if (_cover.MusicUnderCover && (Shell.Overlay.Player is not { } closing || Shell.Skin.Transitions.TitleShown(closing) || _cover.CoveredAt is not null
            || rainbow.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete))
        {
            _cover.MusicUnderCover = false;
            stopMusic();
            Console.WriteLine($"Song Select music stopped as the rainbow's title appeared, at tick {Shell.Tick}.");
        }
        var titleReady = gameplay.RainbowTitle is { } title && Shell.Titles.Resolve(title) is not null;
        if (titleReady && !_cover.TitleReported && rainbow.State is not RainbowTransitionState.Idle)
        {
            _cover.TitleReported = true;
            Console.WriteLine($"Rainbow title ready at tick {Shell.Tick}.");
        }
        if (Shell.Overlay.Player is { } cover && rainbow.FinishCoverWhenStopped(cover.IsPlaying, Shell.Tick))
            _cover.CoveredAt = Shell.Tick;
        // Online: held on the song title until the other player has picked, then it plays with them.
        if (_cover.CoveredAt is not null)
        {
            if (Shell.Match.Together(request) is not { } together)
                return;
            request = together;
            if (Shell.Credit.AiBattle)
            {
                _cover.AiSetup ??= prepareBattle(request, Shell.Hosts.SongSelect?.SupportLevel ?? 0,
                    Shell.Hosts.SongSelect?.ShownSupportCounts.ElementAtOrDefault((int)request.Players[0].Course) ?? 0);
                if (!_cover.AiSetup.IsCompleted) return;
            }
        }
        // Held on the song title (shown) until the song's scene is decoded and uploaded (then its switch is quick).
        // The play's later screens (results, the way out) too: nothing loads during the song.
        if (_cover.CoveredAt is { } coveredAt && ((titleReady && _cover.Prefetch is not null && _cover.Composing is null && Shell.Scenes.PrefetchReady
                    && Shell.Scenes.WarmReady)
                || Shell.Tick - coveredAt >= CoverHoldLimitTicks))
        {
            if (Shell.Tick - coveredAt >= CoverHoldLimitTicks)
                Console.Error.WriteLine("The song's scene was not ready in time; loading the rest now.");
            Console.WriteLine($"Rainbow held covered {Shell.Tick - coveredAt} tick(s) for the song's scene.");
            _cover.CoveredAt = null;
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
            var level = SupportInfo.SupportPick(ChartSimplifier.LevelOf(player.Course), offered, pick);
            var support = pick > 0 && ChartSimplifier.SourceCourse(level) == player.Course ? level : 0;
            var chart = support == 0 ? source : ChartSimplifier.Simplify(source, support);
            var session = await Shell.Sync.BeginAiBattleAsync(TaikoGuest.Profiles[(int)player.Player],
                ChartHash.Compute(chart, player.Course), ChartHash.Compute(source, player.Course), player.Course,
                player.Options.Bits, support).ConfigureAwait(false);
            // No answer (offline): the battle uploads later and the server verifies it then.
            if (session is { Ranked: false })
                Shell.Sync.Notices.Add(new Waddamburo.Services.Online.Notice("ai-unranked", Waddamburo.Services.Online.NoticeSource.Local,
                    Waddamburo.Services.Online.NoticeSeverity.Info,
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
        if (Shell.Hosts.SongSelect?.Session.LibraryRequested is not { } library)
            return false;
        if (!Shell.Arcade.CustomUploadNoticeSeen && library is SongSourceKind.Tja or SongSourceKind.OsuLazer or SongSourceKind.OsuStable)
            Shell.CustomUploadPrompt.Open(() => Shell.Reload.Start(() => Shell.SelectMemory.Library = library));
        else
            Shell.Reload.Start(() => Shell.SelectMemory.Library = library);
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
            Shell.Overlay.Show(FlowScenes.Rainbow);
            Shell.Overlay.Cover();
            return true;
        }
        if (Shell.Overlay.Player is { IsPlaying: true })
            return true;
        _coveringForCalibration = false;
        Shell.Catalog.Replace(calibration.Scene());
        Shell.Scenes.Show(FlowScenes.Calibration);
        Shell.Overlay.Reveal();
        return true;
    }

    private void showSongSelect()
    {
        // A launch straight into Song Select joined nobody: keep the player's own board.
        Shell.Catalog.Replace(FlowScenes.SongSelectScene(
            Shell.Players.JoinedSides.Count > 0 ? [.. Shell.Players.JoinedSides] : [Shell.Credit.PlayerSide], Shell.Credit.Waiwai, Shell.Credit.AiBattle));
        Shell.Scenes.Show(FlowScenes.SongSelect);
    }

    private DanCourseStatus? _course;

    // Our song select (it reads the provider): the catalog as library rows (its folders, their songs, a chart per
    // course), each request doing what the game's call for that step does (SongSelectHostBinding.Own…).
    private sealed record FolderTag(int Folder, bool Choice);
    private sealed record SongTag(int Folder, int Song);
    private sealed record ChartTag(int Folder, int Song, int Course);
    private LumenPlayer? _songMovie;
    private LibraryRows? _songRows;
    private const int SongSelectSeconds = 100; // the game's song select asks StartTimer(100)

    // A drum's player takes a drum sound for the session and hears it once.
    private string? chooseTone(int side, int tone)
    {
        if (tone < 0 || tone >= (Shell.Sounds?.Set.Tones.Count ?? 0))
            return $"there is no drum sound {tone}";
        TaikoGuest.Tones[side] = tone;
        _songMovie?.Publish("player.tone", LumenHostValue.FromNumber(tone), side + 1);
        Shell.Sounds?.Play(new SoundRequest("drum.don", side, tone));
        return null;
    }

    private void tellSongSelect(LumenPlayer movie, ISongSelectScreen host)
    {
        if (movie != _songMovie)
        {
            (_songMovie, _songRows) = (movie, null);
            string? rows(Func<LibraryRows, string?> act) => _songRows is { } loaded ? act(loaded) : "the list is not set up yet";
            static int? at(LumenHostValue[] arguments, int index) =>
                arguments.Length > index && arguments[index].Kind == LumenHostValueKind.Number ? (int)arguments[index].AsNumber() : null;
            new WdRequests("select")
                .Handle("rows.window", arguments => at(arguments, 0) is { } first && at(arguments, 1) is { } count
                    ? rows(loaded => loaded.Window(first, count)) : "rows.window takes the first row and how many")
                .Handle("select.row", arguments => at(arguments, 0) is { } row ? rows(loaded => selectSongRow(loaded, host.Session, row)) : "select.row takes a row")
                .Handle("row.open", arguments => at(arguments, 0) is { } row ? rows(loaded => openSongRow(loaded, host.Session, row)) : "row.open takes a row")
                .Handle("row.close", arguments => at(arguments, 0) is { } row ? rows(loaded => closeSongRow(loaded, host.Session, row)) : "row.close takes a row")
                .Handle("song.start", _ => rows(loaded => startSong(loaded, host.Session)))
                .Handle("tone.choose", arguments => at(arguments, 0) is { } tone && at(arguments, 1) is { } drum and (1 or 2)
                    ? chooseTone(drum - 1, tone) : "tone.choose takes a tone and a drum (1 or 2)")
                .Install(Shell.Active.Player.Layers.Select(static layer => layer.Player));
            // The drum sounds a player can choose, and each drum's choice.
            var tones = Shell.Sounds?.Set.Tones ?? [];
            movie.Publish("tone.count", LumenHostValue.FromNumber(tones.Count));
            for (var index = 0; index < tones.Count; index++)
                movie.PublishItem("tone.name", index, LumenHostValue.FromString(tones[index]));
            for (var side = 0; side < 2; side++)
                movie.Publish("player.tone", LumenHostValue.FromNumber(TaikoGuest.Tones[side]), side + 1);
        }
        if (_songRows is null)
        {
            if (!host.OwnReady())
                return;
            var loaded = _songRows = new LibraryRows(songTree(host.Catalog));
            host.Session.StartCountdown(SongSelectSeconds);
            // Where the cursor starts: a folder, or a song inside it (back from a song).
            var folder = Math.Clamp(host.Session.StartCategory, 0, Math.Max(0, loaded.Rows.Count - 1));
            if (host.Session.StartSong >= 0 && openSongRow(loaded, host.Session, folder) is null)
                selectSongRow(loaded, host.Session, folder + 1 + host.Session.StartSong);
            else
                selectSongRow(loaded, host.Session, folder);
        }
        _songRows.Publish(movie);
        movie.Publish("select.timeLeft", host.Session.TimeLeft is { } left ? LumenHostValue.FromNumber(left) : LumenHostValue.Undefined);
    }

    private static IReadOnlyList<LibraryNode> songTree(SongSelectCatalogView catalog)
    {
        var folders = new List<LibraryNode>();
        for (var index = 0; index < catalog.Categories.Length; index++)
        {
            var category = catalog.Categories[index];
            var choice = (category.Presentation & SongCategoryPresentation.FolderEnd) != 0;
            var folder = new LibraryNode(LibraryNodeKind.Folder, category.Name) { Tag = new FolderTag(index, choice) };
            if (!choice)
                for (var position = 0; position < category.Songs.Length; position++)
                {
                    var entry = category.Songs[position];
                    var title = entry.Descriptor.Title;
                    var song = new LibraryNode(LibraryNodeKind.Song, title.English ?? title.Primary)
                    {
                        Subtitle = entry.Descriptor.Subtitle,
                        Artist = entry.Descriptor.Artist,
                        Tag = new SongTag(index, position),
                    };
                    foreach (var course in Enum.GetValues<TaikoCourse>().Where(entry.HasCourse))
                        song.Children.Add(new LibraryNode(LibraryNodeKind.Chart, course.ToString())
                        {
                            Stars = entry.Level(course) ?? 1,
                            Course = course.ToString().ToLowerInvariant(),
                            Tag = new ChartTag(index, position, (int)course),
                        });
                    folder.Children.Add(song);
                }
            folders.Add(folder);
        }
        return folders;
    }

    private string? selectSongRow(LibraryRows rows, SongSelectSession session, int row)
    {
        var before = rows.Selected;
        if (rows.Select(row) is { } refused)
            return refused;
        if (rows.Selected == before)
            return null;
        switch (rows.Selected?.Tag)
        {
            case FolderTag folder:
                session.MoveFolderCursor(folder.Folder); // the folder's voice
                break;
            case SongTag song:
                // The song's score windows are fetched now, its preview starts.
                if (session.Catalog.TryGetSong(song.Folder, song.Song, out var selected))
                    Shell.Rankings?.Want(selected);
                session.Preview(song.Folder, song.Song);
                break;
        }
        return null;
    }

    private static string? openSongRow(LibraryRows rows, SongSelectSession session, int row)
    {
        if ((uint)row >= (uint)rows.Rows.Count)
            return $"there is no row {row} ({rows.Rows.Count} rows)";
        switch (rows.Rows[row].Node.Tag)
        {
            // A library, the dojo, the tutorial: the flow takes over (a reload, another screen).
            case FolderTag { Choice: true } choice:
                session.Choose(choice.Folder);
                return null;
            case FolderTag folder when rows.Open(row) is null:
                session.Open(folder.Folder);
                return null;
            case SongTag song when rows.Open(row) is null:
                session.BeginCourses(song.Folder, song.Song);
                return null;
        }
        return rows.Open(row);
    }

    private static string? closeSongRow(LibraryRows rows, SongSelectSession session, int row)
    {
        var node = (uint)row < (uint)rows.Rows.Count ? rows.Rows[row].Node : null;
        if (rows.Close(row) is { } refused)
            return refused;
        if (node?.Tag is FolderTag)
        {
            session.Close();
            session.StopPreview();
        }
        if (node?.Tag is SongTag) session.LeaveCourses();
        return null;
    }

    // Plays the chart's course for every joined drum.
    private string? startSong(LibraryRows rows, SongSelectSession session)
    {
        if (rows.Selected?.Tag is not ChartTag chart)
            return "the cursor is not on a chart";
        if (Shell.PlayRequests.Pending is not null)
            return "a song is already starting";
        int course(int side) => Shell.Credit.TwoPlayers || Shell.Credit.PlayerSide == side ? chart.Course : -1;
        session.Select(chart.Folder, chart.Song, course(0), course(1), TaikoGuest.Options);
        session.RecordLastCourses(chart.Folder, chart.Song, course(0), course(1));
        session.StopCountdown();
        session.Choose(chart.Folder);
        return null;
    }

    // Our dan select (it reads the provider): the dojo as library rows (series, courses, their songs), the select
    // scope's requests, the selected course's requirements as the dan.exam lists.
    private LumenPlayer? _danMovie;
    private LibraryRows? _danRows;
    private IReadOnlyList<LibraryNode> _danTree = [];
    private int _danExams;

    private void tellDanSelect(LumenPlayer movie)
    {
        if (movie != _danMovie)
        {
            (_danMovie, _danRows, _danExams) = (movie, null, 0);
            string? rows(Func<LibraryRows, string?> act) => _danRows is { } loaded ? act(loaded) : "the courses are not loaded yet";
            static int? at(LumenHostValue[] arguments, int index) =>
                arguments.Length > index && arguments[index].Kind == LumenHostValueKind.Number ? (int)arguments[index].AsNumber() : null;
            new WdRequests("select")
                .Handle("rows.window", arguments => at(arguments, 0) is { } first && at(arguments, 1) is { } count
                    ? rows(loaded => loaded.Window(first, count)) : "rows.window takes the first row and how many")
                .Handle("select.row", arguments => at(arguments, 0) is { } row ? rows(loaded => loaded.Select(row)) : "select.row takes a row")
                .Handle("row.open", arguments => at(arguments, 0) is { } row ? rows(loaded => loaded.Open(row)) : "row.open takes a row")
                .Handle("row.close", arguments => at(arguments, 0) is { } row ? rows(loaded => loaded.Close(row)) : "row.close takes a row")
                .Handle("course.start", _ => rows(startCourse))
                .Install(Shell.Active.Player.Layers.Select(static layer => layer.Player));
        }
        if (_danRows is null && Shell.Dan is { Ready: true } dan)
        {
            _danTree = dan.Tree();
            _danRows = new LibraryRows(_danTree);
        }
        if (_danRows is not { } loaded)
            return;
        if (loaded.SelectionChanged)
            tellRequirements(movie, loaded.Selected?.Tag as DanCourseStatus);
        loaded.Publish(movie);
    }

    private string? startCourse(LibraryRows rows)
    {
        if (rows.Selected is not { Kind: LibraryNodeKind.Course, Tag: DanCourseStatus status } course)
            return "the cursor is not on a course";
        if (!status.Playable)
            return $"{course.Title} has songs that are not in the library";
        if (Shell.PlayRequests.Pending is not null)
            return "a course is already starting";
        Shell.Dan.Series = _danTree.First(folder => folder.Children.Contains(course)).Tag as string ?? Shell.Dan.Series;
        PlayCourse(status);
        return null;
    }

    // The course's requirements, as the dan panel's lists (each song's target, where songs have their own: the first).
    private void tellRequirements(LumenPlayer movie, DanCourseStatus? status)
    {
        var requirements = status?.Course.Requirements ?? [];
        movie.Publish("dan.exam.count", status is null ? LumenHostValue.Undefined : LumenHostValue.FromNumber(requirements.Count));
        for (var index = 0; index < Math.Max(requirements.Count, _danExams); index++)
        {
            var requirement = index < requirements.Count ? requirements[index] : null;
            movie.PublishItem("dan.exam.type", index, requirement is null ? LumenHostValue.Undefined : LumenHostValue.FromString(requirement.Type));
            movie.PublishItem("dan.exam.target", index, requirement is { Red.Length: > 0 } ? LumenHostValue.FromNumber(requirement.Red[0]) : LumenHostValue.Undefined);
            movie.PublishItem("dan.exam.gold", index, requirement is null ? LumenHostValue.Undefined
                : LumenHostValue.FromNumber(requirement.Gold.Length > 0 ? requirement.Gold[0] : 0));
        }
        _danExams = requirements.Count;
    }

    internal void PlayCourse(DanCourseStatus course)
    {
        _course = course;
        var first = course.Songs[0];
        var key = first.Chart!;
        var song = find(key.Song).Song.Descriptor;
        var chart = song.Charts.First(candidate => candidate.Key == key);
        Shell.Gameplay.Dan = course.Course;
        var challenge = Shell.Dan.Challenge = Shell.Dan.ChallengeNumber(Shell.Dan.Series, course.Course);
        Shell.Gameplay.DanChallengeArt = challenge == 0 ? null : DanChallengeArt.Key(Shell.Dan.Series, challenge, "mini");
        var side = Shell.Credit.PlayerSide == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne;
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
        if (OnlineEcho.Delay is not null && charts.Length == 1 && timeline is null && !Shell.Credit.Waiwai)
            charts = [charts[0], charts[0]];
        // AI battle: AI-don plays the same chart on a second lane.
        if (Shell.Credit.AiBattle && charts.Length == 1 && timeline is null)
            charts = [charts[0], charts[0]];
        var twoPlayers = charts.Length == 2;
        // Waiwai: both players on the song's Waiwai layout (its duet charts reshaped by section).
        WaiwaiComposition? waiwai = null;
        if (Shell.Credit.Waiwai && charts.Length == 2 && song.Descriptor.WaiwaiComposition is { } compositionAsset)
        {
            using var compositionStream = Shell.Assets.OpenReadAsync(compositionAsset).AsTask().GetAwaiter().GetResult();
            waiwai = WaiwaiComposition.Parse(compositionStream);
            // A song gets its rare (heart) note by chance (traced 1 of 4 runs, and 2 of 2 earlier).
            // ponytail: waiwaicollabo/00_taiko.xml prob_rare_0 = 16 read as a percent per song.
            var (left, right) = waiwai.Apply(charts[0], charts[1],
                rareNote: Shell.Random.Next(100) < 16
                    || DiagnosticSwitches.WaiwaiRare); // diagnostic: force it
            charts = [left, right];
        }
        var prefetched = _cover.Prefetch is { } ready && ready.Song == request.Song && ready.Side == side
            && ready.TwoPlayers == twoPlayers && ready.Waiwai == waiwai is not null ? ready : null;
        _cover.Prefetch = null;
        // Every song gets its themed skin or a fresh random original mix.
        var theme = prefetched is not null ? prefetched.Theme : waiwai is not null ? null
            : Shell.Skins.Resolve(song.Descriptor, category, Shell.Arcade.GameplaySkin);
        if (Shell.Sounds?.Gameplay is { } sounds)
        {
            sounds.Waiwai = waiwai is not null;
            sounds.AiBattle = Shell.Credit.AiBattle && twoPlayers;
            sounds.Tones = [.. request.Players.Select(static player => TaikoGuest.Tones[(int)player.Player])];
            sounds.PrepareDrums(request.Players.Length == 2 || sounds.AiBattle);
        }
        // The credit's song number (reset when a credit ends); a home session's endless credit stays on 1
        // (the counters only have art for songs 1-4).
        var stage = Shell.Arcade.Home ? 1 : ++Shell.Credit.SongsPlayed;
        Shell.Credit.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), stage,
            Final: !Shell.Arcade.Home && stage >= Shell.Arcade.SongsPerSession);
        Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
        Shell.Catalog.Replace(Shell.Credit.AiBattle && twoPlayers ? GameplaySceneComposition.CreateAiBattle(FlowScenes.Gameplay, Shell.EnsoLayout)
            : prefetched?.Scene ?? (waiwai is not null
            ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side,
                twoPlayers: twoPlayers, dojo: Shell.Gameplay.Dan is not null)));
        gameplay.Prepare(charts, song, side, waiwai);
        gameplay.AiSession = _cover.AiSetup is { IsCompletedSuccessfully: true } setup ? setup.Result : null;
        _cover.AiSetup = null;
        if (timeline is not null)
            gameplay.PrepareCourse(timeline, courseSongs);
        Shell.Scenes.Show(FlowScenes.Gameplay);
    }
}
