using Waddamburo.Lumen.Rendering;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Flow;

/// <summary>
/// The system indicators over every screen (network, card prompt, credits), as a frontend shows them: Green's drives the
/// game's indicator movies like the game; Waddamburo's movies read the provider and only need drawing.
/// Layers: 0 the network icon, 1 the card prompt, 2 the credit line.
/// </summary>
public interface ISystemIndicators
{
    LumenGameSceneInstance Scene { get; }

    /// <summary>The network icon's state (0 good, 1 warning, 2 disconnected); null: no server.</summary>
    Func<int>? NetworkIcon { set; }

    /// <summary>A second player joins from Song Select (the entry keeps the join messages off).</summary>
    bool Rejoin { get; }

    void SetScene(ScreenKind scene);

    void Advance();

    /// <summary>Visible overlays under (credits) or over (network, card prompt) the intermission.</summary>
    LumenRenderSnapshot CreateSnapshot(bool overIntermission, float interpolation);

    /// <summary>The network icon alone, for the black screen between scene loads.</summary>
    LumenRenderSnapshot CreateNetworkSnapshot(float interpolation);

    void SetupPanels(bool left, bool right);

    void EntryJoined();

    void CardDialog(bool open);

    void CoinsChanged();
}
