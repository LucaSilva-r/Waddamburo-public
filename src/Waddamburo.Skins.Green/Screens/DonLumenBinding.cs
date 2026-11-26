using Waddamburo.Skins.Green.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Screens;

/// <summary>Connects the authored Don fill markers in a Lumen movie to native render targets.</summary>
public static class DonLumenBinding
{
    public static readonly LumenNativeSurfacePlacement Placement =
        LumenNativeSurfacePlacement.Centered(600, 600) with { UniformScale = true };

    /// <param name="player">The movie receiving the native fill markers.</param>
    /// <param name="presentation">The Don presentation supplying render targets.</param>
    /// <param name="layout">The view to use when resetting the presentation.</param>
    /// <param name="reset">False binds the markers without resetting the view and motions (a movie
    /// joining a scene whose Dons are already playing).</param>
    public static void Attach(
        LumenPlayer player,
        IDonPresentationController presentation,
        DonPresentationLayout layout = DonPresentationLayout.Standard,
        bool reset = true)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(presentation);
        if (reset)
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
        IReadOnlyList<LumenHostValue> arguments,
        Func<int, string?>? motionFallback = null)
    {
        if (arguments.Count < 3)
            return;
        var player = integer(arguments[0]);
        if (player is not (0 or 1))
            return;
        presentation.SetMotion(new DonMotionRequest(
            player,
            motionName(context, arguments[1], motionFallback),
            motionName(context, arguments[2], motionFallback)));
    }

    /// <summary>
    /// Entry costume picker: ChangeCostume(player, id) / ChangeDivideCostume(player, head, body, paint),
    /// and ChangeAccessory(player, puchi, 0) (traced session27-puchi, after the costume call).
    /// </summary>
    public static void RegisterCostume(LumenHostObject lumen, IDonPresentationController presentation,
        Action<int>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(lumen);
        ArgumentNullException.ThrowIfNull(presentation);
        lumen.RegisterMethod("ChangeCostume", call =>
        {
            if (call.Arguments.Length >= 2 && integer(call.Arguments[0]) is var player and (0 or 1))
            {
                presentation.SetCostume(player, DonCostume.FromWhole(integer(call.Arguments[1])));
                changed?.Invoke(player);
            }
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ChangeAccessory", call =>
        {
            if (call.Arguments.Length >= 2 && integer(call.Arguments[0]) is var player and (0 or 1))
            {
                presentation.SetAccessory(player, integer(call.Arguments[1]));
                changed?.Invoke(player);
            }
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ChangeDivideCostume", call =>
        {
            if (call.Arguments.Length >= 4 && integer(call.Arguments[0]) is var player and (0 or 1))
            {
                presentation.SetCostume(player, new DonCostume(null,
                    integer(call.Arguments[1]), integer(call.Arguments[2]), integer(call.Arguments[3])));
                changed?.Invoke(player);
            }
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

    private static string? motionName(LumenHostContext context, LumenHostValue value, Func<int, string?>? fallback)
    {
        if (value.Kind != LumenHostValueKind.Number)
            return null;
        var global = context.FindNumericVariableName("DON_", value.AsNumber());
        if (global is null)
        {
            var number = value.AsNumber();
            return fallback is not null && double.IsFinite(number) && number == Math.Truncate(number)
                && number >= int.MinValue && number <= int.MaxValue ? fallback((int)number) : null;
        }
        return $"don_{global[4..].ToLowerInvariant()}"
                .Replace("1p", "1P", StringComparison.Ordinal)
                .Replace("2p", "2P", StringComparison.Ordinal);
    }
}
