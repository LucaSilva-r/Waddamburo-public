using System.Numerics;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Current stage-space bounds and pivot for an authorable scene role.</summary>
public sealed record StageEffectElement(string Target, Vector2 Minimum, Vector2 Maximum, Vector2 Pivot, LumenMatrix Parent)
{
    public bool Contains(Vector2 point) => point.X >= Minimum.X && point.X <= Maximum.X
        && point.Y >= Minimum.Y && point.Y <= Maximum.Y;
}
