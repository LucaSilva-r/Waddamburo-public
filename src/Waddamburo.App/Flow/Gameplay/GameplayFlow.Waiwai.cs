using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Flow.Gameplay;

internal sealed partial class GameplayFlow
{
    /// <summary>
    /// A Waiwai song: two players on the song's Waiwai layout (its duet charts reshaped by section), the stock screen, no
    /// shutter (traced, 5 runs: the results cut in after the same wait), its own results, not saved.
    /// </summary>
    private sealed class WaiwaiPlay(GameplayFlow flow, WaiwaiComposition composition) : PlayMode(flow)
    {
        public override WaiwaiComposition? Composition => composition;

        public override bool UsesPlayerLayout => false;

        public override SceneDefinition? Shutter => null;

        // ponytail: Waiwai (shared voltage, its own results) is not saved until its mode is worked out.
        public override bool SavesSongs => false;

        public override SceneDefinition? Results => FlowScenes.WaiwaiResults;
    }
}
