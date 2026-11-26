using Waddamburo.Features.Shop;
using Waddamburo.Skins.Green.Art;
using Waddamburo.Skins.Green.Screens;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Skins.Green.Gameplay;
using Waddamburo.Skins.Green.Sound;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green;

/// <summary>
/// Creates each scene layer's host by its host id: the game's bindings, which also serve our movies on the provider.
/// The flow sets the credit's state before a scene loads and asks the loaded screens through <see cref="IScreenHosts"/>.
/// </summary>
public sealed class GreenScreenHosts(
    CreditState credit,
    ShopModule shop,
    Func<bool, SongSelectSession> createSelectSession,
    GameFlowSession flow,
    SongCatalogSnapshot catalog,
    ISongBoardTextureService textures,
    GreenScreenSounds? sounds,
    IDonPresentationController? don,
    ISongPreviewController previews,
    bool countdown,
    Func<IReadOnlyList<TaikoPlayResult>> playResults,
    Func<ArcadeSettings> arcadeSettings,
    CoinBank? coins) : IScreenHosts
{
    /// <summary>The credit's state the flow sets before a screen loads.</summary>
    public CreditState Credit { get; } = credit;

    // The game's movies end a screen with _global.isAllEnd (RetryGameHostBinding.IsEnd) or a call their host keeps.
    public bool Ended(SceneId screen, IReadOnlyList<LumenPlayer> movies)
    {
        if (screen == FlowScenes.Result)
            // AI battle's end with its section results (their movie calls Start); result_normal, the second layer,
            // sets _global.isAllEnd once its 面でおわり exit runs. Waiwai's reports AllFinish.
            return Credit.AiOutcome is not null
                ? AiSectionResult is { } sections && (sections.Ended || sections.Played && RetryGameHostBinding.IsEnd(movies[1]))
                : WaiwaiResult is { } waiwai ? waiwai.Ended : RetryGameHostBinding.IsEnd(movies[0]);
        if (screen == FlowScenes.AiRemind)
            return RetryGameHostBinding.IsEnd(movies[1]); // remind, over its backdrop
        if (screen == FlowScenes.Retry)
            return RetryGameHostBinding.IsEnd(movies.Single());
        if (screen == FlowScenes.Tutorial)
            return RetryGameHostBinding.IsEnd(movies[0]);
        if (screen == FlowScenes.WaiwaiTutorial || screen == FlowScenes.AiTutorial)
            return ModeTutorial?.Finished == true;
        if (screen == FlowScenes.Boot)
            return AttractHostBinding.IsBootEnd(movies.Single());
        if (screen == FlowScenes.Logo || screen == FlowScenes.Title || screen == FlowScenes.Caution || screen == FlowScenes.Movie)
            return Attract?.Finished == true;
        return GameOver?.Ended == true; // the credit's last screens
    }

    public void Poll(SceneId screen)
    {
        if (screen == FlowScenes.Entry)
            _entry?.Advance();
        else if (screen == FlowScenes.Result)
        {
            if (Credit.DanResult is not null)
                DaniResult?.Poll();
            AiSectionResult?.Poll();
        }
        else if (screen == FlowScenes.AiRemind)
            AiRemind?.Poll();
        else
        {
            // Song Select's flow: its tutorials and the course list too.
            if (screen == FlowScenes.WaiwaiTutorial || screen == FlowScenes.AiTutorial)
                ModeTutorial?.Poll();
            SongSelect?.Poll();
            if (screen == FlowScenes.DaniSelect)
                DaniSelect?.Poll();
        }
    }

    // The attract stream (the title's) and game over's music, which the game's movies start.
    public void Left(SceneId screen)
    {
        if (screen == FlowScenes.Title)
            sounds?.Attract.StopAttractStream();
        else if (screen == FlowScenes.GameOver)
            sounds?.GameOver.StopGameOverMusic();
    }

    private Gameplay.GreenGameplayScreen? _gameplay;

    public IGameplayScreen Gameplay(LumenGameSceneInstance scene) =>
        _gameplay is { } shown && shown.Scene == scene ? shown
            : _gameplay = new(scene, sounds is null ? null : sounds.Frontend.PlayCue);

    public void MuteAttract(bool muted)
    {
        if (sounds is not null)
            sounds.Attract.Muted = muted;
    }

    public void TimeOut(SceneId screen)
    {
        if (screen == FlowScenes.AiRemind)
            AiRemind?.Exit();
    }

    public DanSelectChoice? DanSelectChoice => DaniSelect is { Terminated: true } list ? new(list.Back, list.Selected) : null;

    public bool RevivalSucceeded => Retry?.Succeeded == true;

    // Without audio: the game's sound requests are traced.
    private readonly ISongSelectSoundController songSelectSounds = sounds is null ? TraceSoundController.Instance : sounds.Frontend;
    private readonly ILumenFrontendServices frontend = new ViewerFrontendServices(sounds?.Frontend, arcadeSettings().FreePlay);

    // Live: the service menu changes settings (e.g. songs per session) while the game runs.
    private ArcadeSettings arcade => arcadeSettings();

    private EntrySceneHost? _entry;
    private int _entryPrevious;

    /// <summary>The cabinet's coins (null on free play); replaced when the service menu changes the coin settings.</summary>
    public CoinBank? Coins { get; set; } = coins;

    public void EntryCoinsChanged() => _entry?.CoinsChanged();

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

    /// <summary>AI battle's song select panels: the player's rank, points to the next, wins, wins to the next reward.</summary>
    public Func<AiBattlePanels>? AiPanels { get; init; }

    /// <summary>Song select's score windows from the server (null offline): fetch a song's, and those fetched.</summary>
    public Action<SongSelectSong>? RankingWanted { get; init; }

    public Func<SongSelectSong, IReadOnlyList<RankingEntry>?[]?>? Rankings { get; init; }

    /// <summary>Song Select's favourite songs.</summary>
    public SongFavourites? Favourites { get; init; }

    /// <summary>The dan dojo's boards for a series and the player's bests (null until the courses are ready).</summary>
    public Func<string, IReadOnlyDictionary<string, int>?, (IReadOnlyList<DaniSelectBoard> Boards, IReadOnlyList<DanCourseStatus?> Courses)?>? DanCourseBoards { get; init; }

    /// <summary>The song behind a dan board's song uid.</summary>
    public Func<int, SongDescriptor?>? DanSong { get; init; }

    /// <summary>The loaded entry's host (null outside the entry).</summary>
    public EntrySceneHost? Entry => _entry;

    IEntryScreen? IScreenHosts.Entry => _entry;

    public bool InsertEntryCard(ScoreProfile card) => _entry?.InsertCard(card) == true;

    /// <summary>The entry gives up (see EntrySceneHost.ReturnToAttract).</summary>
    public Action? ReturnToAttract { get; init; }

    public void RejectEntryCard() => _entry?.RejectCard();

    public bool EntryCardPending => _entry?.CardBusy == true;

    public bool RefuseEntryCard() => _entry?.RefuseCardInUse() == true;

    /// <summary>The name of an account chosen for a drum before the entry (its board shows it at the join).</summary>
    public Func<int, string?>? PlayerName { get; init; }

    /// <summary>The look of the player about to join a drum (see EntrySceneHost.PlayerLook).</summary>
    public Func<int, DonLook?>? PlayerLook { get; init; }

    /// <summary>A card is being read at entry (the coin message hides meanwhile).</summary>
    public Action<bool>? CardDialog { get; init; }

    /// <summary>The entry gave the card to a drum (EntryData).</summary>
    public Action<int, ScoreProfile>? CardClaimed { get; init; }
    private IndicatorParts? _parts;

    // --- The credit's state, set by the flow ---

    public ModeTutorialHostBinding? ModeTutorial { get; private set; }

    /// <summary>The player's best result per course hash, for the course list's stamps.</summary>
    public Func<IReadOnlyDictionary<string, int>>? DanBests { get; init; }

    /// <summary>The course list on screen.</summary>
    public DaniSelectHostBinding? DaniSelect { get; private set; }

    /// <summary>The course behind each of its boards (null: the back board).</summary>
    public IReadOnlyList<DanCourseStatus?> DanBoards { get; private set; } = [];

    /// <summary>The live show_oni setting (the Settings menu changes it while the game runs).</summary>
    public Func<bool>? ShowOni { get; init; }

    /// <summary>Home without the arcade HUD: the timer and song number are hidden.</summary>
    public Func<bool>? HideArcadeHud { get; init; }

    /// <summary>The live timer freeze setting: timers show but never run out.</summary>
    public Func<bool>? TimerFreeze { get; init; }

    private bool counting => countdown && TimerFreeze?.Invoke() != true;

    /// <summary>The boot's music credit pages there are (the copyright NUT's).</summary>
    public int CopyrightPages { get; init; }

    /// <summary>Whether TaikOnline answers (checked from boot on): the entry's mode select then offers the reward shop.</summary>
    public Func<bool>? ServerOnline { get; init; }

    /// <summary>Whether Green's AI battle movies are installed (the entry's mode select offers it only then).</summary>
    public bool AiBattleInstalled { get; init; } = true;

    /// <summary>The carded player whose Don Medals the credit's end shows (reward_shop over the game over); null: none.</summary>
    public Func<RewardShopPlayer?>? RewardPlayer { get; init; }

    /// <summary>The reward shop on stage (the medal screen or a shopping visit).</summary>
    public RewardShopHostBinding? RewardShop { get; set; }

    /// <summary>Called when a player joins at entry (coin mode updates the indicator panel).</summary>
    public Action<int>? EntryJoined { get; init; }

    /// <summary>Called with each layer's host id as a scene loads (the flow scopes per-scene state).</summary>
    public Action<string>? LayerLoading { get; init; }

    // --- The loaded hosts the flow polls ---

    /// <summary>The loaded song select's host (its mode switch).</summary>
    public SongSelectHostBinding? SongSelect { get; private set; }

    ISongSelectScreen? IScreenHosts.SongSelect => SongSelect;

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
    public TaikoCreditNext CreditNext => arcade.Home ? TaikoCreditNext.NextSong : TaikoCredit.Next(Credit.SongInfo.Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    private int endMessage => arcade.Home ? 0 : TaikoCredit.EndMessage(Credit.SongInfo.Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    // --- Host construction ---

    public LumenLayerHost Create(SceneLayerDefinition layer)
    {
        LayerLoading?.Invoke(layer.HostId);
        // A front-end scene movie starts the indicator parts its following layers attach to.
        if (layer.HostId == "reward-shop-visit")
            _parts = new IndicatorParts(IndicatorPartsScene.RewardShop, counting) { ShowCounter = HideArcadeHud?.Invoke() != true };
        // The medal screen: its name boards only.
        if (layer.HostId == "reward-shop")
            _parts = new IndicatorParts(IndicatorPartsScene.RewardShop, counting) { ShowCounter = false };
        if (layer.HostId == "player-entry")
        {
            _parts = new IndicatorParts(IndicatorPartsScene.Entry, counting) { ShowCounter = HideArcadeHud?.Invoke() != true };
            var session = new EntrySession { Coins = Coins, Card = Credit.EntryCard, SetupMode = Credit.EntrySetup };
            if (Credit.EntryRejoined is { } rejoined)
                session.PreJoin(rejoined);
            session.Entered += player => EntryJoined?.Invoke(player);
            session.CardClaimed += (side, card) => CardClaimed?.Invoke(side, card);
            _entry = new EntrySceneHost(_parts, session)
            {
                PlayVoice = sounds is null ? null : sounds.Frontend.PlayEntryVoice,
                PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
                CostumeIcon = static (type, id, name) => type is < 0 or > 2 ? null
                    : name ? CostumeIconTextures.NameKey(type, id) : CostumeIconTextures.Key(type, id),
                Don = don,
                ReturnToAttract = () => ReturnToAttract?.Invoke(),
                PlayerLook = side => PlayerLook?.Invoke(side),
                PlayerName = side => PlayerName?.Invoke(side),
                CardDialog = open => CardDialog?.Invoke(open),
                PreJoined = Credit.EntryRejoined,
                // Green always offers the shop (traced IsAvailableShop 1) and greys it itself for a drum without card
                // data. Fixed for the scene: an answer that changes rebuilds the boards (the shop popped in late).
                ShopAvailable = ServerOnline?.Invoke() == true ? static () => true : static () => false,
                AiBattleAvailable = AiBattleInstalled,
            };
            _entryPrevious = Credit.EntryRejoined is null ? 0 : 1; // SCENE_ATTRACT / SCENE_SONGSELECT
            Credit.EntryRejoined = null;
            Credit.EntryCard = null;
            if (Credit.EntryCardRejected)
                _entry.RejectCard();
            Credit.EntryCardRejected = false;
        }
        else if (layer.HostId == "song-select")
        {
            // time_counter has art for songs 1-4 (red when final); home's endless credit stays on 1.
            var next = arcade.Home ? 1 : Credit.SongsPlayed + 1;
            _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, counting)
            {
                MusicNumber = Math.Min(next, 4),
                AiPanels = Credit.AiBattle ? AiPanels?.Invoke() ?? AiBattlePanels.NewPlayer : AiBattlePanels.NewPlayer,
                FinalStage = !arcade.Home && next >= arcade.SongsPerSession,
                ShowCounter = HideArcadeHud?.Invoke() != true,
            };
        }
        return layer.HostId switch
        {
            "player-entry" => entryHost(_entry!),
            LayerRoles.IndicatorPart => partHost(_parts
                ?? throw new InvalidOperationException("Indicator part loaded without its scene movie."),
                Path.GetFileNameWithoutExtension(layer.MovieId)),
            "song-select" => songSelectHost(),
            GameplaySceneComposition.StaticHostId or GameplaySceneComposition.PlayerTwoHostId =>
                new LumenLayerHost(new TaikoGameplayHostBinding(() => Credit.SongInfo, sounds is null ? null : sounds.Frontend.PlayCue)),
            RainbowTransitionComposition.StaticHostId => new LumenLayerHost(null),
            LayerRoles.SystemIndicators => new LumenLayerHost(null),
            "result" => resultHost(),
            "ai-result" => aiResultHost(),
            "ai-result-section" => aiSectionResultHost(),
            "ai-remind" => aiRemindHost(),
            "ai-remind-bg" => new LumenLayerHost(null),
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
            "waiwai-tutorial" => modeTutorialHost(false),
            "ai-tutorial" => modeTutorialHost(true),
            "ai-tutorial-timer" => new LumenLayerHost(null, movie => ModeTutorial?.AttachTimer(movie)),
            "dani-select" => daniSelectHost(),
            "gameover" => gameOverHost(),
            "reward-shop" => rewardShopHost(),
            "reward-shop-visit" => rewardShopVisitHost(),
            "attract" => attractHost(),
            "boot" => new LumenLayerHost(null, bootCredits),
            _ => throw new KeyNotFoundException($"No Lumen host is configured for '{layer.HostId}'."),
        };
    }

    private void bootCredits(LumenPlayer player) =>
        AttractHostBinding.ShowCredits(player, CopyrightPages, CopyrightArt.Key);

    private LumenLayerHost entryHost(EntrySceneHost entry) => new(
        new LumenFrontendHostBinding(frontend, flow, don, entry),
        player =>
        {
            initializeEntry(player);
            entry.AttachEntry(player);
        });

    private void initializeEntry(LumenPlayer player) => EntrySceneHost.Prepare(player, don, _entryPrevious, Credit.EntryTrigger);

    private static LumenLayerHost partHost(IndicatorParts parts, string movie) =>
        new(null, player => parts.Attach(movie, player));

    // The song select movie of the mode (FlowScenes.SongSelectScene), whose patched genres the host fills.
    private string songSelectMovie => Patching.SongSelectGenrePatch.MovieNames[Credit.AiBattle ? 2 : Credit.Waiwai ? 1 : 0];

    private LumenLayerHost songSelectHost()
    {
        var binding = SongSelect = new SongSelectHostBinding(
            createSelectSession(_parts?.Countdown ?? true),
            songSelectSounds,
            don,
            parts: _parts,
            side: Credit.PlayerSide,
            twoPlayers: Credit.TwoPlayers,
            crowns: [Crowns?.Invoke(0), Crowns?.Invoke(1)],
            partner: Credit.PartnerSide)
        {
            Favourites = Favourites,
            ShowOni = ShowOni?.Invoke() == true,
            PatchedGenres = Patching.SongSelectGenrePatch.Applied(songSelectMovie),
            GenreColours = Patching.SongSelectGenrePatch.Colours(songSelectMovie),
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            RankingWanted = RankingWanted,
            Rankings = Rankings,
            AiBattle = Credit.AiBattle,
            SupportCounts = SupportCounts,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    /// <summary>AI battle's intermediate levels per song (below かんたん, ふつう, むずかしい, おに).</summary>
    public Func<SongDescriptor, int[]>? SupportCounts { get; init; }

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
        var binding = new ResultHostBinding(() => own, Credit.SongInfo.Stage, endMessage, don, sounds?.Results, 0)
        {
            Best = Credit.Best,
            Placement = Credit.Placement, // the ranking, as on a song's results
            AiBattle = true,
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            NormalResultClosed = () => AiSectionResult?.Start(),
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost aiRemindHost()
    {
        var binding = AiRemind = new AiRemindBinding(AiPanels?.Invoke() ?? AiBattlePanels.NewPlayer, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    /// <summary>AI battle's farewell, while it is on stage.</summary>
    public AiRemindBinding? AiRemind { get; private set; }

    // Traced SetWinTotalFlag(false) after a lost battle; sent with the section results' setup (once callbacks exist).
    private LumenLayerHost aiResultBackgroundHost() => new(null, player => AiResultBackground = player);

    /// <summary>AI battle's results backdrop (result_bg), while it is on stage.</summary>
    public LumenPlayer? AiResultBackground { get; private set; }

    private LumenLayerHost aiSectionResultHost()
    {
        var outcome = Credit.AiOutcome ?? throw new InvalidOperationException("AI battle results without a battle.");
        var binding = AiSectionResult = new AiSectionResultBinding(outcome, Credit.AiNumbers ?? AiResultNumbers.NewPlayer,
            sounds is null ? null : sounds.Frontend.PlayCue, Credit.AiSectionBest)
        {
            Backdrop = () => AiResultBackground,
        };
        return new LumenLayerHost(binding, player =>
        {
            binding.Attach(player);
            // Ours reads the battle's results from the provider.
            if (!player.UsesProvider)
                return;
            var numbers = Credit.AiNumbers ?? AiResultNumbers.NewPlayer;
            player.Publish("result.aiWon", LumenHostValue.FromBoolean(outcome.Won));
            for (var section = 0; section < outcome.Sections.Count; section++)
                player.PublishItem("result.aiSectionWon", section, LumenHostValue.FromBoolean(outcome.Sections[section].Won));
            player.Publish("result.aiRank", LumenHostValue.FromNumber(numbers.RankBefore));
            player.Publish("result.aiRankAfter", LumenHostValue.FromNumber(numbers.RankAfter));
            player.Publish("result.aiWins", LumenHostValue.FromNumber(numbers.WinsBefore));
        });
    }

    private LumenLayerHost resultHost()
    {
        WaiwaiResult = null;
        var plays = playResults();
        if (plays.Count == 0)
            throw new InvalidOperationException("Results loaded without a finished play.");
        var binding = new ResultHostBinding(() => plays, Credit.SongInfo.Stage, endMessage,
            don, sounds?.Results, Credit.PlayerSide)
        {
            Best = Credit.Best,
            Placement = Credit.Placement,
        };
        return new LumenLayerHost(binding, player =>
        {
            publishResults(player, plays);
            binding.Attach(player);
        });
    }

    // The provider's result.* for a results movie of ours, each play under its drum's number.
    private void publishResults(LumenPlayer player, IReadOnlyList<TaikoPlayResult> plays)
    {
        if (!player.UsesProvider)
            return;
        for (var index = 0; index < plays.Count; index++)
        {
            var play = plays[index];
            var drum = (plays.Count == 2 ? index : Credit.PlayerSide) + 1;
            void tell(string name, LumenHostValue value) => player.Publish(name, value, drum);
            tell("result.course", LumenHostValue.FromString(play.Course.ToString().ToLowerInvariant()));
            tell("result.score", LumenHostValue.FromNumber(play.Score));
            tell("result.best", Credit.Best(index) is { } best ? LumenHostValue.FromNumber(best) : LumenHostValue.Undefined);
            tell("result.good", LumenHostValue.FromNumber(play.Great));
            tell("result.ok", LumenHostValue.FromNumber(play.Good));
            tell("result.bad", LumenHostValue.FromNumber(play.Miss));
            tell("result.maxCombo", LumenHostValue.FromNumber(play.MaxCombo));
            tell("result.rolls", LumenHostValue.FromNumber(play.Rolls + play.BalloonHits));
            tell("result.gauge", LumenHostValue.FromNumber((double)play.GaugeSegments / TaikoSoulGauge.Segments));
            tell("result.cleared", LumenHostValue.FromBoolean(play.Cleared));
            tell("result.fullCombo", LumenHostValue.FromBoolean(play.FullCombo));
        }
    }

    /// <summary>The dan results on stage, while they are.</summary>
    public DaniResultHostBinding? DaniResult { get; private set; }

    private LumenLayerHost daniResultHost()
    {
        var binding = DaniResult = new DaniResultHostBinding(Credit.DanResult ?? throw new InvalidOperationException("Dan results loaded without a course play."),
            don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost waiwaiResultHost()
    {
        var outcome = Credit.WaiwaiOutcome?.Invoke() ?? throw new InvalidOperationException("Waiwai results without a Waiwai song.");
        var binding = WaiwaiResult = new WaiwaiResultHostBinding(outcome.GaugeSegments, outcome.DuetPercent,
            outcome.RareNotesHit, don, sounds?.WaiwaiResults);
        return new LumenLayerHost(binding, player =>
        {
            binding.Attach(player);
            binding.ShowArt(player, WaiwaiResultTextures.Background, WaiwaiResultTextures.Sentence(binding.Sentence), WaiwaiResultTextures.RareNote);
            // Ours reads Waiwai's results from the provider.
            if (!player.UsesProvider)
                return;
            player.Publish("result.waiwaiGauge", LumenHostValue.FromNumber(outcome.GaugeSegments));
            player.Publish("result.duet", LumenHostValue.FromNumber(outcome.DuetPercent));
            for (var note = 0; note < outcome.RareNotesHit.Count; note++)
                player.PublishItem("result.rareHit", note, LumenHostValue.FromBoolean(outcome.RareNotesHit[note]));
        });
    }

    private LumenLayerHost retryHost()
    {
        var binding = Retry = new RetryGameHostBinding(don, sounds?.Retry);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost modeTutorialHost(bool aiBattle)
    {
        Action? music = null;
        var binding = ModeTutorial = new ModeTutorialHostBinding(aiBattle, Credit.PlayerSide, Credit.TwoPlayers,
            sounds is null ? null : sounds.Frontend.PlayCue,
            name => (music = sounds?.Green.PrepareTutorialMusic(name)) is not null,
            () => music?.Invoke());
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost tutorialHost()
    {
        var binding = new TutorialHostBinding(Credit.PlayerSide, Credit.TwoPlayers, sounds is null ? null : sounds.Frontend.PlayCue, don);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost daniSelectHost()
    {
        var view = new SongSelectCatalogView(catalog);
        var songs = view.Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .GroupBy(static entry => entry.Song.Descriptor.Key).ToDictionary(static group => group.Key, static group => group.First());
        var binding = DaniSelect = new DaniSelectHostBinding(() =>
        {
            if (DanCourseBoards?.Invoke(Credit.DanSeries, DanBests?.Invoke()) is not var (boards, courses))
                return null;
            DanBoards = courses;
            return boards;
        }, Credit.PlayerSide, uid =>
            DanSong?.Invoke(uid) is { } song && songs.TryGetValue(song.Key, out var entry)
                ? textures.GetSongTitle(entry.Song, entry.Category.BoardStyle, SongBoardTextureKind.Expanded) : null,
            (challenge, kind) => DanChallengeArt.Key(Credit.DanSeries, challenge, kind),
            songSelectSounds, don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost gameOverHost()
    {
        var binding = GameOver = new GameOverHostBinding(sounds?.GameOver, Credit.PlayerSide, Credit.TwoPlayers,
            RewardPlayer?.Invoke() is null ? null : () => RewardShop?.Ended ?? false,
            static side => TaikoGuest.Profiles[side] is { Baid: > 0 });
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost rewardShopHost()
    {
        var player = RewardPlayer?.Invoke() ?? throw new InvalidOperationException("reward_shop without a carded player.");
        var (season, telop) = Waddamburo.Game.Scores.DonMedals.Season(DateOnly.FromDateTime(DateTime.Now));
        var binding = RewardShop = new RewardShopHostBinding(season, telop, player, sounds?.RewardShop, parts: _parts);
        var layer = rewardShopLayer(binding, season);
        if (player.Rewards is not { Count: > 0 })
            return layer;
        // The reward sliders' fills (rewardicon_/rewardname_<n>p_<index>, in PushReward order): a costume's icon and name
        // plate, a song's title.
        var songs = new SongSelectCatalogView(catalog).Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .Where(static entry => entry.Song.Descriptor.Key.Source == SongSourceKind.Stock)
            .GroupBy(static entry => entry.Song.Descriptor.Key.StableId).ToDictionary(static group => group.Key, static group => group.First());
        var rewards = player.Rewards.Where(reward => installed(reward.Type, reward.Id, reward.MusicId, songs.ContainsKey)).ToList();
        binding = RewardShop = new RewardShopHostBinding(season, telop, player with { Rewards = rewards }, sounds?.RewardShop, parts: _parts);
        layer = rewardShopLayer(binding, season);
        return new LumenLayerHost(binding, movie =>
        {
            layer.Initialize?.Invoke(movie);
            var side = $"{player.Side + 1}p";
            for (var index = 0; index < rewards.Count; index++)
            {
                var reward = rewards[index];
                if (reward.Type is 3 or 4 or 5)
                {
                    var set = reward.Type switch { 3 => 0, 5 => 1, _ => 2 };
                    movie.SetNativeFill($"rewardicon_{side}_{index}", CostumeIconTextures.Key(set, reward.Id));
                    movie.SetNativeFill($"rewardname_{side}_{index}", CostumeIconTextures.NameKey(set, reward.Id));
                }
                else if (reward.MusicId is { } musicId && songs.TryGetValue(musicId, out var entry))
                    movie.SetNativeFill($"rewardname_{side}_{index}", textures.GetTransitionTitle(entry.Song));
            }
        });
    }

    // The season's art in reward_shop's fills and the players' Don-chan in their costumes (don1pM / don2pM), as at the entry.
    private LumenLayerHost rewardShopLayer(RewardShopHostBinding binding, int season) => new(binding, movie =>
    {
        // ponytail: slot names guessed from the movie's strings; checked on screen.
        movie.SetNativeFill("season_BG", SeasonArt.Key("BG", season));
        movie.SetNativeFill("season_thema", SeasonArt.Key("thema", season));
        for (var side = 0; side < 2; side++)
        {
            movie.SetNativeFill($"season_fan_{side + 1}p", SeasonArt.Key("daishi_1p2p", season, side));
            movie.SetNativeFill($"medalbase{side + 1}p", SeasonArt.Key("medal_1p2p", season, side));
        }
        if (don is not null)
            DonLumenBinding.Attach(movie, don);
        binding.Attach(movie);
    });

    // Whether this install can show a good TaikOnline serves (the latest Green revision's): a stock song in the library, a
    // costume or puchi with its model. Tones always pass.
    private bool installed(int type, int id, string? musicId, Func<string, bool> hasSong) => type switch
    {
        1 => musicId is { } music && hasSong(music),
        3 or 4 or 5 or 7 => don?.HasCostume(type, id) ?? true,
        _ => true,
    };

    // The movie's GENRELABEL order (1-8).
    private static readonly string[] ShopGenres = ["J-POP", "アニメ", "ボーカロイド", "童謡", "バラエティ", "クラシック", "ゲームミュージック", "ナムコオリジナル"];

    /// <summary>
    /// The reward shop after a card entry (shopping mode): the season's goods TaikOnline serves, songs found in the stock
    /// library by musicid (their genre, stars, title and preview), costumes previewed on the player's Don.
    /// </summary>
    private LumenLayerHost rewardShopVisitHost()
    {
        var state = shop.WaitForGoods();
        // Each joined drum: a card whose shop came in buys with its medals; anyone else (a guest, a card the server missed)
        // only looks.
        int[] joined = Credit.TwoPlayers ? [0, 1] : [Credit.PlayerSide];
        var view = new SongSelectCatalogView(catalog);
        var songs = view.Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .Where(static entry => entry.Song.Descriptor.Key.Source == SongSourceKind.Stock)
            .GroupBy(static entry => entry.Song.Descriptor.Key.StableId).ToDictionary(static group => group.Key, static group => group.First());
        (SongSelectSong Song, SongSelectCategory Category)? song(int id) =>
            state.Goods.FirstOrDefault(goods => goods.Type == 1 && goods.Id == id)?.MusicId is { } musicId
            && songs.TryGetValue(musicId, out var entry) ? entry : null;
        bool[] owned(ShopGoods item) => [shop.Owns(0, item.No), shop.Owns(1, item.No)];
        var goods = state.Goods.Where(item => installed(item.Type, item.Id, item.MusicId, songs.ContainsKey)).Select(item => song(item.Id) is { } entry && item.Type == 1
            ? new RewardShopGoods(item, owned(item), Array.IndexOf(ShopGenres, entry.Category.AuthoredLabel) + 1,
                [.. new[] { TaikoCourse.Easy, TaikoCourse.Normal, TaikoCourse.Hard, TaikoCourse.Oni }.Select(course => entry.Song.Level(course) ?? 0)])
            : new RewardShopGoods(item, owned(item))).ToList();
        var dressed = new[] { true, true }; // each Don wears its player's own look
        var visit = new RewardShopVisit(goods, joined, [shop.Medals(0), shop.Medals(1)], shop.Purchase,
            (side, type, id) =>
            {
                // A song plays its preview; a costume (kigurumi 3, head 5, body 4) is worn alone; -2 (the exit) and songs bring back the
                // player's own look. True: the Don changes (the movie waits for FinishCostumeLoad).
                // ponytail: one preview player; the last cursor to move picks the song.
                if (type == 1 && song(id) is { Song.Descriptor: { AudioAsset: { } audio } descriptor })
                    previews.SetPreview(new SongPreviewRequest(descriptor.Key, audio, descriptor.PreviewStart ?? TimeSpan.Zero));
                else
                    previews.Silence();
                if (don is null || side is not (0 or 1))
                    return false;
                if (type is 3 or 4 or 5)
                {
                    don.SetCostume(side, new DonCostume(type == 3 ? id : null, type == 5 ? id : 0, type == 4 ? id : 0, 0));
                    dressed[side] = false;
                    return true;
                }
                if (dressed[side])
                    return false;
                don.SetLook(side, TaikoGuest.Profiles[side]?.Look);
                dressed[side] = true;
                return true;
            },
            (slot, type, id) => type switch
            {
                // The detail's song name is vertical, like Song Select's boards.
                1 when slot is 10 or 11 && song(id) is var (entry, category) =>
                    textures.GetSongTitle(entry, category.BoardStyle, SongBoardTextureKind.Expanded),
                3 or 4 or 5 => slot is 6 or 7 ? CostumeIconTextures.NameKey(iconSet(type), id) : CostumeIconTextures.Key(iconSet(type), id),
                _ => null,
            },
            static (type, id) => type is 3 or 4 or 5 ? CostumeIconTextures.Key(iconSet(type), id) : null) { ServerMessages = !arcade.Home };
        // Shop TYPE_SUIT 3 / TYPE_HEAD 5 / TYPE_BODY 4 → the icon sets kigurumi 0, head 1, body 2.
        static int iconSet(int type) => type switch { 3 => 0, 5 => 1, _ => 2 };
        var binding = RewardShop = new RewardShopHostBinding(state.SeasonId, state.Telop, null, sounds?.RewardShop, visit, don, _parts);
        return rewardShopLayer(binding, state.SeasonId);
    }

    private LumenLayerHost attractHost()
    {
        var binding = Attract = new AttractHostBinding(sounds?.Attract);
        return new LumenLayerHost(binding);
    }
}
