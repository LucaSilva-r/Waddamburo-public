using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public readonly record struct PatternKey(string Track, ChartModKeyframe Key);
public sealed record EffectPatternEdit(EffectDocument Document, ImmutableHashSet<EffectKey> Keys);

/// <summary>A selection copied with relative beats, retaining its channels and incoming interpolation.</summary>
public sealed record EffectKeyPattern(double Origin, ImmutableArray<PatternKey> Keys)
{
    public double Span => Keys.IsEmpty ? 0 : Keys.Max(k => k.Key.Beat);
    public bool SingleTrack => Keys.Select(k => k.Track).Distinct().Take(2).Count() == 1;

    public static EffectKeyPattern Capture(EffectDocument document, IReadOnlySet<EffectKey> selected)
    {
        var points = document.Tracks.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p =>
            p.Value.Where(k => selected.Contains(new(p.Key, k.Beat))).Select(k => new PatternKey(p.Key, k))).ToArray();
        if (points.Length == 0 || points.Length != selected.Count) throw new InvalidDataException("Select keys first.");
        var origin = points.Min(k => k.Key.Beat);
        return new(origin, [.. points.Select(p => p with { Key = p.Key with { Beat = p.Key.Beat - origin } })]);
    }

    public EffectPatternEdit Paste(EffectDocument document, double beat, string? track = null) =>
        Apply(document, beat, 1, 0, true, track, false);

    /// <summary>Adds copies after the source pattern. Matching shared endpoints retain their closing interpolation.</summary>
    public EffectPatternEdit Repeat(EffectDocument document, int copies, double period, bool overwrite = false)
    {
        if (!double.IsFinite(period) || period <= 0 || period < Span)
            throw new InvalidDataException("The repeat period must be positive and at least the selection's length.");
        return Apply(document, Origin + period, copies, period, overwrite, null, true);
    }

    private EffectPatternEdit Apply(EffectDocument document, double beat, int copies, double period, bool overwrite, string? track, bool seam)
    {
        if (document.UsesEmbeddedEffects) throw new InvalidDataException("Start a keyframe modchart before pasting keys.");
        if (!double.IsFinite(beat) || beat < 0 || copies is < 1 or > 1000 || Keys.IsEmpty || (long)copies * Keys.Length > 100_000)
            throw new InvalidDataException("The paste/repeat is too large or its beat is invalid.");
        if (track is not null && !SingleTrack) throw new InvalidDataException("Only a single-track selection can be remapped.");
        var tracks = document.Tracks.ToBuilder();
        var selected = ImmutableHashSet.CreateBuilder<EffectKey>();
        foreach (var group in Keys.GroupBy(k => track ?? k.Track))
        {
            var merged = tracks.GetValueOrDefault(group.Key, []).ToDictionary(k => k.Beat);
            for (var i = 0; i < copies; i++)
                foreach (var point in group)
                {
                    var key = point.Key with { Beat = beat + i * period + point.Key.Beat };
                    if (merged.TryGetValue(key.Beat, out var existing))
                    {
                        if (seam && point.Key.Beat == 0 && existing.Value == key.Value)
                        { selected.Add(new(group.Key, key.Beat)); continue; }
                        if (!overwrite) throw new InvalidDataException("A repeat overlaps an existing key. Increase the period or enable replacement.");
                    }
                    merged[key.Beat] = key; selected.Add(new(group.Key, key.Beat));
                }
            tracks[group.Key] = [.. merged.Values.OrderBy(k => k.Beat)];
        }
        return new(EffectDocument.FromTracks(tracks.ToImmutable()), selected.ToImmutable());
    }
}
