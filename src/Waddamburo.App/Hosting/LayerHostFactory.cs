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

    /// <summary>The playing profile's crowns by chart key, read as Song Select loads (null: no profile).</summary>
    public Func<IReadOnlyDictionary<string, TaikoCrown>>? Crowns { get; init; }
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
                CostumeIcon = static (type, id, name) => type is < 0 or > 2 ? null
                    : name ? CostumeIconTextures.NameKey(type, id) : CostumeIconTextures.Key(type, id),
            };
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
            DonLumenBinding.Attach(player, don, DonPresentationLayout.OpposedPlayers);
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
            // ponytail: one profile (--baid) until login; two players get per-side crowns with it.
            crowns: TwoPlayers ? null : Crowns?.Invoke());
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
