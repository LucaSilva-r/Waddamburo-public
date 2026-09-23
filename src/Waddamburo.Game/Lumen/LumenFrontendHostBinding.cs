using Waddamburo.Game.Flow;
using Waddamburo.Game.Don;
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
    ISceneTransitionSink transitions,
    IDonPresentationController? don = null,
    EntrySceneHost? entry = null) : ILumenHostBinding, IDisposable
{
    private readonly ILumenFrontendServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly ISceneTransitionSink _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
    private readonly IDonPresentationController? _don = don;
    private readonly EntrySceneHost? _entry = entry;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(_services.CallExternalInterface);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("IsReady", _ =>
                LumenHostValue.FromBoolean(_services.IsReady && (_entry?.PollReady() ?? true)));
            lumen.RegisterMethod("InitInfo", _ =>
            {
                _services.Initialize();
                _entry?.InitInfo();
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("IsStartLumen", _ => LumenHostValue.FromBoolean(_services.IsStartLumen));
            // EntryCoin(player, side): the entry scene host performs the join (coins, costume lists).
            lumen.RegisterMethod("EntryCoin", call => LumenHostValue.FromBoolean(_entry is not null
                ? _entry.TryJoin(call.Arguments.Length > 0 ? (int)call.Arguments[0].AsNumber() : 0)
                : _services.TryEnterPlayer()));
            _entry?.Register(lumen);
            lumen.RegisterMethod("IsFreePlay", _ => LumenHostValue.FromBoolean(_services.IsFreePlay));
            lumen.RegisterMethod("RequestSE", call => requestSound(LumenFrontendSoundRequestKind.Effect, call));
            lumen.RegisterMethod("RequestSystemSE", call => requestSound(LumenFrontendSoundRequestKind.SystemEffect, call));
            lumen.RegisterMethod("NotifyPlayLoopVO", call => requestSound(LumenFrontendSoundRequestKind.LoopVoice, call));
            lumen.RegisterMethod("StopVoice", _ =>
            {
                _services.StopVoice();
                return LumenHostValue.Undefined;
            });
            if (_don is not null)
            {
                DonLumenBinding.RegisterMotion(context, lumen, _don);
                DonLumenBinding.RegisterCostume(lumen, _don);
            }
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
