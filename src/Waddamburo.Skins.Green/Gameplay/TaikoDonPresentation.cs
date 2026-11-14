using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>
/// Owns gameplay motions and the authored native-model marker. The idle loop follows the play
/// state (Go-Go, then a running miss streak, then the gauge band); one-shot reactions play over
/// it and return to whatever idle is current when they end.
/// </summary>
public sealed class TaikoDonPresentation : ITaikoLongNoteCharacter
{
    private const int ComboReactionInterval = 10;
    private const int LongMissStreak = 6;

    private readonly IDonPresentationController _controller;
    private readonly int _player;
    private readonly LumenSceneLayer? _layer;
    // The camera the Don goes back to after a balloon or kusudama.
    private readonly DonPresentationLayout _view;
    private readonly LumenNativeSurfaceKey _surface;
    // Composite filters (reference frames). Full gauge: additive gold
    // (0.430 + 0.352t, 0.391 + 0.234t, 0.117t) with a pulse t in 0..1; it replaces the darkening.
    // ponytail: t is fixed at the reference frame's 0.173; the pulse's shape and period are unknown.
    private static readonly LumenRenderColor FullGaugeGlow = new(0.4906f, 0.4312f, 0.0203f, 0);
    // Lying down after a long miss streak: the character's colours are halved.
    private static readonly LumenRenderColor MissDarken = new(0.5f, 0.5f, 0.5f, 1);
    // AI battle: the Don's fill in the battle movie, taken off while a balloon shows it (Green: the stage's Dons vanish
    // during balloons, user-confirmed).
    private readonly (LumenSceneLayer Layer, string State, LumenNativeSurfacePlacement Placement)? _stageFill;
    private readonly string _kusu; // the kusudama motions: don_kusu1P_* for the top lane, don_kusu2P_* for the second
    private readonly bool _keepCamera; // AI battle's stage: its own view through balloons and kusudama
    private bool _goGo;
    private bool _balloonVisible;
    private TaikoGaugeState _gauge;
    private int _combo;
    private int _missStreak;

    /// <param name="controller">The Don motion and render target controller.</param>
    /// <param name="layer">The gameplay movie layer receiving the native fill.</param>
    /// <param name="player">The lane: 0 top (don3d's don1p state), 1 the second player's lower lane (don2p).</param>
    /// <param name="stage">The Don stands in a movie the scene draws itself (AI battle's ai_hit_effect, its don1p / don2p
    /// fills by the territory needle): no state to pick, nothing for the lane to draw.</param>
    /// <param name="overlays">The lane's balloon and kusudama movies: the Don shows in their don1pM / don2pM fills.</param>
    public TaikoDonPresentation(IDonPresentationController controller, LumenSceneLayer layer, int player = 0, bool stage = false,
        (LumenPlayer Balloon, LumenPlayer? Kusudama)? overlays = null)
    {
        // The Dons are already playing (another lane may have started first): bind without a reset.
        if (overlays is var (balloon, kusudama))
        {
            DonLumenBinding.Attach(balloon, controller, reset: false);
            if (kusudama is not null) DonLumenBinding.Attach(kusudama, controller, reset: false);
        }
        _kusu = player == 1 ? "don_kusu2P" : "don_kusu1P";
        _keepCamera = stage;
        _controller = controller;
        _player = player;
        _layer = stage ? null : layer;
        _surface = controller.GetSurface(player);
        // AI battle's Dons face each other (the kusudama view turns both to the centre), scaled into their slots.
        _view = stage ? DonPresentationLayout.Kusudama : DonPresentationLayout.Gameplay;
        controller.SetCameraLayout(player, _view);
        var state = player == 1 ? "don2p" : "don1p";
        // ponytail: the battle's size read off the recomp (about 60% of the menus' 600); tune against it.
        // and the player's 40 px left (the kusudama view leans it to the centre, into AI-don's side).
        var placement = stage ? LumenNativeSurfacePlacement.Centered(360, 360) with { X = -180 - (player == 0 ? 40 : 0) }
            : LumenNativeSurfacePlacement.Centered(448, 256);
        layer.Player.SetNativeFill(state, _surface, placement);
        if (stage)
            _stageFill = (layer, state, placement);
        if (!stage && !layer.Player.TryGotoLabel("", state))
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

    /// <summary>
    /// Back to the plain gameplay pose: camera and idle (a reviewed play gone back before a balloon this
    /// presentation never saw, though the shared model still wears it).
    /// </summary>
    public void ResetPose()
    {
        if (_balloonVisible && _stageFill is var (stage, state, placement))
            stage.Player.SetNativeFill(state, _surface, placement);
        _balloonVisible = false;
        _controller.SetCameraLayout(_player, _view);
        _controller.SetMotion(new(_player, null, idle));
    }

    public void SetBalloonVisible(bool visible)
    {
        var wasVisible = _balloonVisible;
        _balloonVisible = visible;
        if (_stageFill is var (stage, state, placement) && wasVisible != visible)
        {
            if (visible)
                stage.Player.RemoveNativeFill(state);
            else
                stage.Player.SetNativeFill(state, _surface, placement);
        }
        if (wasVisible && !visible)
        {
            _controller.SetCameraLayout(_player, _view);
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

    // A balloon or kusudama (TaikoLongNotePresentation): the overlay shows the Don while it is up.
    public void Opened(PlayableLongNoteKind overlay)
    {
        if (overlay == PlayableLongNoteKind.Kusudama)
        {
            if (!_keepCamera) _controller.SetCameraLayout(_player, DonPresentationLayout.Kusudama);
            _controller.SetMotion(new(_player, $"{_kusu}_in", $"{_kusu}_nobeat"));
            return;
        }
        // AI battle keeps its battle view but, as every lane's balloon, unmirrored: the mirrored view turned AI-don's
        // balloon motion the wrong way (user-reported).
        _controller.SetCameraLayout(_player, _keepCamera ? DonPresentationLayout.Standard : DonPresentationLayout.Balloon);
        _controller.SetMotion(new(_player, null, "don_balloon_nobeat"));
    }

    // Every hit restarts the pumping motion, which settles back to the no-beat idle.
    public void Pumped(PlayableLongNote note) => _controller.SetMotion(note.Kind == PlayableLongNoteKind.Kusudama
        ? new(_player, $"{_kusu}_loop", $"{_kusu}_nobeat")
        : new(_player, "don_balloon_loop", "don_balloon_nobeat"));

    public void Closed(PlayableLongNote note, bool popped) => _controller.SetMotion(new(_player,
        (note.Kind == PlayableLongNoteKind.Kusudama, popped) switch
        {
            (true, true) => $"{_kusu}_success02",
            (true, false) => $"{_kusu}_failure",
            (false, true) => "don_balloon_success",
            _ => "don_balloon_failure",
        }, null));

    /// <summary>A one-shot motion that returns to the current idle (e.g. the end-of-song full combo).</summary>
    public void React(string motion) => react(motion);

    /// <summary>Turns the model in the gameplay view (a modchart's yaw, pitch and roll, degrees).</summary>
    public void Turn(double yaw, double pitch, double roll) => _controller.SetModelRotation(_player, (float)yaw, (float)pitch, (float)roll);

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
