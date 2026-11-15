namespace Waddamburo.Game.Scenes;

/// <summary>Layer roles (<see cref="SceneLayerDefinition.HostId"/>) shared by the engine's scene lists and the skins.</summary>
public static class LayerRoles
{
    /// <summary>A part of the always-on indicator (name boards, countdown, legend) set up with its screen's movie.</summary>
    public const string IndicatorPart = "indicator-part";

    /// <summary>The indicators kept over every screen (network, card prompt, coins).</summary>
    public const string SystemIndicators = "system-indicators";
}
