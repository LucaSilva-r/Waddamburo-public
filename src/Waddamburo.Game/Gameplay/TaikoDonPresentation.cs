using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Soul-gauge band that selects Don's calm or cleared idle.</summary>
public enum TaikoGaugeState
{
    BelowClear,
    Cleared,
    Full,
}

/// <summary>
/// Owns gameplay motions and the authored native-model marker. The idle loop follows the play
/// state (Go-Go, then a running miss streak, then the gauge band); one-shot reactions play over
/// it and return to whatever idle is current when they end.
/// </summary>
public sealed class TaikoDonPresentation
{
    private const int ComboReactionInterval = 10;
    private const int LongMissStreak = 6;

    private readonly IDonPresentationController _controller;
    private readonly int _player;
    private readonly LumenSceneLayer _layer;
    private readonly LumenNativeSurfaceKey _surface;
    // Composite filters (reference frames). Full gauge: additive gold
    // (0.430 + 0.352t, 0.391 + 0.234t, 0.117t) with a pulse t in 0..1; it replaces the darkening.
    // ponytail: t is fixed at the reference frame's 0.173; the pulse's shape and period are unknown.
    private static readonly LumenRenderColor FullGaugeGlow = new(0.4906f, 0.4312f, 0.0203f, 0);
    // Lying down after a long miss streak: the character's colours are halved.
    private static readonly LumenRenderColor MissDarken = new(0.5f, 0.5f, 0.5f, 1);
    private bool _goGo;
    private bool _balloonVisible;
    private TaikoGaugeState _gauge;
    private int _combo;
    private int _missStreak;

    /// <param name="player">The lane: 0 top (don3d's don1p state), 1 the second player's lower lane (don2p).</param>
    public TaikoDonPresentation(IDonPresentationController controller, LumenSceneLayer layer, int player = 0)
    {
        _controller = controller;
        _player = player;
        _layer = layer;
        _surface = controller.GetSurface(player);
        controller.SetCameraLayout(player, DonPresentationLayout.Gameplay);
        var state = player == 1 ? "don2p" : "don1p";
        layer.Player.SetNativeFill(state, _surface,
            LumenNativeSurfacePlacement.Centered(448, 256));
        if (!layer.Player.TryGotoLabel("", state))
            throw new InvalidDataException($"Gameplay Don movie is missing its '{state}' state.");
        _controller.SetMotion(new(player, null, idle));
    }

    private string idle => _goGo ? "don_sabi"
        : _missStreak >= LongMissStreak ? "don_miss6"
        : _missStreak > 0 ? "don_miss"
        : _gauge == TaikoGaugeState.BelowClear ? "don_normal"
        : "don_norm_loop";

    public void SetGoGo(bool active)
    {
        if (_goGo == active) return;
        _goGo = active;
        if (active) react("don_sabi_start");
        else refreshIdle();
    }

    public void SetGauge(TaikoGaugeState state)
    {
        if (_gauge == state) return;
        var previous = _gauge;
        _gauge = state;
        if (state == TaikoGaugeState.Full) react("don_full_gage");
        else if (state == TaikoGaugeState.BelowClear) react("don_norm_down");
        else if (previous == TaikoGaugeState.BelowClear) react("don_norm_up");
        else refreshIdle(); // leaving a full gauge has no reaction
    }

    public void SetBalloonVisible(bool visible)
    {
        var wasVisible = _balloonVisible;
        _balloonVisible = visible;
        if (wasVisible && !visible)
        {
            _controller.SetCameraLayout(_player, DonPresentationLayout.Gameplay);
            _controller.SetMotion(new(_player, null, idle));
        }
    }

    public void OnJudged(TaikoNoteJudgement judgement)
    {
        if (judgement.StrongHitCompleted) return;
        if (judgement.Result == TaikoHitResult.Miss)
        {
            _combo = 0;
            _missStreak++;
            if (_missStreak is 1 or LongMissStreak) refreshIdle();
            return;
        }
        _combo++;
        if (_missStreak > 0)
        {
            _missStreak = 0;
            if (!_goGo) react("don_miss_normal");
            else refreshIdle();
        }
        if (_combo % ComboReactionInterval == 0 && !_goGo)
            react(_gauge == TaikoGaugeState.Full ? "don_full_combo" : "don_combo");
    }

    /// <summary>A one-shot motion that returns to the current idle (e.g. the end-of-song full combo).</summary>
    public void React(string motion) => react(motion);

    // ponytail: the long-note presentation owns Don while its overlay is up, so state changes
    // there only update what the idle will be once the overlay closes.
    private void react(string motion)
    {
        if (!_balloonVisible) _controller.SetMotion(new(_player, motion, idle));
    }

    private void refreshIdle()
    {
        if (!_balloonVisible) _controller.SetIdle(_player, idle);
    }

    /// <summary>Applies the full-gauge gold or the long-miss darkening to every quad showing this character.</summary>
    public LumenRenderSnapshot Tint(LumenRenderSnapshot scene)
    {
        Func<LumenRenderQuad, LumenRenderQuad>? filter = _gauge == TaikoGaugeState.Full
            ? quad => quad with { AddColor = FullGaugeGlow }
            : _missStreak >= LongMissStreak ? quad => quad with { MultiplyColor = MissDarken }
            : null;
        return filter is null ? scene
            : new(scene.StageWidth, scene.StageHeight, scene.Quads.Select(quad => quad.NativeSurface == _surface
                ? filter(quad)
                : quad));
    }

    /// <summary>The character marker to draw, or null while a long-note overlay owns Don.</summary>
    public LumenSceneLayer? Layer => _balloonVisible ? null : _layer;
}
