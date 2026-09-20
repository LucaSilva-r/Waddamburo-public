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

    public void Install(LumenHostContext context) =>
        context.RegisterObject("Lumen", _ => { });
}
