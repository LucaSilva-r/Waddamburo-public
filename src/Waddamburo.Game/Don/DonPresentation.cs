using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Don;

public readonly record struct DonMotionRequest(int PlayerIndex, string? OneShot, string? Loop);

public enum DonPresentationLayout
{
    Standard,
    OpposedPlayers,
}

/// <summary>
/// Renderer-facing Don state owned outside Lumen. Implementations advance at the game tick and
/// provide GPU-backed native surfaces to the platform compositor.
/// </summary>
public interface IDonPresentationController
{
    void Reset(DonPresentationLayout layout);

    LumenNativeSurfaceKey GetSurface(int playerIndex);

    void SetMotion(DonMotionRequest request);
}

/// <summary>Connects the authored Don fill markers in a Lumen movie to native render targets.</summary>
public static class DonLumenBinding
{
    private static readonly LumenNativeSurfacePlacement Placement =
        LumenNativeSurfacePlacement.Centered(600, 600);

    public static void Attach(
        LumenPlayer player,
        IDonPresentationController presentation,
        DonPresentationLayout layout = DonPresentationLayout.Standard)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(presentation);
        presentation.Reset(layout);
        player.SetNativeFill("don1pM", presentation.GetSurface(0), Placement);
        player.SetNativeFill("don2pM", presentation.GetSurface(1), Placement);
    }

    public static void RegisterMotion(
        LumenHostContext context,
        LumenHostObject lumen,
        IDonPresentationController presentation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lumen);
        ArgumentNullException.ThrowIfNull(presentation);
        lumen.RegisterMethod("SetMotion", call =>
        {
            if (call.Arguments.Length < 3)
                return LumenHostValue.Undefined;
            var player = integer(call.Arguments[0]);
            if (player is not (0 or 1))
                return LumenHostValue.Undefined;
            presentation.SetMotion(new DonMotionRequest(
                player,
                motionName(context, call.Arguments[1]),
                motionName(context, call.Arguments[2])));
            return LumenHostValue.Undefined;
        });
    }

    private static int integer(LumenHostValue argument)
    {
        if (argument.Kind != LumenHostValueKind.Number)
            throw new ArgumentException("Don player argument must be a number.", nameof(argument));
        var value = argument.AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(argument), "Don player argument must be a finite integer.");
        return (int)value;
    }

    private static string? motionName(LumenHostContext context, LumenHostValue value)
    {
        if (value.Kind != LumenHostValueKind.Number)
            return null;
        var global = context.FindNumericVariableName("DON_", value.AsNumber());
        return global is null
            ? null
            : $"don_{global[4..].ToLowerInvariant()}"
                .Replace("1p", "1P", StringComparison.Ordinal)
                .Replace("2p", "2P", StringComparison.Ordinal);
    }
}
