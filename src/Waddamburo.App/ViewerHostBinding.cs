using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

/// <summary>
/// Selects movies' native-host path while keeping unimplemented game services
/// explicit. Concrete native methods belong in product host bindings, not AVM.
/// </summary>
internal sealed class ViewerHostBinding : ILumenHostBinding, ILumenFrontendServices, ISceneTransitionSink
{
    public static ViewerHostBinding Instance { get; } = new();

    private ViewerHostBinding()
    {
    }

    public void Install(LumenHostContext context)
        => new LumenFrontendHostBinding(this, this).Install(context);

    public bool IsReady => true;

    public bool IsStartLumen => true;

    public bool IsFreePlay => false;

    public void Initialize()
    {
    }

    // The standalone viewer has no cabinet credit service. Accept an authored
    // entry request so input-driven scenes remain testable.
    public bool TryEnterPlayer() => true;

    public void StopVoice()
    {
    }

    public LumenHostValue CallExternalInterface(LumenHostCall hostCall)
    {
        Console.WriteLine($"ExternalInterface.call({string.Join(", ", hostCall.Arguments.Select(formatValue))})");
        return LumenHostValue.Undefined;
    }

    public bool TryRequestTransition(LumenSceneRequest request)
    {
        Console.WriteLine($"Lumen.SetNextScene({request.SceneNumber}, {request.Argument1}, {request.Argument2})");
        return true;
    }

    private static string formatValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => "undefined",
        LumenHostValueKind.Null => "null",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        _ => "undefined",
    };
}
