using Waddamburo.Skins.Green.Art;
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
    Func<bool, SongSelectSession> createSelectSession,
    GameFlowSession flow,
    SongCatalogSnapshot catalog,
    ISongBoardTextureService textures,
    ISongSelectSoundController songSelectSounds,
    GreenScreenSounds? sounds,
    ILumenFrontendServices frontend,
    IDonPresentationController? don,
    Func<TaikoSongInfo> songInfo,
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

    public void TimeOut(SceneId screen)
    {
        if (screen == FlowScenes.AiRemind)
            AiRemind?.Exit();
    }

    public DanSelectChoice? DanSelectChoice => DaniSelect is { Terminated: true } list ? new(list.Back, list.Selected) : null;

    public bool RevivalSucceeded => Retry?.Succeeded == true;

    // Live: the service menu changes settings (e.g. songs per session) while the game runs.
    private ArcadeSettings arcade => arcadeSettings();

    private EntrySceneHost? _entry;
    private int _entryPrevious;

    /// <summary>The cabinet's coins (null on free play); replaced when the service menu changes the coin settings.</summary>
    public CoinBank? Coins { get; set; } = coins;

    public void EntryCoinsChanged() => _entry?.CoinsChanged();

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

    /// <summary>Each saved play's previous best on its chart, by play index (results' best score).</summary>
    public Func<int, long?>? Best { get; init; }

    public Func<int, RankingPlacement?>? Placement { get; init; }

    /// <summary>Songs started this credit (song select shows the next one's number).</summary>
    public Func<int>? SongsStarted { get; init; }

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
    public TaikoCreditNext CreditNext => arcade.Home ? TaikoCreditNext.NextSong : TaikoCredit.Next(songInfo().Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    private int endMessage => arcade.Home ? 0 : TaikoCredit.EndMessage(songInfo().Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    // --- Host construction ---

    public LumenLayerHost Create(SceneLayerDefinition layer)
    {
        LayerLoading?.Invoke(layer.HostId);
        // A front-end scene movie starts the indicator parts its following layers attach to.
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
            var next = arcade.Home ? 1 : (SongsStarted?.Invoke() ?? 0) + 1;
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
                new LumenLayerHost(new TaikoGameplayHostBinding(songInfo, sounds is null ? null : sounds.Frontend.PlayCue)),
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
        var binding = new ResultHostBinding(() => own, songInfo().Stage, endMessage, don, sounds?.Results, 0)
        {
            Best = Best,
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
        var binding = AiSectionResult = new AiSectionResultBinding(outcome, Credit.AiNumbers ?? AiResultNumbers.NewPlayer with { ShowProgress = false },
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
        var binding = new ResultHostBinding(() => plays, songInfo().Stage, endMessage,
            don, sounds?.Results, Credit.PlayerSide)
        {
            Best = Best,
            Placement = Placement,
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
            tell("result.best", Best?.Invoke(index) is { } best ? LumenHostValue.FromNumber(best) : LumenHostValue.Undefined);
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
        var binding = GameOver = new GameOverHostBinding(sounds?.GameOver, Credit.PlayerSide, Credit.TwoPlayers);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost attractHost()
    {
        var binding = Attract = new AttractHostBinding(sounds?.Attract);
        return new LumenLayerHost(binding);
    }
}
