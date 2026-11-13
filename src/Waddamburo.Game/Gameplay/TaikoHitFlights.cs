using Waddamburo.Catalog;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Bounded independent authored animations sharing immutable scene textures.</summary>
public sealed class TaikoHitFlights
{
    private readonly LumenSceneLayer[] _layers;
    private readonly bool[] _active;
    private int _next;
    private readonly LumenSceneLayer? _own; // the scene's own flight movie: it flies the notes itself when it reads the provider
    private readonly int _kadonAt; // the Kadon flights (purple big ka) follow the others; none: _layers.Length

    /// <param name="layers">The hit flight movies, taken in turn.</param>
    /// <param name="kadonLayers">Flights for OpenTaiko's Kadon (the flight movie on its purple texture copies).</param>
    public TaikoHitFlights(IEnumerable<LumenSceneLayer> layers, IEnumerable<LumenSceneLayer>? kadonLayers = null, LumenSceneLayer? sceneOwn = null)
    {
        _own = sceneOwn;
        var own = layers.ToArray();
        _kadonAt = own.Length;
        _layers = [.. own, .. kadonLayers ?? []];
        if (_layers.Length == 0)
            throw new ArgumentException("At least one flight player is required.", nameof(layers));
        _active = new bool[_layers.Length];
        foreach (var layer in _layers)
            layer.Player.Stop();
    }

    /// <summary>
    /// The flight movie hears the hits from the provider and flies them itself (one of ours): it is drawn as it
    /// is, advanced with the scene, and nothing here jumps it to a label. Known once its first frame has run.
    /// </summary>
    private bool Provided => _own?.Player.UsesProvider == true;

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
        if (judgement.HitObject.IsKadon && _kadonAt < _layers.Length)
            trigger(label is null ? null : "katsu_d_hit", _kadonAt, _layers.Length);
        else
            trigger(label);
    }

    public static string? StateFor(PlayableLongNoteKind kind, TaikoInputAction action) => kind switch
    {
        PlayableLongNoteKind.Balloon or PlayableLongNoteKind.Kusudama => null,
        PlayableLongNoteKind.BigRoll => action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon
            ? "don_renda_d_hit" : "katsu_renda_d_hit",
        _ => action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? "don_hit" : "katsu_hit",
    };

    public void Trigger(TaikoLongNoteProgress progress)
    {
        if (progress.Note.IsBalloon)
            return;
        if (progress.LastAction is { } action)
            trigger(progress.IsPopped ? "geki_hit" : StateFor(progress.Note.Kind, action));
    }

    private void trigger(string? label) => trigger(label, 0, _kadonAt);

    // A free player in [from, until), else the next in turn there.
    private void trigger(string? label, int from, int until)
    {
        if (label is null || Provided)
            return;
        var index = Array.FindIndex(_active, from, until - from, active => !active);
        if (index < 0)
            index = _next >= from && _next < until ? _next : from;
        // Independent players allow simultaneous judgements without restarting peers.
        _layers[index].Player.GotoLabel(label, play: true);
        _active[index] = true;
        _next = index + 1 < until ? index + 1 : from;
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

    /// <summary>Whether <paramref name="player"/> is one of these flights.</summary>
    public bool Owns(LumenPlayer player) => Array.Exists(_layers, layer => layer.Player == player) || _own?.Player == player;

    public IEnumerable<LumenSceneLayer> ActiveLayers =>
        Provided ? [_own!] : _layers.Where((_, index) => _active[index]);
}
