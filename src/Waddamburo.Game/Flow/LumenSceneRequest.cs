using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Flow;

/// <summary>
/// Primitive scene request emitted by authored Lumen content. Mapping its numeric
/// values to product scenes is composition policy, not AVM behavior.
/// </summary>
public readonly record struct LumenSceneRequest(int SceneNumber, int Argument1, int Argument2)
{
    public static LumenSceneRequest FromHostCall(LumenHostCall call)
    {
        if (call.Arguments.Length != 3)
            throw new ArgumentException("Lumen.SetNextScene requires exactly three numeric arguments.", nameof(call));

        return new LumenSceneRequest(
            readInteger(call.Arguments[0], 0),
            readInteger(call.Arguments[1], 1),
            readInteger(call.Arguments[2], 2));
    }

    private static int readInteger(LumenHostValue value, int index)
    {
        if (value.Kind != LumenHostValueKind.Number)
            throw new ArgumentException($"Lumen.SetNextScene argument {index} must be numeric.");

        var number = value.AsNumber();
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), number, $"Lumen.SetNextScene argument {index} must be a finite 32-bit integer.");
        return (int)number;
    }
}

public interface ISceneTransitionSink
{
    bool TryRequestTransition(LumenSceneRequest request);
}
