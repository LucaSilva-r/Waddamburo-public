using Waddamburo.App.Audio;
using Waddamburo.App.Gameplay;
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
    Func<TaikoSongInfo> songInfo,
    bool countdown,
    Func<IReadOnlyList<TaikoPlayResult>> playResults,
    ArcadeSettings arcade,
    CoinBank? coins) : ILumenLayerHostFactory
{
    private EntrySceneHost? _entry;
    private int _entryPrevious;

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

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
    public bool EntryCardPending => _entry?.Card is not null;

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

    /// <summary>Two players browse Waiwai's song select (and may switch to the normal one).</summary>
    public bool Waiwai { get; set; }

    /// <summary>The tutorial was watched this credit: Song Select drops its folder.</summary>
    public bool TutorialSeen { get; set; }

    /// <summary>The live show_oni setting (the Settings menu changes it while the game runs).</summary>
    public Func<bool>? ShowOni { get; init; }

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

    /// <summary>The last search's results, listed first in Song Select (null: no search folder).</summary>
    public SongSearchResults? Search
    {
        get => _search;
        set => (_search, _searchShown) = (value, value is not null);
    }
    private SongSearchResults? _search;
    private bool _searchShown;
    private (CategoryKey Category, SongKey Song)? _lastPick;

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
            _parts = new IndicatorParts(IndicatorPartsScene.Entry, countdown);
            _entry = new EntrySceneHost(_parts)
            {
                Coins = coins,
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
            _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, countdown)
            {
                MusicNumber = Math.Min(next, 4),
                FinalStage = !arcade.Home && next >= arcade.SongsPerSession,
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
                new LumenLayerHost(new TaikoGameplayHostBinding(songInfo)),
            RainbowTransitionComposition.StaticHostId => new LumenLayerHost(null),
            SystemIndicators.HostId => new LumenLayerHost(null),
            "result" => resultHost(),
            "waiwai-result" => waiwaiResultHost(),
            "waiwai-result-board-0" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 0)),
            "waiwai-result-board-1" => new LumenLayerHost(null, static board => GuestNameBoard.Show(board, 1)),
            "waitinput" => new LumenLayerHost(null), // the game never calls its Start (traced)
            "retry" => retryHost(),
            "tutorial" => tutorialHost(),
            "gameover" => gameOverHost(),
            "attract" => attractHost(),
            "boot" => new LumenLayerHost(null),
            _ => throw new KeyNotFoundException($"No Lumen host is configured for '{layer.HostId}'."),
        };
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
            DonLumenBinding.Attach(player, don, DonPresentationLayout.OpposedPlayers);
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
            modeSwitch: TwoPlayers, library: linked.Length > 0 ? Library : null,
            links: linked.Where(library => library != Library), favourites: Favourites?.Songs, tutorial: !TutorialSeen,
            browse: OsuBrowse, search: Search);
        // Back from a library: the cursor starts on the folder that leads to it; otherwise on the last
        // song played, when this view lists it.
        _lastPick = SongSelect?.Picked ?? _lastPick;
        var (start, startSong) = (0, -1);
        if (_searchShown)
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
            crowns: [Crowns?.Invoke(0), Crowns?.Invoke(1)])
        {
            StartCategory = start,
            StartSong = startSong,
            Favourites = Favourites,
            ShowOni = ShowOni?.Invoke() == true,
            PatchedGenres = Waddamburo.Game.Patching.SongSelectGenrePatch.LastApplied,
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
            RankingWanted = Rankings is null ? null : Rankings.Want,
            Rankings = Rankings is null ? null : Rankings.For,
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
