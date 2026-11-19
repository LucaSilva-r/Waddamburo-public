using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
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
    bool countdown,
    Func<IReadOnlyList<TaikoPlayResult>> playResults,
    Func<ArcadeSettings> arcadeSettings,
    CoinBank? coins,
    CreditSession credit,
    SongSelectMemory memory,
    LoadedHosts loaded) : ILumenLayerHostFactory
{
    // Live: the service menu changes settings (e.g. songs per session) while the game runs.
    private ArcadeSettings arcade => arcadeSettings();

    private int _entryPrevious;

    /// <summary>The cabinet's coins (null on free play); replaced when the service menu changes the coin settings.</summary>
    public CoinBank? Coins { get; set; } = coins;

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

    /// <summary>A drum's play history as chart keys, newest first (this PC's plays and the server's); null: none.</summary>
    public Func<int, IReadOnlyList<string>?>? RecentCharts { get; init; }

    /// <summary>The songs marked favourite (a folder per library, a red coin icon on their boards).</summary>
    public SongFavourites? Favourites { get; init; }




    /// <summary>AI battle's song select panels: the player's rank, points to the next, wins, wins to the next reward.</summary>
    public Func<AiBattlePanels>? AiPanels { get; init; }

    /// <summary>Song select's score windows from the server (null offline).</summary>
    public Online.SongRankings? Rankings { get; init; }






    /// <summary>The entry gives up (see EntrySceneHost.ReturnToAttract).</summary>
    public Action? ReturnToAttract { get; set; }





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








    /// <summary>The dan dojo's courses (null: no such folder in this run).</summary>
    public DanCourses? Dan { get; set; }



    /// <summary>The player's best result per course hash, for the course list's stamps.</summary>
    public Func<IReadOnlyDictionary<string, int>>? DanBests { get; set; }







    // Home without the arcade HUD: the timer and song number are hidden.
    private bool hideArcadeHud => arcade.Home && !arcade.ArcadeHud;

    private bool counting => countdown && !arcade.TimerFreeze;


    /// <summary>The boot's music credit pages there are (the copyright NUT's).</summary>
    public int CopyrightPages { get; init; }

    /// <summary>Whether Song Select offers the Online folder.</summary>
    public Func<bool>? OnlineAvailable { get; init; }

    /// <summary>The player is in an online room: songs are played together, so no dan dojo.</summary>
    public Func<bool>? InOnlineRoom { get; init; }

    /// <summary>A song another player sent this session (Song Select lists it while it's the online song).</summary>
    public Func<SongKey, SongDescriptor?>? Received { get; init; }



    /// <summary>Waiwai's numbers for its results screen, from the finished song.</summary>
    public Func<WaiwaiOutcome?>? WaiwaiOutcome { get; set; }

    /// <summary>Called when a player joins at entry (coin mode updates the indicator panel).</summary>
    public Action<int>? EntryJoined { get; set; }

    /// <summary>Called with each layer's host id as a scene loads (the flow scopes per-scene state).</summary>
    public Action<string>? LayerLoading { get; set; }

    // --- The loaded hosts the flow polls ---






    /// <summary>What follows the results (see <see cref="TaikoCredit"/>).</summary>
    // Home: an endless credit, song after song, no revival and no end message.
    public TaikoCreditNext CreditNext => arcade.Home ? TaikoCreditNext.NextSong : TaikoCredit.Next(credit.SongInfo.Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    private int endMessage => arcade.Home ? 0 : TaikoCredit.EndMessage(credit.SongInfo.Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);



    // --- Host construction ---

    public LumenLayerHost Create(SceneLayerDefinition layer)
    {
        LayerLoading?.Invoke(layer.HostId);
        // A front-end scene movie starts the indicator parts its following layers attach to.
        if (layer.HostId == "reward-shop-visit")
            _parts = new IndicatorParts(IndicatorPartsScene.RewardShop, counting) { ShowCounter = !hideArcadeHud };
        if (layer.HostId == "player-entry")
        {
            _parts = new IndicatorParts(IndicatorPartsScene.Entry, counting) { ShowCounter = !hideArcadeHud };
            loaded.Entry = new EntrySceneHost(_parts)
            {
                Coins = Coins,
                PlayVoice = sounds is null ? null : sounds.Frontend.PlayEntryVoice,
                PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
                CostumeIcon = static (type, id, name) => type is < 0 or > 2 ? null
                    : name ? CostumeIconTextures.NameKey(type, id) : CostumeIconTextures.Key(type, id),
                Card = credit.EntryCard,
                ShopAvailable = () => credit.ShopGoods is not null,
                Don = don,
                ReturnToAttract = () => ReturnToAttract?.Invoke(),
                PlayerLook = side => PlayerLook?.Invoke(side),
                PlayerName = side => PlayerName?.Invoke(side),
                CardDialog = open => CardDialog?.Invoke(open),
                CardClaimed = (side, card) => CardClaimed?.Invoke(side, card),
                SetupMode = credit.EntrySetup,
                PreJoined = credit.EntryRejoined,
            };
            _entryPrevious = credit.EntryRejoined is null ? 0 : 1; // SCENE_ATTRACT / SCENE_SONGSELECT
            credit.EntryRejoined = null;
            credit.EntryCard = null;
            if (credit.EntryCardRejected)
                loaded.Entry!.RejectCard();
            credit.EntryCardRejected = false;
            loaded.Entry!.PlayerJoined += player => EntryJoined?.Invoke(player);
        }
        else if (layer.HostId == "song-select")
        {
            // time_counter has art for songs 1-4 (red when final); home's endless credit stays on 1.
            var next = arcade.Home ? 1 : credit.SongsPlayed + 1;
            _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, counting)
            {
                MusicNumber = Math.Min(next, 4),
                AiPanels = credit.AiBattle ? AiPanels?.Invoke() ?? AiBattlePanels.NewPlayer : AiBattlePanels.NewPlayer,
                FinalStage = !arcade.Home && next >= arcade.SongsPerSession,
                ShowCounter = !hideArcadeHud,
            };
        }
        return layer.HostId switch
        {
            "player-entry" => entryHost(loaded.Entry!),
            IndicatorParts.HostId => partHost(_parts
                ?? throw new InvalidOperationException("Indicator part loaded without its scene movie."),
                Path.GetFileNameWithoutExtension(layer.MovieId)),
            "song-select" => songSelectHost(),
            GameplaySceneComposition.StaticHostId or GameplaySceneComposition.PlayerTwoHostId =>
                new LumenLayerHost(new TaikoGameplayHostBinding(() => credit.SongInfo, sounds is null ? null : sounds.Frontend.PlayCue)),
            RainbowTransitionComposition.StaticHostId => new LumenLayerHost(null),
            SystemIndicators.HostId => new LumenLayerHost(null),
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
            "ai-tutorial-timer" => new LumenLayerHost(null, movie => loaded.ModeTutorial?.AttachTimer(movie)),
            "dani-select" => daniSelectHost(),
            "gameover" => gameOverHost(),
            "reward-shop" => rewardShopHost(),
            "reward-shop-visit" => rewardShopVisitHost(),
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
                LumenHostValue.FromNumber(credit.EntryTrigger)]))
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

    // The song select movie of the mode (FlowScenes.SongSelectScene), whose patched genres the host fills.
    private string songSelectMovie => Waddamburo.Game.Patching.SongSelectGenrePatch.MovieNames[credit.AiBattle ? 2 : credit.Waiwai ? 1 : 0];

    // "Only what I earned": the stock songs locked for every joined player (a guest, or a player who unlocks everything, has
    // none locked, so a song shows when anyone at the drums may play it).
    private HashSet<SongKey> lockedSongs()
    {
        int[] sides = credit.TwoPlayers ? [0, 1] : [credit.PlayerSide];
        var locked = sides.Select(side => TaikoGuest.Profiles[side]?.Locked?.Songs ?? []).Aggregate(
            static (shared, songs) => [.. shared.Intersect(songs)]);
        return [.. locked.Select(static musicId => new SongKey(SongSourceKind.Stock, musicId))];
    }

    private LumenLayerHost songSelectHost()
    {
        var linked = credit.Waiwai ? Array.Empty<SongSourceKind>() : memory.Libraries();
        var view = new SongSelectCatalogView(catalog.Without(lockedSongs()), credit.Waiwai ? SongSelectMode.Waiwai : SongSelectMode.Normal,
            modeSwitch: credit.TwoPlayers, library: linked.Length > 0 && memory.OnlineSong is null ? memory.Library : null,
            // AI battle too: its song select takes the custom folders (SongSelectGenrePatch), the other libraries' among them.
            links: linked.Where(library => library != memory.Library), favourites: Favourites?.Songs, tutorial: !(arcade.Home && arcade.TutorialSeen) && !(credit.AiBattle ? credit.AiTutorialSeen : credit.Waiwai ? credit.WaiwaiTutorialSeen : credit.TutorialSeen),
            browse: memory.OsuBrowse, search: memory.Search, // As on the game: no dan dojo for two players, or without an account (it works offline once its list is saved).
            danDojo: !credit.AiBattle && Dan?.Available == true && !credit.TwoPlayers && TaikoGuest.Profiles[credit.PlayerSide] is not null
                && InOnlineRoom?.Invoke() != true,
            danSeries: arcade.Home ? Dan?.Series : null,
            online: !credit.Waiwai && !credit.AiBattle && !credit.TwoPlayers && OnlineAvailable?.Invoke() == true, onlySong: memory.OnlineSong, received: Received,
            courseStar: memory.OnlineSong is null, rollFolder: arcade.RandomSongFolder,
            recentCharts: credit.TwoPlayers || memory.OnlineSong is not null ? null : RecentCharts?.Invoke(credit.PlayerSide));
        var (start, startSong) = memory.Start(view, loaded.SongSelect?.Picked);
        var binding = loaded.SongSelect = new SongSelectHostBinding(
            new SongSelectSession(
                view,
                textures,
                previews,
                playRequests),
            songSelectSounds,
            don,
            parts: _parts,
            side: credit.PlayerSide,
            twoPlayers: credit.TwoPlayers,
            crowns: [Crowns?.Invoke(0), Crowns?.Invoke(1)],
            partner: credit.PartnerSide)
        {
            StartCategory = start,
            ForcePick = memory.OnlineSong is not null,
            StartSong = startSong,
            Favourites = Favourites,
            ShowOni = arcade.ShowOni,
            PatchedGenres = Waddamburo.Game.Patching.SongSelectGenrePatch.Applied(songSelectMovie),
            GenreColours = Waddamburo.Game.Patching.SongSelectGenrePatch.Colours(songSelectMovie),
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            RankingWanted = Rankings is null ? null : Rankings.Want,
            Rankings = Rankings is null ? null : Rankings.For,
            AiBattle = credit.AiBattle,
            SupportCounts = SupportCounts,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    /// <summary>AI battle's intermediate levels per song (below かんたん, ふつう, むずかしい, おに).</summary>
    public Func<SongDescriptor, int[]>? SupportCounts { get; set; }





    // AI battle's normal results: the player's play only (AI-don's lane is not shown); closing them starts the sections'.
    private LumenLayerHost aiResultHost()
    {
        loaded.WaiwaiResult = null;
        var plays = playResults();
        if (plays.Count == 0)
            throw new InvalidOperationException("Results loaded without a finished play.");
        IReadOnlyList<TaikoPlayResult> own = [plays[0]];
        var binding = new ResultHostBinding(() => own, credit.SongInfo.Stage, endMessage, don, sounds?.Results, 0)
        {
            Best = credit.Best,
            Placement = credit.Placement, // the ranking, as on a song's results
            AiBattle = true,
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            NormalResultClosed = () => loaded.AiSectionResult?.Start(),
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost aiRemindHost()
    {
        var binding = loaded.AiRemind = new AiRemindBinding(AiPanels?.Invoke() ?? AiBattlePanels.NewPlayer, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }


    // Traced SetWinTotalFlag(false) after a lost battle; sent with the section results' setup (once callbacks exist).
    private LumenLayerHost aiResultBackgroundHost() => new(null, player => loaded.AiResultBackground = player);


    private LumenLayerHost aiSectionResultHost()
    {
        var outcome = credit.AiOutcome ?? throw new InvalidOperationException("AI battle results without a battle.");
        var binding = loaded.AiSectionResult = new AiSectionResultBinding(outcome, credit.AiNumbers ?? AiResultNumbers.NewPlayer with { ShowProgress = false },
            sounds is null ? null : sounds.Frontend.PlayCue, credit.AiSectionBest)
        {
            Backdrop = () => loaded.AiResultBackground,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost resultHost()
    {
        loaded.WaiwaiResult = null;
        var plays = playResults();
        if (plays.Count == 0)
            throw new InvalidOperationException("Results loaded without a finished play.");
        var binding = new ResultHostBinding(() => plays, credit.SongInfo.Stage, endMessage,
            don, sounds?.Results, credit.PlayerSide)
        {
            Best = credit.Best,
            Placement = credit.Placement,
        };
        return new LumenLayerHost(binding, binding.Attach);
    }



    private LumenLayerHost daniResultHost()
    {
        var binding = loaded.DaniResult = new DaniResultHostBinding(credit.DanResult ?? throw new InvalidOperationException("Dan results loaded without a course play."),
            don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost waiwaiResultHost()
    {
        var outcome = WaiwaiOutcome?.Invoke() ?? throw new InvalidOperationException("Waiwai results without a Waiwai song.");
        var binding = loaded.WaiwaiResult = new WaiwaiResultHostBinding(outcome.GaugeSegments, outcome.DuetPercent,
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
        var binding = loaded.Retry = new RetryGameHostBinding(don, sounds?.Retry);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost modeTutorialHost(bool aiBattle)
    {
        Action? music = null;
        var binding = loaded.ModeTutorial = new ModeTutorialHostBinding(aiBattle, credit.PlayerSide, credit.TwoPlayers,
            sounds is null ? null : sounds.Frontend.PlayCue,
            name => (music = sounds?.Bank.PrepareTutorialMusic(name)) is not null,
            () => music?.Invoke());
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost tutorialHost()
    {
        var binding = new TutorialHostBinding(credit.PlayerSide, credit.TwoPlayers, sounds is null ? null : sounds.Frontend.PlayCue, don);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost daniSelectHost()
    {
        var view = new SongSelectCatalogView(catalog);
        var songs = view.Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .GroupBy(static entry => entry.Song.Descriptor.Key).ToDictionary(static group => group.Key, static group => group.First());
        var binding = loaded.DaniSelect = new DaniSelectHostBinding(() =>
        {
            if (!Dan!.Ready)
                return null;
            var (boards, courses) = Dan.Boards(credit.DanSeries, DanBests?.Invoke());
            loaded.DanBoards = courses;
            return boards;
        }, credit.PlayerSide, uid =>
            Dan!.Song(uid) is { } song && songs.TryGetValue(song.Key, out var entry)
                ? textures.GetSongTitle(entry.Song, entry.Category.BoardStyle, SongBoardTextureKind.Expanded) : null,
            (challenge, kind) => DanChallengeArt.Key(credit.DanSeries, challenge, kind),
            songSelectSounds, don, sounds is null ? null : sounds.Frontend.PlayCue);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost gameOverHost()
    {
        var binding = loaded.GameOver = new GameOverHostBinding(sounds?.GameOver, credit.PlayerSide, credit.TwoPlayers,
            credit.RewardPlayer is null ? null : () => loaded.RewardShop?.Ended ?? false);
        return new LumenLayerHost(binding, binding.Attach);
    }

    private LumenLayerHost rewardShopHost()
    {
        var player = credit.RewardPlayer ?? throw new InvalidOperationException("reward_shop without a carded player.");
        var (season, telop) = Waddamburo.Game.Scores.DonMedals.Season(DateOnly.FromDateTime(DateTime.Now));
        var binding = loaded.RewardShop = new RewardShopHostBinding(season, telop, player, sounds?.RewardShop);
        return rewardShopLayer(binding, season);
    }

    // The season's art in reward_shop's fills and the players' Don-chan in their costumes (don1pM / don2pM), as at the entry.
    private LumenLayerHost rewardShopLayer(RewardShopHostBinding binding, int season) => new(binding, movie =>
    {
        // ponytail: slot names guessed from the movie's strings; checked on screen.
        movie.SetNativeFill("season_BG", Presentation.SeasonArt.Key("BG", season));
        movie.SetNativeFill("season_thema", Presentation.SeasonArt.Key("thema", season));
        for (var side = 0; side < 2; side++)
        {
            movie.SetNativeFill($"season_fan_{side + 1}p", Presentation.SeasonArt.Key("daishi_1p2p", season, side));
            movie.SetNativeFill($"medalbase{side + 1}p", Presentation.SeasonArt.Key("medal_1p2p", season, side));
        }
        if (don is not null)
            DonLumenBinding.Attach(movie, don);
        binding.Attach(movie);
    });

    // The movie's GENRELABEL order (1-8).
    private static readonly string[] ShopGenres = ["J-POP", "アニメ", "ボーカロイド", "童謡", "バラエティ", "クラシック", "ゲームミュージック", "ナムコオリジナル"];

    /// <summary>
    /// The reward shop after a card entry (shopping mode): the season's goods TaikOnline serves, songs found in the stock
    /// library by musicid (their genre, stars, title and preview), costumes previewed on the player's Don.
    /// </summary>
    private LumenLayerHost rewardShopVisitHost()
    {
        var state = credit.ShopGoods ?? throw new InvalidOperationException("reward_shop without a card's shop.");
        // Each joined drum: a card whose shop came in buys with its medals; anyone else (a guest, a card the server missed)
        // only looks.
        int[] joined = credit.TwoPlayers ? [0, 1] : [credit.PlayerSide];
        var shops = credit.Shops.Select(shop => shop?.State.IsCompletedSuccessfully == true ? shop : null).ToArray();
        var view = new SongSelectCatalogView(catalog);
        var songs = view.Categories.SelectMany(category => category.Songs.Select(song => (Song: song, Category: category)))
            .Where(static entry => entry.Song.Descriptor.Key.Source == SongSourceKind.Stock)
            .GroupBy(static entry => entry.Song.Descriptor.Key.StableId).ToDictionary(static group => group.Key, static group => group.First());
        (SongSelectSong Song, SongSelectCategory Category)? song(int id) =>
            state.Goods.FirstOrDefault(goods => goods.Type == 1 && goods.Id == id)?.MusicId is { } musicId
            && songs.TryGetValue(musicId, out var entry) ? entry : null;
        bool[] owned(ShopGoods item) => [.. shops.Select(shop => shop?.State.Result.Goods.Any(goods => goods.No == item.No && goods.Owned) == true)];
        var goods = state.Goods.Select(item => song(item.Id) is { } entry && item.Type == 1
            ? new RewardShopGoods(item, owned(item), Array.IndexOf(ShopGenres, entry.Category.AuthoredLabel) + 1,
                [.. new[] { TaikoCourse.Easy, TaikoCourse.Normal, TaikoCourse.Hard, TaikoCourse.Oni }.Select(course => entry.Song.Level(course) ?? 0)])
            : new RewardShopGoods(item, owned(item))).ToList();
        var dressed = new[] { true, true }; // each Don wears its player's own look
        var visit = new RewardShopVisit(goods, joined, [.. shops.Select(shop => shop?.State.Result.Medals)],
            async (side, no) =>
            {
                if (shops[side] is not { } shop)
                    return null;
                try
                {
                    var answer = await shop.Purchase(no).ConfigureAwait(false);
                    if (answer.Result == "ok" && state.Goods.FirstOrDefault(goods => goods.No == no) is { } bought
                        && TaikoGuest.Profiles[side] is { Locked: { } locks } profile)
                        TaikoGuest.Profiles[side] = profile with { Locked = locks.Unlock(bought.Type, bought.Id, bought.MusicId) };
                    return answer;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"Warning SHOP: the purchase failed ({exception.Message}).");
                    return null;
                }
            },
            (side, type, id) =>
            {
                // A song plays its preview; a costume (head 5, body 4) is worn alone; -2 (the exit) and songs bring back the
                // player's own look. True: the Don changes (the movie waits for FinishCostumeLoad).
                // ponytail: one preview player; the last cursor to move picks the song.
                if (type == 1 && song(id) is { Song.Descriptor: { AudioAsset: { } audio } descriptor })
                    previews.SetPreview(new SongPreviewRequest(descriptor.Key, audio, descriptor.PreviewStart ?? TimeSpan.Zero));
                else
                    previews.Silence();
                if (don is null || side is not (0 or 1))
                    return false;
                if (type is 4 or 5)
                {
                    don.SetCostume(side, new DonCostume(null, type == 5 ? id : 0, type == 4 ? id : 0, 0));
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
                1 when slot is 10 or 11 && song(id) is var (entry, category) =>
                    textures.GetSongTitle(entry, category.BoardStyle, SongBoardTextureKind.Expanded),
                4 or 5 => slot is 6 or 7 ? CostumeIconTextures.NameKey(type == 5 ? 1 : 2, id) : CostumeIconTextures.Key(type == 5 ? 1 : 2, id),
                _ => null,
            },
            static (type, id) => type is 4 or 5 ? CostumeIconTextures.Key(type == 5 ? 1 : 2, id) : null);
        var binding = loaded.RewardShop = new RewardShopHostBinding(state.SeasonId, state.Telop, null, sounds?.RewardShop, visit, don, _parts);
        return rewardShopLayer(binding, state.SeasonId);
    }

    private LumenLayerHost attractHost()
    {
        var binding = loaded.Attract = new AttractHostBinding(sounds?.Attract);
        return new LumenLayerHost(binding);
    }
}
