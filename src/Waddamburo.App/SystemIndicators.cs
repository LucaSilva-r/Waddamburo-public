using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

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

    private readonly LumenPlayer _network;
    private readonly LumenPlayer _card;
    private readonly LumenPlayer _coins;
    private bool _cardVisible;
    private bool _coinsVisible;
    private bool _online;

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

    public static SceneDefinition Definition(SceneId id) => new(SceneDefinition.CurrentVersion, id,
        new[] { "network_icon", "msg_banapass", "msg_coins" }.Select(name => new SceneLayerDefinition(
            "indicator/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity, HostId)));

    public void SetScene(IndicatorScene scene)
    {
        var attract = scene is IndicatorScene.Attract or IndicatorScene.AttractPrompt;
        _online = scene != IndicatorScene.Boot;
        _cardVisible = attract || scene == IndicatorScene.Entry;
        // The free-play/coin message first appears with the attract title (traced SetVisible* calls);
        // during the boot screens it stays off.
        _coinsVisible = scene is not (IndicatorScene.Gameplay or IndicatorScene.Boot);
        call(_card, "SetScene", LumenHostValue.FromNumber(scene == IndicatorScene.Entry ? 1 : 0));
        // Song Select shows the unjoined player's message; the authored indicator movie
        // positions it on the available side and runs its fade animation.
        var prompt = scene is IndicatorScene.AttractPrompt or IndicatorScene.Entry or IndicatorScene.SongSelect;
        call(_coins, "SetScene", LumenHostValue.FromNumber(scene switch
        {
            IndicatorScene.Entry => 1,
            IndicatorScene.SongSelect or IndicatorScene.Gameplay => 2,
            IndicatorScene.Result => 3,
            _ => 0,
        }));
        call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(prompt)); // sic, the movie's spelling
        call(_coins, "SetVisibleCoinType", LumenHostValue.FromBoolean(!attract || prompt));
        // In Song Select the joined P1 has no prompt (-1); the unjoined right drum uses
        // message 2. The indicator movie positions and animates each side itself.
        setMessageNumbers(scene == IndicatorScene.Entry ? -1 : scene == IndicatorScene.SongSelect ? -1 : 0,
            scene == IndicatorScene.SongSelect ? 2 : 0);
        if (scene == IndicatorScene.Entry)
        {
            // The entry then switches the panel to its split (per-player) scene.
            call(_coins, "SetScene", LumenHostValue.FromNumber(4));
            call(_coins, "SetVisibleSplitPanel", LumenHostValue.FromBoolean(false), LumenHostValue.FromBoolean(true));
            // SetScene(4) hides both messages (isMSGVisible off); the game turns them back on before
            // the numbers (traced ~0.9 s later, after Don-chan's entry motion).
            call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(true));
            setMessageNumbers(-1);
        }
        // SetCoinType seeks the authored movie to its scene label. Repeating it every
        // frame restarts the join message before its fade timeline can advance.
        call(_coins, "SetCoinType", LumenHostValue.FromNumber(2)); // free play
    }

    /// <summary>A player joined at entry: the split panel and the start message go away.</summary>
    public void EntryJoined()
    {
        call(_coins, "SetScene", LumenHostValue.FromNumber(4));
        call(_coins, "SetVisibleSplitPanel", LumenHostValue.FromBoolean(false), LumenHostValue.FromBoolean(false));
        call(_coins, "SetVisibieMsg", LumenHostValue.FromBoolean(false));
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
