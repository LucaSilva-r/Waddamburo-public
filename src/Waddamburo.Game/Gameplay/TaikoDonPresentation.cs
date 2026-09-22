using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Owns gameplay motions and the authored native-model marker.</summary>
public sealed class TaikoDonPresentation
{
    private readonly IDonPresentationController _controller;
    private readonly LumenSceneLayer _layer;
    private bool _goGo;
    private bool _balloonVisible;
    private string loop => _goGo ? "don_swing02" : "don_normal";

    public TaikoDonPresentation(IDonPresentationController controller, LumenSceneLayer layer)
    {
        _controller = controller;
        _layer = layer;
        controller.Reset(DonPresentationLayout.Gameplay);
        layer.Player.SetNativeFill("don1p", controller.GetSurface(0),
            LumenNativeSurfacePlacement.Centered(448, 256));
        if (!layer.Player.TryGotoLabel("", "don1p"))
            throw new InvalidDataException("Gameplay Don movie is missing its player-one state.");
        resume();
    }

    public void SetGoGo(bool active)
    {
        if (_goGo == active) return;
        _goGo = active;
        if (!_balloonVisible) resume();
    }

    public void SetBalloonVisible(bool visible)
    {
        var wasVisible = _balloonVisible;
        _balloonVisible = visible;
        if (wasVisible && !visible)
        {
            _controller.Reset(DonPresentationLayout.Gameplay);
            resume();
        }
    }

    public void OnJudged(TaikoNoteJudgement judgement)
    {
        if (!_balloonVisible && judgement.Result == TaikoHitResult.Miss)
            _controller.SetMotion(new(0, "don_miss", loop));
    }

    private void resume() => _controller.SetMotion(new(0, null, loop));

    public LumenRenderSnapshot Compose(LumenRenderSnapshot scene)
    {
        if (_balloonVisible) return scene;
        var character = new LumenScenePlayer(scene.StageWidth, scene.StageHeight, [_layer])
            .CreateRenderSnapshot();
        return new(scene.StageWidth, scene.StageHeight, scene.Quads.Concat(character.Quads));
    }
}
