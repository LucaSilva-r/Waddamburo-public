using Waddamburo.Lumen.Runtime;

/// <summary>
/// Selects movies' native-host path while keeping unimplemented game services
/// explicit. Concrete native methods belong in product host bindings, not AVM.
/// </summary>
internal sealed class ViewerHostBinding : ILumenHostBinding
{
    public static ViewerHostBinding Instance { get; } = new();

    private ViewerHostBinding()
    {
    }

    public void Install(LumenHostContext context)
    {
        context.RegisterExternalInterfaceCall(call =>
        {
            Console.WriteLine($"ExternalInterface.call({string.Join(", ", call.Arguments.Select(formatValue))})");
            return LumenHostValue.Undefined;
        });
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("IsReady", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("InitInfo", _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("IsStartLumen", _ => LumenHostValue.FromBoolean(true));
            // The standalone viewer has no cabinet credit service. Accept an
            // authored entry request so input-driven scenes remain testable.
            lumen.RegisterMethod("EntryCoin", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("IsFreePlay", _ => LumenHostValue.FromBoolean(false));
            lumen.RegisterMethod("StopVoice", _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("SetNextScene", call =>
            {
                Console.WriteLine($"Lumen.SetNextScene({string.Join(", ", call.Arguments.Select(formatValue))})");
                return LumenHostValue.Undefined;
            });
        });
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
