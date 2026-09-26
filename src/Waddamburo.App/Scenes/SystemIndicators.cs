using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Scenes;

/// <summary>Which state the always-on indicator overlays show (traced per game scene).</summary>
internal enum IndicatorScene
{
    Boot, // kidou: the card prompt stays off (traced SetOnline(0) until the attract loop)
    Attract,
    AttractPrompt, // caution screen / title: "hit the drum to start"
    Entry,
    SongSelect,
    Gameplay,
    Result,
}

/// <summary>
/// The indicator overlays the game keeps over every scene: network status, the card prompt and the
/// coin/free-play message. Loaded once; the game drives them with per-frame callbacks and shows or
/// hides them per scene (traced, research/traces/session1.md). Depths -3000 / -950: msg_coins sits
/// under the intermission (-2000), the other two above everything.
/// </summary>
internal sealed class SystemIndicators
{
    public const string HostId = "system-indicators";
    private static readonly string[] IndicatorMovies = ["network_icon", "msg_banapass", "msg_coins"];

    private readonly LumenPlayer _network;
    private readonly LumenPlayer _card;
    private readonly LumenPlayer _coins;
    private bool _cardVisible;
    private bool _coinsVisible;
    private bool _online;
    private bool _joined;
    private IndicatorScene _scene;
    private int _messageDelay = -1;

    public SystemIndicators(LumenGameSceneInstance scene)
    {
        Scene = scene;
        LumenPlayer movie(string name) => scene.Player.Layers[scene.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == name)].Player;
        _network = movie("network_icon");
        _card = movie("msg_banapass");
        _coins = movie("msg_coins");
    }

    public LumenGameSceneInstance Scene { get; }

    /// <summary>The cabinet's credits in coin mode; null in free play.</summary>
    public CoinBank? Coins { get; init; }

    /// <summary>The (expected) player's drum: 0 left, 1 right. The panels mirror for the right one (traced).</summary>
    public int Side { get; set; }

    /// <summary>Both drums have a player: nobody is left to prompt (traced session9-2p).</summary>
    public bool TwoPlayers { get; set; }

    public static SceneDefinition Definition(SceneId id) => new(SceneDefinition.CurrentVersion, id,
        IndicatorMovies.Select(name => new SceneLayerDefinition(
            "indicator/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity, HostId)));

    public void SetScene(IndicatorScene scene)
    {
        var attract = scene is IndicatorScene.Attract or IndicatorScene.AttractPrompt;
        _scene = scene;
        if (scene != IndicatorScene.Entry)
            _cardDialog = false; // left the entry mid-dialog (F1)
        _online = scene != IndicatorScene.Boot;
        _cardVisible = attract || scene == IndicatorScene.Entry;
        // The free-play/coin message first appears with the attract title (traced SetVisible* calls);
        // during the boot screens it stays off.
        _coinsVisible = scene is not (IndicatorScene.Gameplay or IndicatorScene.Boot);
        call(_card, "SetScene", LumenHostValue.FromNumber(scene == IndicatorScene.Entry ? 1 : 0));
        // Song Select shows the unjoined player's message; the authored indicator movie
        // positions it on the available side and runs its fade animation.
        var prompt = scene is IndicatorScene.AttractPrompt or IndicatorScene.Entry
            || scene == IndicatorScene.SongSelect && !TwoPlayers;
        call(_coins, "SetScene", LumenHostValue.FromNumber(scene switch
        {
            IndicatorScene.Entry => 1,
            IndicatorScene.SongSelect or IndicatorScene.Gameplay => 2,
            IndicatorScene.Result => 3,
            _ => 0,
        }));
        // sic, the movie's spelling. A card read as the entry loads opens its dialog before this runs.
        call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(prompt && !_cardDialog));
        call(_coins, "SetVisibleCoinType", LumenHostValue.FromBoolean(!attract || prompt));
        _joined = scene is IndicatorScene.SongSelect or IndicatorScene.Gameplay or IndicatorScene.Result;
        if (Coins is not null)
        {
            // Coin mode (traced session2-coins): coin type 0, the credit count, and per side the
            // credits still missing (-1 once joined). The split panel only comes with a join.
            call(_coins, "SetCoinType", LumenHostValue.FromNumber(0));
            CoinsChanged();
            return;
        }
        // In Song Select the joined player has no prompt (-1); the unjoined drum uses
        // message 2. The indicator movie positions and animates each side itself.
        var joinedMessage = scene is IndicatorScene.Entry or IndicatorScene.SongSelect ? -1 : 0;
        var otherMessage = scene == IndicatorScene.SongSelect ? 2 : 0;
        setSideNumbers(joinedMessage, otherMessage);
        if (scene == IndicatorScene.Entry)
        {
            // The entry then switches the panel to its split (per-player) scene, open on the other side.
            splitPanel();
            // SetScene(4) hides both messages (isMSGVisible off); the game turns them back on before
            // the numbers (traced ~0.9 s later, after Don-chan's entry motion).
            if (!_cardDialog)
                call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(true));
            setSideNumbers(-1, 0);
        }
        // SetCoinType seeks the authored movie to its scene label. Repeating it every
        // frame restarts the join message before its fade timeline can advance.
        call(_coins, "SetCoinType", LumenHostValue.FromNumber(2)); // free play
    }

    /// <summary>A player joined at entry: the split panel and the start message go away.</summary>
    public void EntryJoined()
    {
        if (Coins is not null && !TwoPlayers)
        {
            // Traced: the split panel opens for the other side, whose message stays up with its price.
            _joined = true;
            splitPanel();
            CoinsChanged();
            // SetScene(4) fades both messages out; the game shows the other drum's again after
            // Don-chan's entry motion (traced 0.85 s later), which Advance does.
            _messageDelay = 51;
            return;
        }
        // Free play, or the second player: the panel closes on both sides.
        _messageDelay = -1;
        call(_coins, "SetScene", LumenHostValue.FromNumber(4));
        call(_coins, "SetVisibleSplitPanel", LumenHostValue.FromBoolean(false), LumenHostValue.FromBoolean(false));
        call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(false));
    }

    /// <summary>
    /// A card is being read at entry: the game hides the coin message until a drum takes the card
    /// (traced session14-card-entry: SetVisibieMsg false at the read, true after EntryData).
    /// </summary>
    public void CardDialog(bool open)
    {
        _cardDialog = open;
        _messageDelay = -1;
        call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(!open));
        if (!open)
            CoinsChanged();
    }

    // A card dialog is open: nothing may bring the coin message back over it (user-confirmed: the
    // game shows none while the dialog is up, even when coins are inserted meanwhile).
    private bool _cardDialog;

    /// <summary>Coin mode: shows the current credits and what each side still needs.</summary>
    public void CoinsChanged()
    {
        if (Coins is not { } bank) return;
        var settings = bank.Settings;
        call(_coins, "SetCoinNum", LumenHostValue.FromNumber(bank.Credits));
        if (_cardDialog)
            return;
        // Before anyone joins, P2's number is the full two-player price (traced 2 / 4 at 0 credits).
        if (_joined)
            setSideNumbers(-1, bank.Missing(1));
        else
            setMessageNumbers(Math.Max(0, settings.CreditsOnePlayer - bank.Credits),
                Math.Max(0, settings.CreditsTwoPlayers - bank.Credits));
    }

    // SetScene(4) + SetVisibleSplitPanel(left, right): the panel opens on the drum without a player
    // (traced (false, true) for a left player, (true, false) for a right one).
    private void splitPanel()
    {
        call(_coins, "SetScene", LumenHostValue.FromNumber(4));
        call(_coins, "SetVisibleSplitPanel", LumenHostValue.FromBoolean(Side == 1), LumenHostValue.FromBoolean(Side == 0));
    }

    /// <summary>
    /// Message numbers for the joined player's drum and the other one. SetMsgNum indexes the drum
    /// (traced Song Select: (0, -1), (1, 2) for a left player, (0, 2), (1, -1) for a right one), except
    /// on entry's split panel, which gets the left player's numbers whichever drum joined.
    /// </summary>
    private void setSideNumbers(int own, int other)
    {
        if (Side == 1 && _scene != IndicatorScene.Entry) setMessageNumbers(other, own);
        else setMessageNumbers(own, other);
    }

    private void setMessageNumbers(int player1, int player2 = 0)
    {
        // SetMsgNum(player, coins still needed); -1 = enough.
        call(_coins, "SetMsgNum", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(player1));
        call(_coins, "SetMsgNum", LumenHostValue.FromNumber(1), LumenHostValue.FromNumber(player2));
    }

    /// <summary>Per-frame state push, as the game does every tick.</summary>
    public void Advance()
    {
        if (_messageDelay >= 0 && _messageDelay-- == 0 && !_cardDialog)
        {
            call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(true));
            CoinsChanged();
        }
        // ponytail: no network in the engine; type 2 is what the game shows before it connects.
        call(_network, "SetType", LumenHostValue.FromNumber(2));
        call(_card, "SetOnline", LumenHostValue.FromNumber(_online ? 1 : 0));
        call(_card, "SetCamera", LumenHostValue.FromNumber(0));
        call(_card, "SetBncoin", LumenHostValue.FromNumber(0));
        call(_card, "SetBurst", LumenHostValue.FromBoolean(false), LumenHostValue.FromNumber(0));
        _network.Advance();
        _card.Advance();
        _coins.Advance();
    }

    /// <summary>Visible overlays under (msg_coins) or over (network, card prompt) the intermission.</summary>
    public LumenRenderSnapshot CreateSnapshot(bool overIntermission, float interpolation)
    {
        var layers = Scene.Player.Layers;
        IEnumerable<LumenSceneLayer> visible = overIntermission
            ? _cardVisible ? [layers[0], layers[1]] : [layers[0]]
            : _coinsVisible ? [layers[2]] : [];
        return new LumenScenePlayer(1280, 720, visible).CreateRenderSnapshot(interpolation);
    }

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Indicator movie is missing callback '{name}'.");
    }
}
