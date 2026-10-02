using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// tutorial.lm, the how-to-play movie (traced research session24): it runs itself (ten scenes, five
/// rim hits skip), asks for sounds through LumenMethod.SE_REQUEST / VOICE_REQUEST (group, id) and
/// sets _global.isAllEnd when done. The game only tells it who plays: SetPlayerStatus(left, right).
/// Its Don stands in the don1p marker with the gameplay camera; the balloon and kusudama demos move it
/// to the don1pM marker and their views through ExternalInterface DonTrig(0, trigger).
/// </summary>
public sealed class TutorialHostBinding(int side, bool twoPlayers, Action<string, int>? playCue = null,
    IDonPresentationController? don = null) : ILumenHostBinding
{
    private LumenPlayer? _player;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(call =>
        {
            if (call.Arguments.Length > 2 && call.Arguments[0].Kind == LumenHostValueKind.Text
                && call.Arguments[0].AsString() == "DonTrig" && call.Arguments[2].Kind == LumenHostValueKind.Number)
                donTrigger((int)call.Arguments[2].AsNumber());
            return LumenHostValue.Undefined;
        });
        context.RegisterObject("LumenMethod", method =>
        {
            method.RegisterMethod("SE_REQUEST", call =>
            {
                // The movie's SE_DON 0 / SE_KATSU 1 / SE_SIDEBODY 2 / CLEAR 3 / BALOON 4 / KUSUDAMA 5 / BURN 6:
                // traced 0, 1 -> SE_COM 0, 3; 2, 4, 5 -> SE_HOWTOPLAY 0, 2, 3 (3 and 6 follow the pattern).
                switch (id(call))
                {
                    case 0: playCue?.Invoke("SE_COM", 0); break;
                    case 1: playCue?.Invoke("SE_COM", 3); break;
                    case var effect and >= 2 and <= 6: playCue?.Invoke("SE_HOWTOPLAY", effect - 2); break;
                    case var other: Console.WriteLine($"Tutorial.SE_REQUEST({other}) [unmapped]"); break;
                }
                return LumenHostValue.Undefined;
            });
            // Traced VOICE_REQUEST(0, n) -> VO_HOWTOPLAY cue n - 1 (n = 2..9).
            method.RegisterMethod("VOICE_REQUEST", call =>
            {
                if (id(call) is > 0 and var n)
                    playCue?.Invoke("VO_HOWTOPLAY", n - 1);
                return LumenHostValue.Undefined;
            });
            method.RegisterMethod("NOTIFY_EXIT", static _ => LumenHostValue.Undefined);
        });
    }

    public void Attach(LumenPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        if (don is not null)
        {
            don.Reset(DonPresentationLayout.Gameplay);
            view(DonPresentationLayout.Gameplay);
            don.SetIdle(0, "don_normal");
        }
        // Traced (true, false) for 1P on the left drum; the game plays VO_HOWTOPLAY 0 as the scene loads.
        LumenPlayerCalls.Call(player, "SetPlayerStatus",
            LumenHostValue.FromBoolean(twoPlayers || side == 0), LumenHostValue.FromBoolean(twoPlayers || side == 1));
        playCue?.Invoke("VO_HOWTOPLAY", 0);
    }

    // Traced DonTrig: 17 a balloon hit (the balloon view), 16 changed nothing, 18 the pop (SE_HOWTOPLAY 2),
    // 20 don_balloon_failure, 21 a kusudama hit (the kusudama view), 23 it opens (back to the gameplay
    // view and don_normal at once). The hits play gameplay's pumping motions (TaikoLongNotePresentation).
    // ponytail: only the left Don (the traced 1P); a right-drum player's tutorial is untraced.
    private bool _kusudamaHit;

    private void donTrigger(int trigger)
    {
        if (don is null)
            return;
        switch (trigger)
        {
            case 17:
                view(DonPresentationLayout.Balloon);
                don.SetMotion(new(0, "don_balloon_loop", "don_balloon_nobeat"));
                break;
            case 18:
                don.SetMotion(new(0, "don_balloon_success", "don_balloon_nobeat"));
                break;
            case 20:
                don.SetMotion(new(0, "don_balloon_failure", null));
                break;
            case 21:
                view(DonPresentationLayout.Kusudama);
                don.SetMotion(new(0, _kusudamaHit ? "don_kusu1P_loop" : "don_kusu1P_in", "don_kusu1P_nobeat"));
                _kusudamaHit = true;
                break;
            case 23:
                _kusudamaHit = false;
                view(DonPresentationLayout.Gameplay);
                don.SetMotion(new(0, null, "don_normal"));
                break;
        }
    }

    // Two markers: don1p, the gameplay slot (top left, 448x256), and don1pM, the balloon and kusudama
    // square (600x600). Only the current view's marker shows the Don.
    private void view(DonPresentationLayout layout)
    {
        don!.SetCameraLayout(0, layout);
        var gameplay = layout == DonPresentationLayout.Gameplay;
        var (shown, hidden) = gameplay ? ("don1p", "don1pM") : ("don1pM", "don1p");
        _player?.RemoveNativeFill(hidden);
        _player?.SetNativeFill(shown, don.GetSurface(0),
            gameplay ? LumenNativeSurfacePlacement.Centered(448, 256) : DonLumenBinding.Placement);
    }

    private static int id(LumenHostCall call) =>
        call.Arguments.Length > 1 && call.Arguments[1].Kind == LumenHostValueKind.Number ? (int)call.Arguments[1].AsNumber() : -1;
}
