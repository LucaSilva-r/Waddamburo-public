using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

public static class RainbowTransitionComposition
{
    public const string StaticHostId = "rainbow-transition";

    public static SceneDefinition Create(SceneId id) => new(
        SceneDefinition.CurrentVersion,
        id,
        [
            new SceneLayerDefinition(
                "intermission/packeddata.ddp",
                "scene_change_rainbow/scene_change_rainbow.lm",
                LumenMatrix.Identity,
                StaticHostId),
        ]);
}
