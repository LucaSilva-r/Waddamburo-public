using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Marks movies as host-driven so their standalone keyboard demos stay disabled.</summary>
public sealed class TaikoGameplayHostBinding : ILumenHostBinding
{
    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", static _ => { });
    }
}
