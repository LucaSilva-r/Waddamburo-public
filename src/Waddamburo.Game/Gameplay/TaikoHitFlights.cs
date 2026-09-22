using Waddamburo.Catalog;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Bounded independent authored animations sharing immutable scene textures.</summary>
public sealed class TaikoHitFlights
{
    private readonly LumenSceneLayer[] _layers;
    private readonly bool[] _active;
    private int _next;

    public TaikoHitFlights(IEnumerable<LumenSceneLayer> layers)
    {
        _layers = layers.ToArray();
        if (_layers.Length == 0)
            throw new ArgumentException("At least one flight player is required.", nameof(layers));
        _active = new bool[_layers.Length];
        foreach (var layer in _layers)
            layer.Player.Stop();
    }

    public static string? StateFor(PlayableNoteKind kind, TaikoHitResult? result, bool secondaryHit)
    {
        if (secondaryHit || result is not (TaikoHitResult.Great or TaikoHitResult.Good))
            return null;
        return kind switch
        {
            PlayableNoteKind.Don => "don_hit",
            PlayableNoteKind.Ka => "katsu_hit",
            PlayableNoteKind.BigDon => "don_d_hit",
            PlayableNoteKind.BigKa => "katsu_d_hit",
            _ => null,
        };
    }

    public void Trigger(TaikoNoteJudgement judgement)
    {
        var label = StateFor(judgement.HitObject.Kind, judgement.Result, judgement.StrongHitCompleted);
        trigger(label);
    }

    public static string StateFor(PlayableLongNoteKind kind, TaikoInputAction action) => kind switch
    {
        PlayableLongNoteKind.Balloon or PlayableLongNoteKind.Kusudama => "don_hit",
        PlayableLongNoteKind.BigRoll => action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon
            ? "don_renda_d_hit" : "katsu_renda_d_hit",
        _ => action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? "don_hit" : "katsu_hit",
    };

    public void Trigger(TaikoLongNoteProgress progress)
    {
        if (progress.LastAction is { } action)
            trigger(progress.IsPopped ? "geki_hit" : StateFor(progress.Note.Kind, action));
    }

    private void trigger(string? label)
    {
        if (label is null)
            return;
        var index = Array.FindIndex(_active, active => !active);
        if (index < 0)
            index = _next;
        // Independent players allow simultaneous judgements without restarting peers.
        _layers[index].Player.GotoLabel(label, play: true);
        _active[index] = true;
        _next = (index + 1) % _layers.Length;
    }

    public void Advance()
    {
        for (var index = 0; index < _layers.Length; index++)
        {
            if (!_active[index])
                continue;
            _layers[index].Player.Advance(LumenInputSnapshot.Empty);
            if (!_layers[index].Player.IsPlaying)
                _active[index] = false;
        }
    }

    public IEnumerable<LumenSceneLayer> ActiveLayers =>
        _layers.Where((_, index) => _active[index]);
}
