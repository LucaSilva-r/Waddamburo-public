using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public readonly record struct EffectKey(string Track, double Beat);

/// <summary>Immutable keyframe data; edits either produce a complete valid document or leave it untouched.</summary>
public sealed class EffectDocument
{
    public static EffectDocument Empty { get; } = new(ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>>.Empty);
    public static EffectDocument EmbeddedPreview { get; } = new(ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>>.Empty, true);
    public bool UsesEmbeddedEffects { get; }
    public ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>> Tracks { get; }
    private EffectDocument(ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>> tracks, bool embedded = false)
    { Tracks = tracks; UsesEmbeddedEffects = embedded; }

    public static EffectDocument FromTracks(IReadOnlyDictionary<string, ImmutableArray<ChartModKeyframe>> tracks)
    {
        _ = ChartModFile.Compile(tracks); // The file format and editor enforce the same limits and curve rules.
        return new(tracks.ToImmutableDictionary(p => p.Key, p => Canonical(p.Value), StringComparer.Ordinal));
    }

    public bool Contains(EffectKey key) => Tracks.TryGetValue(key.Track, out var keys) && keys.Any(k => k.Beat == key.Beat);
    public EffectDocument AddTrack(string key) => Tracks.ContainsKey(key) ? this : ReplaceTrack(key, []);
    public EffectDocument RemoveTrack(string key) => Tracks.ContainsKey(key) ? new(Tracks.Remove(key)) : this;

    public EffectDocument SetKey(string track, ChartModKeyframe key, double? previousBeat = null)
    {
        var keys = Tracks.GetValueOrDefault(track, []);
        if (previousBeat is { } previous && !keys.Any(k => k.Beat == previous))
            throw new InvalidDataException("The selected key no longer exists.");
        if (previousBeat is { } old && old != key.Beat && keys.Any(k => k.Beat == key.Beat))
            throw new InvalidDataException("Another key already occupies that beat.");
        return ReplaceTrack(track, [.. keys.Where(k => k.Beat != (previousBeat ?? key.Beat)).Append(key).OrderBy(k => k.Beat)]);
    }

    public EffectDocument DeleteKeys(IReadOnlySet<EffectKey> selected)
    {
        var tracks = Tracks.ToBuilder();
        foreach (var (track, keys) in Tracks)
            if (keys.Any(k => selected.Contains(new(track, k.Beat))))
                tracks[track] = [.. keys.Where(k => !selected.Contains(new(track, k.Beat)))];
        return new(tracks.ToImmutable());
    }

    public EffectDocument MoveKeys(IReadOnlySet<EffectKey> selected, double beats, double values = 0)
    {
        if (!double.IsFinite(beats) || !double.IsFinite(values)) throw new InvalidDataException("Key movement must be finite.");
        var document = this;
        foreach (var (track, keys) in Tracks)
        {
            if (!keys.Any(k => selected.Contains(new(track, k.Beat)))) continue;
            var moved = keys.Select(k => selected.Contains(new(track, k.Beat)) ? k with { Beat = k.Beat + beats, Value = k.Value + values } : k);
            document = document.ReplaceTrack(track, [.. moved.OrderBy(k => k.Beat)]);
        }
        return document;
    }

    public EffectDocument SetValuesAt(double beat, IReadOnlyDictionary<string, double> values, bool autoKey)
    {
        if (UsesEmbeddedEffects) throw new InvalidDataException("Start a keyframe modchart before editing scene elements.");
        if (!autoKey && values.Keys.Any(track => !Contains(new(track, beat))))
            throw new InvalidDataException("No key at this beat. Enable Auto-key to create keys at the playhead.");
        var document = this;
        foreach (var (track, value) in values)
        {
            var key = Tracks.GetValueOrDefault(track, []).FirstOrDefault(k => k.Beat == beat, new ChartModKeyframe(beat, value));
            document = document.SetKey(track, key with { Value = value });
        }
        return document;
    }

    private EffectDocument ReplaceTrack(string track, ImmutableArray<ChartModKeyframe> keys)
    {
        keys = Canonical(keys);
        _ = ChartModFile.Compile(new Dictionary<string, ImmutableArray<ChartModKeyframe>> { [track] = keys });
        if (Tracks.Sum(p => (long)p.Value.Length) - Tracks.GetValueOrDefault(track, []).Length + keys.Length > 100_000)
            throw new InvalidDataException("The document has too many keys.");
        if (Tracks.TryGetValue(track, out var old) && old.AsSpan().SequenceEqual(keys.AsSpan())) return this;
        return new(Tracks.SetItem(track, keys));
    }

    private static ImmutableArray<ChartModKeyframe> Canonical(ImmutableArray<ChartModKeyframe> keys) =>
        keys.Any(k => k.Curve.HasValue && k.Ease != ChartEase.Linear)
            ? [.. keys.Select(k => k.Curve.HasValue ? k with { Ease = ChartEase.Linear } : k)] : keys;

    public bool ContentEquals(EffectDocument other) => UsesEmbeddedEffects == other.UsesEmbeddedEffects && Tracks.Count == other.Tracks.Count
        && Tracks.All(p => other.Tracks.TryGetValue(p.Key, out var keys) && p.Value.AsSpan().SequenceEqual(keys.AsSpan()));

    public static double Snap(double beat, double interval) => interval <= 0 ? beat : Math.Round(beat / interval, MidpointRounding.AwayFromZero) * interval;
}

/// <summary>Undo stores whole immutable documents. A drag previews many frames and commits as one operation.</summary>
public sealed class EffectHistory(EffectDocument initial)
{
    private readonly Stack<(EffectDocument Document, string Label)> _undo = [];
    private readonly Stack<(EffectDocument Document, string Label)> _redo = [];
    private EffectDocument? _gesture;
    public EffectDocument Current { get; private set; } = initial;
    public EffectDocument Saved { get; private set; } = initial;
    public bool Dirty => !Current.ContentEquals(Saved);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Edit(EffectDocument document, string label)
    {
        if (_gesture is not null) throw new InvalidOperationException("Finish the active drag first.");
        if (Current.ContentEquals(document)) return false;
        _undo.Push((Current, label)); _redo.Clear(); Current = document;
        return true;
    }
    public void BeginGesture() => _gesture ??= Current;
    public bool Preview(EffectDocument document)
    {
        if (_gesture is null) throw new InvalidOperationException("No drag is active.");
        if (Current.ContentEquals(document)) return false;
        Current = document;
        return true;
    }
    public void EndGesture()
    {
        if (_gesture is not { } before) return;
        _gesture = null;
        if (!before.ContentEquals(Current)) { _undo.Push((before, "Move keys")); _redo.Clear(); }
    }
    public void CancelGesture()
    {
        if (_gesture is not { } before) return;
        Current = before; _gesture = null;
    }
    public bool Undo()
    {
        EndGesture();
        if (!_undo.TryPop(out var before)) return false;
        _redo.Push((Current, before.Label)); Current = before.Document;
        return true;
    }
    public bool Redo()
    {
        EndGesture();
        if (!_redo.TryPop(out var after)) return false;
        _undo.Push((Current, after.Label)); Current = after.Document;
        return true;
    }
    public void MarkSaved() => Saved = Current;
}
