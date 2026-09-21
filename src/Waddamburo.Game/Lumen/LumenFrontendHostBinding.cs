using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum LumenFrontendSoundRequestKind
{
    Effect,
    SystemEffect,
    LoopVoice,
}

public readonly record struct LumenFrontendSoundRequest(
    LumenFrontendSoundRequestKind Kind,
    System.Collections.Immutable.ImmutableArray<LumenHostValue> Arguments);

/// <summary>Game services exposed through the common native Lumen object.</summary>
public interface ILumenFrontendServices
{
    bool IsReady { get; }

    bool IsStartLumen { get; }

    bool IsFreePlay { get; }

    void Initialize();

    bool TryEnterPlayer();

    void RequestSound(LumenFrontendSoundRequest request);

    void StopVoice();

    LumenHostValue CallExternalInterface(LumenHostCall hostCall);
}

/// <summary>
/// Installs the native interface expected by front-end Lumen movies. The binding
/// translates calls only; session policy remains in the supplied services and
/// transition sink.
/// </summary>
public sealed class LumenFrontendHostBinding(
    ILumenFrontendServices services,
    ISceneTransitionSink transitions) : ILumenHostBinding, IDisposable
{
    private readonly ILumenFrontendServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly ISceneTransitionSink _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(_services.CallExternalInterface);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("IsReady", _ => LumenHostValue.FromBoolean(_services.IsReady));
            lumen.RegisterMethod("InitInfo", _ =>
            {
                _services.Initialize();
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("IsStartLumen", _ => LumenHostValue.FromBoolean(_services.IsStartLumen));
            lumen.RegisterMethod("EntryCoin", _ => LumenHostValue.FromBoolean(_services.TryEnterPlayer()));
            lumen.RegisterMethod("IsFreePlay", _ => LumenHostValue.FromBoolean(_services.IsFreePlay));
            lumen.RegisterMethod("RequestSE", call => requestSound(LumenFrontendSoundRequestKind.Effect, call));
            lumen.RegisterMethod("RequestSystemSE", call => requestSound(LumenFrontendSoundRequestKind.SystemEffect, call));
            lumen.RegisterMethod("NotifyPlayLoopVO", call => requestSound(LumenFrontendSoundRequestKind.LoopVoice, call));
            lumen.RegisterMethod("StopVoice", _ =>
            {
                _services.StopVoice();
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("SetNextScene", call =>
            {
                _transitions.TryRequestTransition(LumenSceneRequest.FromHostCall(call));
                return LumenHostValue.Undefined;
            });
        });
    }

    private LumenHostValue requestSound(LumenFrontendSoundRequestKind kind, LumenHostCall call)
    {
        _services.RequestSound(new LumenFrontendSoundRequest(kind, call.Arguments));
        return LumenHostValue.Undefined;
    }

    public void Dispose() => _services.StopVoice();
}
