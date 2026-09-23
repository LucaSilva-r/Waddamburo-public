using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Don;

public readonly record struct DonMotionRequest(int PlayerIndex, string? OneShot, string? Loop);

/// <summary>
/// What Don wears: a whole costume (full/cos/cos_NNN000) or divided parts (head, body, face paint).
/// Costume 0 is "no costume": the default parts.
/// </summary>
public readonly record struct DonCostume(int? Whole, int Head, int Body, int Paint)
{
    public static DonCostume Default { get; } = new(null, 0, 0, 0);

    public static DonCostume FromWhole(int id) => id <= 0 ? Default : new(id, 0, 0, 0);
}

public enum DonPresentationLayout
{
    Standard,
    OpposedPlayers,
    Gameplay,
    Retry,
    RetrySuccess,
}

/// <summary>
/// Renderer-facing Don state owned outside Lumen. Implementations advance at the game tick and
/// provide GPU-backed native surfaces to the platform compositor.
/// </summary>
public interface IDonPresentationController
{
    void Reset(DonPresentationLayout layout);

    /// <summary>Changes the view while keeping the current motion.</summary>
    void SetCameraLayout(DonPresentationLayout layout) => Reset(layout);

    LumenNativeSurfaceKey GetSurface(int playerIndex);

    void SetMotion(DonMotionRequest request);

    /// <summary>
    /// Changes the loop without interrupting a running one-shot: the one-shot finishes into the
    /// new loop, and an idle that is already looping switches at once.
    /// </summary>
    void SetIdle(int playerIndex, string loop) => SetMotion(new(playerIndex, null, loop));

    /// <summary>Dresses a player's Don; kept across scenes.</summary>
    void SetCostume(int playerIndex, DonCostume costume)
    {
    }
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
        IDonPresentationController presentation,
        string methodName = "SetMotion")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lumen);
        ArgumentNullException.ThrowIfNull(presentation);
        lumen.RegisterMethod(methodName, call =>
        {
            ApplyMotion(context, presentation, call.Arguments);
            return LumenHostValue.Undefined;
        });
    }

    /// <summary>(player, one-shot DON_* id, loop DON_* id), resolved through the movie's DON_ constants.</summary>
    public static void ApplyMotion(
        LumenHostContext context,
        IDonPresentationController presentation,
        IReadOnlyList<LumenHostValue> arguments)
    {
        if (arguments.Count < 3)
            return;
        var player = integer(arguments[0]);
        if (player is not (0 or 1))
            return;
        presentation.SetMotion(new DonMotionRequest(
            player,
            motionName(context, arguments[1]),
            motionName(context, arguments[2])));
    }

    /// <summary>Entry costume picker: ChangeCostume(player, id) / ChangeDivideCostume(player, head, body, paint).</summary>
    public static void RegisterCostume(LumenHostObject lumen, IDonPresentationController presentation)
    {
        ArgumentNullException.ThrowIfNull(lumen);
        ArgumentNullException.ThrowIfNull(presentation);
        lumen.RegisterMethod("ChangeCostume", call =>
        {
            if (call.Arguments.Length >= 2 && integer(call.Arguments[0]) is var player and (0 or 1))
                presentation.SetCostume(player, DonCostume.FromWhole(integer(call.Arguments[1])));
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ChangeDivideCostume", call =>
        {
            if (call.Arguments.Length >= 4 && integer(call.Arguments[0]) is var player and (0 or 1))
                presentation.SetCostume(player, new DonCostume(null,
                    integer(call.Arguments[1]), integer(call.Arguments[2]), integer(call.Arguments[3])));
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
