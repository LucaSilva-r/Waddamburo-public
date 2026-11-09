using Waddamburo.App.Audio;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Online;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Hosting;

/// <summary>
/// Creates each scene layer's game-side host by its host id. The flow sets the credit's state here
/// before a scene loads, and polls the loaded hosts it needs (mode switch, results end, revival).
/// </summary>
internal sealed class LayerHostFactory(
    GameFlowSession flow,
    SongCatalogSnapshot catalog,
    ISongBoardTextureService textures,
    ISongPreviewController previews,
    ISongSelectSoundController songSelectSounds,
    GameSounds? sounds,
    ILumenFrontendServices frontend,
    IDonPresentationController? don,
    IPlayRequestSink playRequests,
    Func<TaikoSongInfo> songInfo,
    bool countdown,
    Func<IReadOnlyList<TaikoPlayResult>> playResults,
    ArcadeSettings arcade,
    CoinBank? coins) : ILumenLayerHostFactory
{
    private EntrySceneHost? _entry;
    private int _entryPrevious;

    /// <summary>The cabinet's coins (null on free play); replaced when the service menu changes the coin settings.</summary>
    public CoinBank? Coins { get; set; } = coins;

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

    /// <summary>A drum's play history as chart keys, newest first (this PC's plays and the server's); null: none.</summary>
    public Func<int, IReadOnlyList<string>?>? RecentCharts { get; init; }

    /// <summary>The songs marked favourite (a folder per library, a red coin icon on their boards).</summary>
    public SongFavourites? Favourites { get; init; }

    /// <summary>Each saved play's previous best on its chart, by play index (results' best score).</summary>
    public Func<int, long?>? Best { get; init; }

    public Func<int, Waddamburo.Game.Scores.RankingPlacement?>? Placement { get; init; }

    /// <summary>Songs started this credit (song select shows the next one's number).</summary>
    public Func<int>? SongsStarted { get; init; }

    /// <summary>Song select's score windows from the server (null offline).</summary>
    public Online.SongRankings? Rankings { get; init; }

    /// <summary>The next entry is the home player setup (see <see cref="EntrySceneHost.SetupMode"/>).</summary>
    public bool EntrySetup { get; set; }

    /// <summary>
    /// The next entry comes from Song Select (SCENE_SONGSELECT, 1) with this drum's player already in: the
    /// other drum joins there (its SetPrevious trigger, <see cref="EntryTrigger"/>). Null: from the attract.
    /// </summary>
    public int? EntryRejoined { get; set; }

    /// <summary>The loaded entry's host (null outside the entry).</summary>
    public EntrySceneHost? Entry => _entry;

    /// <summary>A card read in the attract loop, for the entry about to load.</summary>
    public ScoreProfile? EntryCard { get; set; }

    /// <summary>A card read during entry; false when no entry runs or one is already pending.</summary>
    public bool InsertEntryCard(ScoreProfile card) => _entry?.InsertCard(card) == true;

    /// <summary>The entry gives up (see EntrySceneHost.ReturnToAttract).</summary>
    public Action? ReturnToAttract { get; set; }

    /// <summary>A card rejected during entry: the red band.</summary>
    public void RejectEntryCard() => _entry?.RejectCard();

    /// <summary>A card rejected in the attract loop: the entry about to load shows the red band.</summary>
    public bool EntryCardRejected { get; set; }

    /// <summary>The entry is reading a card or waiting for a drum to take it.</summary>
    public bool EntryCardPending => _entry?.CardBusy == true;

    public bool RefuseEntryCard() => _entry?.RefuseCardInUse() == true;

    /// <summary>The name of an account chosen for a drum before the entry (its board shows it at the join).</summary>
    public Func<int, string?>? PlayerName { get; init; }

    /// <summary>The look of the player about to join a drum (see EntrySceneHost.PlayerLook).</summary>
    public Func<int, DonLook?>? PlayerLook { get; init; }

    /// <summary>A card is being read at entry (the coin message hides meanwhile).</summary>
    public Action<bool>? CardDialog { get; set; }

    /// <summary>The entry gave the card to a drum (EntryData).</summary>
    public Action<int, ScoreProfile>? CardClaimed { get; init; }
    private IndicatorParts? _parts;

    // --- The credit's state, set by the flow ---

    /// <summary>SetPrevious trigger for the next entry: SCENE_TRIGGER_DON_1P (0) or _COIN (3).</summary>
    public int EntryTrigger { get; set; }

    /// <summary>The joined player's drum (0 left, 1 right; 0 for two players); scenes after entry follow it.</summary>
    public int PlayerSide { get; set; }

    /// <summary>Both drums joined.</summary>
    public bool TwoPlayers { get; set; }

    /// <summary>An online partner's side in Song Select: their Don-chan and name board show there (null: none).</summary>
    public int? PartnerSide { get; set; }

    /// <summary>Two players browse Waiwai's song select (and may switch to the normal one).</summary>
    public bool Waiwai { get; set; }

    /// <summary>The credit is an AI battle (entry's mode select, SetNextScene(10)): its song select, battles and results.</summary>
    public bool AiBattle { get; set; }

    /// <summary>The tutorial was watched this credit: Song Select drops its folder.</summary>
    public bool TutorialSeen { get; set; }

    /// <summary>The dan dojo's courses (null: no such folder in this run).</summary>
    public DanCourses? Dan { get; set; }

    /// <summary>The series dani_select lists.</summary>
    public string DanSeries { get; set; } = "green";

    /// <summary>The challenge course being played: its number in <see cref="DanSeries"/>; 0: a rank.</summary>
    public int DanChallenge { get; set; }

    /// <summary>The player's best result per course hash, for the course list's stamps.</summary>
    public Func<IReadOnlyDictionary<string, int>>? DanBests { get; set; }

    /// <summary>The course list on screen.</summary>
    public DaniSelectHostBinding? DaniSelect { get; private set; }

    /// <summary>The course behind each of its boards (null: the back board).</summary>
    public IReadOnlyList<DanCourseStatus?> DanBoards { get; private set; } = [];

    /// <summary>The live show_oni setting (the Settings menu changes it while the game runs).</summary>
    public Func<bool>? ShowOni { get; init; }

    /// <summary>The settings' random-song spine (the movie's roll folder) is on.</summary>
    public Func<bool>? RandomSongFolder { get; init; }

    /// <summary>Home without the arcade HUD: the timer and song number are hidden.</summary>
    public Func<bool>? HideArcadeHud { get; init; }

    /// <summary>The live timer freeze setting: timers show but never run out.</summary>
    public Func<bool>? TimerFreeze { get; init; }

    private bool counting => countdown && TimerFreeze?.Invoke() != true;

    /// <summary>Home: the tutorial was watched once (kept in config.cfg); song select no longer offers it.</summary>
    public Func<bool>? HomeTutorialSeen { get; init; }

    /// <summary>The boot's music credit pages there are (the copyright NUT's).</summary>
    public int CopyrightPages { get; init; }

    /// <summary>The song library normal Song Select lists (home mode switches between them).</summary>
    public SongSourceKind Library
    {
        get => _library ??= libraries().FirstOrDefault();
        set => (_libraryLeft, _library) = (Library, value);
    }
    private SongSourceKind? _library, _libraryLeft;

    /// <summary>The osu!lazer library's grouping and order (from the settings; its spines cycle them).</summary>
    public SongBrowse OsuBrowse { get; set; } = arcade.OsuBrowse;

    /// <summary>Cycles the grouping or order a spine names; the reloaded Song Select starts on that spine.</summary>
    public SongBrowse CycleBrowse(SongBrowseControl control)
    {
        _browseLeft = control;
        return OsuBrowse = control == SongBrowseControl.Group ? OsuBrowse.NextGroup() : OsuBrowse.NextSort();
    }
    private SongBrowseControl? _browseLeft;

    /// <summary>Whether Song Select offers the Online folder.</summary>
    public Func<bool>? OnlineAvailable { get; init; }

    /// <summary>The player is in an online room: songs are played together, so no dan dojo.</summary>
    public Func<bool>? InOnlineRoom { get; init; }

    /// <summary>A song another player sent this session (Song Select lists it while it's the online song).</summary>
    public Func<SongKey, SongDescriptor?>? Received { get; init; }


    /// <summary>The next Song Select opens on this song (an online host's pick), in its own library.</summary>
    public void FocusSong(SongKey song)
    {
        if (catalog.Categories.FirstOrDefault(category => category.Songs.Contains(song)) is not { } category)
            return;
        if (libraries().Contains(category.Key.Source))
            _library = category.Key.Source;
        _focus = (category.Key, song);
        (_browseLeft, _searchShown) = (null, false);
    }

    private (CategoryKey Category, SongKey Song)? _focus;

    /// <summary>Song Select lists only this song (the online host's pick, for the guest's course), from any library; null: as usual.</summary>
    public SongKey? OnlineSong { get; private set; }

    public void ShowOnlineSong(SongKey? song)
    {
        OnlineSong = song;
        if (song is not null)
            (_browseLeft, _searchShown) = (null, false);
    }

    /// <summary>The next Song Select starts on the Online spine (the lobby closed over it).</summary>
    public void ReturnToOnline() => _browseLeft = SongBrowseControl.Online;

    /// <summary>The last search's results, listed first in Song Select (null: no search folder).</summary>
    public SongSearchResults? Search
    {
        get => _search;
        set => (_search, _searchShown) = (value, value is not null);
    }
    private SongSearchResults? _search;
    private bool _searchShown;
    private (CategoryKey Category, SongKey Song)? _lastPick;

    /// <summary>A new cabinet credit: Song Select no longer starts on the previous credit's song.</summary>
    public void ForgetLastSong()
    {
        _lastPick = null;
        SongSelect?.ForgetPicked();
    }

    // Home mode keeps each source in its own library (arcade lists the stock songs as the game does);
    // one source needs no switching.
    private SongSourceKind[] libraries()
    {
        var sources = catalog.Categories.Select(static category => category.Key.Source).Distinct().Order().ToArray();
        return arcade.Home && sources.Length > 1 ? sources : [];
    }

    /// <summary>Waiwai's numbers for its results screen, from the finished song.</summary>
    public Func<WaiwaiOutcome?>? WaiwaiOutcome { get; set; }

    /// <summary>Called when a player joins at entry (coin mode updates the indicator panel).</summary>
    public Action<int>? EntryJoined { get; set; }

    /// <summary>Called with each layer's host id as a scene loads (the flow scopes per-scene state).</summary>
    public Action<string>? LayerLoading { get; set; }

    // --- The loaded hosts the flow polls ---

    /// <summary>The loaded song select's host (its mode switch).</summary>
    public SongSelectHostBinding? SongSelect { get; private set; }

    /// <summary>The loaded Waiwai results' host (null for normal results).</summary>
    public WaiwaiResultHostBinding? WaiwaiResult { get; private set; }

    /// <summary>The loaded revival scene's host (its outcome).</summary>
    public RetryGameHostBinding? Retry { get; private set; }

    /// <summary>The loaded game-over scene's host.</summary>
    public GameOverHostBinding? GameOver { get; private set; }

    /// <summary>The loaded attract movie's host.</summary>
    public AttractHostBinding? Attract { get; private set; }

    /// <summary>What follows the results (see <see cref="TaikoCredit"/>).</summary>
    // Home: an endless credit, song after song, no revival and no end message.
    public TaikoCreditNext CreditNext => arcade.Home ? TaikoCreditNext.NextSong : TaikoCredit.Next(songInfo().Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    private int endMessage => arcade.Home ? 0 : TaikoCredit.EndMessage(songInfo().Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    public void EntryCoinsChanged() => _entry?.CoinsChanged();

    public void AdvanceEntry() => _entry?.Advance();

    // --- Host construction ---

    public LumenLayerHost Create(SceneLayerDefinition layer)
    {
        LayerLoading?.Invoke(layer.HostId);
        // A front-end scene movie starts the indicator parts its following layers attach to.
        if (layer.HostId == "player-entry")
        {
            _parts = new IndicatorParts(IndicatorPartsScene.Entry, counting) { ShowCounter = HideArcadeHud?.Invoke() != true };
            _entry = new EntrySceneHost(_parts)
            {
                Coins = Coins,
                PlayVoice = sounds is null ? null : sounds.Frontend.PlayEntryVoice,
                PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
                CostumeIcon = static (type, id, name) => type is < 0 or > 2 ? null
                    : name ? CostumeIconTextures.NameKey(type, id) : CostumeIconTextures.Key(type, id),
                Card = EntryCard,
                Don = don,
                ReturnToAttract = () => ReturnToAttract?.Invoke(),
                PlayerLook = side => PlayerLook?.Invoke(side),
                PlayerName = side => PlayerName?.Invoke(side),
                CardDialog = open => CardDialog?.Invoke(open),
                CardClaimed = (side, card) => CardClaimed?.Invoke(side, card),
                SetupMode = EntrySetup,
                PreJoined = EntryRejoined,
            };
            _entryPrevious = EntryRejoined is null ? 0 : 1; // SCENE_ATTRACT / SCENE_SONGSELECT
            EntryRejoined = null;
            EntryCard = null;
            if (EntryCardRejected)
                _entry.RejectCard();
            EntryCardRejected = false;
            _entry.PlayerJoined += player => EntryJoined?.Invoke(player);
        }
        else if (layer.HostId == "song-select")
        {
            // time_counter has art for songs 1-4 (red when final); home's endless credit stays on 1.
            var next = arcade.Home ? 1 : (SongsStarted?.Invoke() ?? 0) + 1;
            _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, counting)
            {
                MusicNumber = Math.Min(next, 4),
                FinalStage = !arcade.Home && next >= arcade.SongsPerSession,
                ShowCounter = HideArcadeHud?.Invoke() != true,
            };
        }
        return layer.HostId switch
        {
            "player-entry" => entryHost(_entry!),
            IndicatorParts.HostId => partHost(_parts
                ?? throw new InvalidOperationException("Indicator part loaded without its scene movie."),
                Path.GetFileNameWithoutExtension(layer.MovieId)),
            "song-select" => songSelectHost(),
            GameplaySceneComposition.StaticHostId or GameplaySceneComposition.PlayerTwoHostId =>
                new LumenLayerHost(new TaikoGameplayHostBinding(songInfo, sounds is null ? null : sounds.Frontend.PlayCue)),
            RainbowTransitionComposition.StaticHostId => new LumenLayerHost(null),
            SystemIndicators.HostId => new LumenLayerHost(null),
            "result" => resultHost(),
            "ai-result" => aiResultHost(),
            "ai-result-section" => aiSectionResultHost(),
            // Traced SetWinTotalFlag(false) after a lost battle.
            "ai-result-bg" => aiResultBackgroundHost(),
            "waiwai-result" => waiwaiResultHost(),
            "dani-result" => daniResultHost(),
            "waiwai-result-board-0" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 0)),
            "waiwai-result-board-1" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 1)),
            "dani-board-0" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 0)),
            "dani-board-1" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 1)),
            "waitinput" => new LumenLayerHost(null), // the game never calls its Start (traced)
            "retry" => retryHost(),
            "tutorial" => tutorialHost(),
            "dani-select" => daniSelectHost(),
            "gameover" => gameOverHost(),
            "attract" => attractHost(),
            "boot" => new LumenLayerHost(null, bootCredits),
            _ => throw new KeyNotFoundException($"No Lumen host is configured for '{layer.HostId}'."),
        };
    }

    // kidou shows a music credit page per _global.copyright_num, each a native fill named after its label.
    private void bootCredits(LumenPlayer player)
    {
        if (CopyrightPages == 0 || !player.TryWriteScriptValue("_global.copyright_num", (double)CopyrightPages))
            return;
        for (var page = 1; page <= CopyrightPages; page++)
            player.SetNativeFill($"copyright{page}", Presentation.CopyrightArt.Key(page));
    }

    private LumenLayerHost entryHost(EntrySceneHost entry) => new(
        new LumenFrontendHostBinding(frontend, flow, don, entry),
        player =>
        {
            initializeEntry(player);
            entry.AttachEntry(player);
        });

    private void initializeEntry(LumenPlayer player)
    {
        if (don is not null)
        {
            // The entry's movie doesn't turn the second player's Don-chan: the mirrored camera faces them inward
            // (user-confirmed with an asymmetric costume; Katsu-chan looks alike either way).
            DonLumenBinding.Attach(player, don);
            player.SetNativeFill("donExM", don.GetSurface(2), DonLumenBinding.Placement);
        }
        // SetPrevious(scene, trigger): entry starts from the attract loop (scene 0) after a P1 drum
        // hit (SCENE_TRIGGER_DON_1P = 0) or a coin (SCENE_TRIGGER_COIN = 3); the movie then joins
        // P1 via EntryCoin.
        if (!player.TryInvokeCallback("SetPrevious", [
                LumenHostValue.FromNumber(_entryPrevious),
                LumenHostValue.FromNumber(EntryTrigger)]))
        {
            throw new InvalidOperationException("Entry did not export SetPrevious.");
        }
        if (!player.TryInvokeCallback("SetPlayer", [
                LumenHostValue.FromNumber(0),
                LumenHostValue.FromBoolean(false),
                LumenHostValue.FromBoolean(false),
                LumenHostValue.FromBoolean(false)]))
        {
            throw new InvalidOperationException("Entry did not export SetPlayer.");
        }
    }

    private static LumenLayerHost partHost(IndicatorParts parts, string movie) =>
        new(null, player => parts.Attach(movie, player));

    private LumenLayerHost songSelectHost()
    {
        var linked = Waiwai ? Array.Empty<SongSourceKind>() : libraries();
        var view = new SongSelectCatalogView(catalog, Waiwai ? SongSelectMode.Waiwai : SongSelectMode.Normal,
            modeSwitch: TwoPlayers, library: linked.Length > 0 && OnlineSong is null ? Library : null,
            // AI battle: the current library's songs, without the folders leading to the others.
            links: AiBattle ? [] : linked.Where(library => library != Library), favourites: Favourites?.Songs, tutorial: !AiBattle && !TutorialSeen && HomeTutorialSeen?.Invoke() != true,
            browse: OsuBrowse, search: Search, // As on the game: no dan dojo for two players, or without an account (it works offline once its list is saved).
            danDojo: !AiBattle && Dan?.Available == true && !TwoPlayers && TaikoGuest.Profiles[PlayerSide] is not null
                && InOnlineRoom?.Invoke() != true,
            danSeries: arcade.Home ? Dan?.Series : null,
            online: !Waiwai && !AiBattle && !TwoPlayers && OnlineAvailable?.Invoke() == true, onlySong: OnlineSong, received: Received,
            courseStar: OnlineSong is null, rollFolder: RandomSongFolder?.Invoke() == true,
            recentCharts: TwoPlayers || OnlineSong is not null ? null : RecentCharts?.Invoke(PlayerSide));
        // Back from a library: the cursor starts on the folder that leads to it; otherwise on the last
        // song played, when this view lists it.
        _lastPick = SongSelect?.Picked ?? _lastPick;
        if (_focus is { } focus)
            (_lastPick, _focus) = (focus, null);
        var (start, startSong) = (0, -1);
        if (OnlineSong is not null)
            (start, startSong) = (0, 0);
        else if (_searchShown)
            start = Math.Max(view.Categories.ToList().FindIndex(category => category.Key == SongSelectCatalogView.SearchKey), 0);
        else if (_browseLeft is { } control)
            start = Math.Max(view.Categories.ToList().FindIndex(category => category.Control == control), 0);
        else if (_libraryLeft is { } left)
            start = Math.Max(view.LinkCategory(left), 0);
        else if (_lastPick is { } pick && view.Find(pick.Category, pick.Song) is { } found)
            (start, startSong) = found;
        _libraryLeft = null;
        _browseLeft = null;
        _searchShown = false;
        var binding = SongSelect = new SongSelectHostBinding(
            new SongSelectSession(
                view,
                textures,
                previews,
                playRequests),
            songSelectSounds,
            don,
            parts: _parts,
            side: PlayerSide,
            twoPlayers: TwoPlayers,
            crowns: [Crowns?.Invoke(0), Crowns?.Invoke(1)],
            partner: PartnerSide)
        {
            StartCategory = start,
            ForcePick = OnlineSong is not null,
            StartSong = startSong,
            Favourites = Favourites,
            ShowOni = ShowOni?.Invoke() == true,
            PatchedGenres = Waddamburo.Game.Patching.SongSelectGenrePatch.LastApplied,
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            RankingWanted = Rankings is null ? null : Rankings.Want,
            Rankings = Rankings is null ? null : Rankings.For,
            AiBattle = AiBattle,
            SupportCounts = SupportCounts,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    /// <summary>AI battle's intermediate levels per song (below かんたん, ふつう, むずかしい, おに).</summary>
    public Func<SongDescriptor, int[]>? SupportCounts { get; set; }

    /// <summary>The AI battle song the next results show; null: not a battle.</summary>
    public AiBattleOutcome? AiOutcome { get; set; }

    /// <summary>AI battle's section results, while they are on stage.</summary>
    public AiSectionResultBinding? AiSectionResult { get; private set; }

    // AI battle's normal results: the player's play only (AI-don's lane is not shown); closing them starts the sections'.
    private LumenLayerHost aiResultHost()
    {
        WaiwaiResult = null;
        var plays = playResults();
        if (plays.Count == 0)
            throw new InvalidOperationException("Results loaded without a finished play.");
        IReadOnlyList<TaikoPlayResult> own = [plays[0]];
        var binding = new ResultHostBinding(() => own, songInfo().Stage, endMessage, don, sounds?.Results, 0)
        {
            Best = Best,
            AiBattle = true,
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            NormalResultClosed = () => AiSectionResult?.Start(),
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    // Traced SetWinTotalFlag(false) after a lost battle; sent with the section results' setup (once callbacks exist).
    private LumenLayerHost aiResultBackgroundHost() => new(null, player => AiResultBackground = player);

    /// <summary>AI battle's results backdrop (result_bg), while it is on stage.</summary>
    public LumenPlayer? AiResultBackground { get; private set; }

    private LumenLayerHost aiSectionResultHost()
    {
        var outcome = AiOutcome ?? throw new InvalidOperationException("AI battle results without a battle.");
        var binding = AiSectionResult = new AiSectionResultBinding(outcome, sounds is null ? null : sounds.Frontend.PlayCue)
        {
            Backdrop = () => AiResultBackground,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost resultHost()
    {
        WaiwaiResult = null;
        var plays = playResults();
        if (plays.Count == 0)
            throw new InvalidOperationException("Results loaded without a finished play.");
        var binding = new ResultHostBinding(() => plays, songInfo().Stage, endMessage,
            don, sounds?.Results, PlayerSide)
        {
            Best = Best,
            Placement = Placement,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    /// <summary>The course play the next results show; null: a song's.</summary>
    public DanCourseResult? DanResult { get; set; }

    /// <summary>The dan results on stage, while they are.</summary>
    public DaniResultHostBinding? DaniResult { get; private set; }

    private LumenLayerHost daniResultHost()
    {
        var binding = DaniResult = new DaniResultHostBinding(DanResult ?? throw new InvalidOperationException("Dan results loaded without a course play."),
            don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost waiwaiResultHost()
    {
        var outcome = WaiwaiOutcome?.Invoke() ?? throw new InvalidOperationException("Waiwai results without a Waiwai song.");
        var binding = WaiwaiResult = new WaiwaiResultHostBinding(outcome.GaugeSegments, outcome.DuetPercent,
            outcome.RareNotesHit, don, sounds?.WaiwaiResults);
        return new LumenLayerHost(binding, player =>
        {
            binding.Attach(player);
            player.SetNativeFill("dummy_bg", WaiwaiResultTextures.Background);
            player.SetNativeFill("dummy_sentence", WaiwaiResultTextures.Sentence(binding.Sentence));
            // ponytail: 00_taiko has one rare note kind; every slot shows it.
            for (var slot = 1; slot <= 5; slot++)
                player.SetNativeFill($"dummy_rare_onp_0{slot}", WaiwaiResultTextures.RareNote);
        });
    }

    private LumenLayerHost retryHost()
    {
        var binding = Retry = new RetryGameHostBinding(don, sounds?.Retry);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost tutorialHost()
    {
        var binding = new TutorialHostBinding(PlayerSide, TwoPlayers, sounds is null ? null : sounds.Frontend.PlayCue, don);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost daniSelectHost()
    {
        var view = new SongSelectCatalogView(catalog);
        var songs = view.Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .GroupBy(static entry => entry.Song.Descriptor.Key).ToDictionary(static group => group.Key, static group => group.First());
        var binding = DaniSelect = new DaniSelectHostBinding(() =>
        {
            if (!Dan!.Ready)
                return null;
            var (boards, courses) = Dan.Boards(DanSeries, DanBests?.Invoke());
            DanBoards = courses;
            return boards;
        }, PlayerSide, uid =>
            Dan!.Song(uid) is { } song && songs.TryGetValue(song.Key, out var entry)
                ? textures.GetSongTitle(entry.Song, entry.Category.BoardStyle, SongBoardTextureKind.Expanded) : null,
            (challenge, kind) => DanChallengeArt.Key(DanSeries, challenge, kind),
            songSelectSounds, don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost gameOverHost()
    {
        var binding = GameOver = new GameOverHostBinding(sounds?.GameOver, PlayerSide, TwoPlayers);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost attractHost()
    {
        var binding = Attract = new AttractHostBinding(sounds?.Attract);
        return new LumenLayerHost(binding);
    }
}
