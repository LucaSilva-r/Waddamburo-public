using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>A chart's modchart values (<see cref="ChartMods"/>) looked up by target and property; a function of chart time.</summary>
public sealed class ChartModValues
{
    private readonly Dictionary<(string Target, string Property), ChartModEase[]> _changes;

    public ChartModValues(IEnumerable<ChartModEase> changes) =>
        _changes = changes.GroupBy(static change => (change.Target, change.Property))
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static change => change.Start).ToArray());

    public bool IsEmpty => _changes.Count == 0;

    /// <summary>Whether the chart changes anything of <paramref name="target"/>.</summary>
    public bool Touches(string target) => _changes.Keys.Any(key => key.Target == target);

    /// <summary>The value at <paramref name="time"/> (its default when the chart never changes it).</summary>
    public double Value(string target, string property, TimeSpan time) =>
        _changes.TryGetValue((target, property), out var changes)
            ? ChartMods.ValueAt(changes, time, ChartMods.Default(property)) : ChartMods.Default(property);
}
