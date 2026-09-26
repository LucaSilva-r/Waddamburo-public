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

    /// <summary>A drum's player's crowns by chart key, read as Song Select loads (null: a guest).</summary>
    public Func<int, IReadOnlyDictionary<string, TaikoCrown>?>? Crowns { get; init; }

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
    public TaikoCreditNext CreditNext => TaikoCredit.Next(songInfo().Stage,
        [.. playResults().Select(play => play.Cleared)], arcade.SongsPerSession);

    private int endMessage => TaikoCredit.EndMessage(songInfo().Stage,
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
                CardDialog = open => CardDialog?.Invoke(open),
                CardClaimed = (side, card) => CardClaimed?.Invoke(side, card),
            };
            EntryCard = null;
            if (EntryCardRejected)
                _entry.RejectCard();
            EntryCardRejected = false;
            _entry.PlayerJoined += player => EntryJoined?.Invoke(player);
        }
        else if (layer.HostId == "song-select")
            _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, countdown);
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
                LumenHostValue.FromNumber(0),
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
        var binding = SongSelect = new SongSelectHostBinding(
            new SongSelectSession(
                new SongSelectCatalogView(catalog, Waiwai ? SongSelectMode.Waiwai : SongSelectMode.Normal,
                    modeSwitch: TwoPlayers),
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
            PlayCue = sounds is null ? null : sounds.Frontend.PlayCue,
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
            don, sounds?.Results, PlayerSide);
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
