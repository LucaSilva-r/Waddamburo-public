using Waddamburo.Lumen.Rendering;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's system indicators: our network icon, card prompt and credit line, which read the provider (app scope:
/// credits, network, screen) and decide themselves what shows. They are only advanced and drawn.
/// </summary>
internal sealed class WaddamburoIndicators(LumenGameSceneInstance scene) : ISystemIndicators
{
    // The three movies, in the layer order the frontends share.
    private static readonly string[] Movies = ["network_icon", "msg_banapass", "msg_coins"];

    public static SceneDefinition Definition(SceneId id) => new(SceneDefinition.CurrentVersion, id,
        Movies.Select(static name => new SceneLayerDefinition(
            "indicator/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity, LayerRoles.SystemIndicators)));

    public LumenGameSceneInstance Scene => scene;

    public Func<int>? NetworkIcon { set { } }

    public bool Rejoin => false;

    public void SetScene(ScreenKind screen)
    {
    }

    public void Advance()
    {
        foreach (var layer in scene.Player.Layers)
            layer.Player.Advance();
    }

    // Drawn every frame: each snapshot reuses its own quad list.
    private readonly LumenQuadBuffer _quads = new(), _networkQuads = new();

    public LumenRenderSnapshot CreateSnapshot(bool overIntermission, float interpolation)
    {
        var layers = scene.Player.Layers;
        return new LumenScenePlayer(1280, 720, overIntermission ? [layers[0], layers[1]] : [layers[2]]).CreateRenderSnapshot(interpolation, buffer: _quads);
    }

    public LumenRenderSnapshot CreateNetworkSnapshot(float interpolation) =>
        new LumenScenePlayer(1280, 720, [scene.Player.Layers[0]]).CreateRenderSnapshot(interpolation, buffer: _networkQuads);

    public void SetupPanels(bool left, bool right)
    {
    }

    public void EntryJoined()
    {
    }

    public void CardDialog(bool open)
    {
    }

    public void CoinsChanged()
    {
    }
}
