using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.SongSelect;

public sealed record SongChartSlot(TaikoCourse Course, SongChartDescriptor? Chart);

/// <summary>Four visible choices over a song's complete, individually identified chart list.</summary>
public sealed class SongChartCarousel
{
    public const int VisibleCount = 4;
    private readonly int[] _selected = new int[2];
    private readonly bool[] _confirmed = new bool[2];
    private readonly bool[] _utilities = new bool[2];
    private readonly int _firstPlayable;

    public SongChartCarousel(SongDescriptor song, IEnumerable<int> sides)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(sides);
        Charts = [.. song.Charts.Where(static chart => chart.Course is not null)
            .OrderBy(static chart => chart.Course)
            .ThenBy(static chart => chart.StarRating ?? chart.Level ?? 0)];
        var standard = new[] { TaikoCourse.Easy, TaikoCourse.Normal, TaikoCourse.Hard, TaikoCourse.Oni }
            .Select(course => new SongChartSlot(course, Charts.FirstOrDefault(chart => chart.Course == course))).ToArray();
        Slots = [.. standard, .. Charts.Where(chart => !standard.Any(slot => slot.Chart?.Key == chart.Key))
            .Select(chart => new SongChartSlot(chart.Course!.Value, chart))];
        Sides = [.. sides.Distinct().Order()];
        if (Charts.IsEmpty) throw new ArgumentException("The song has no playable charts.", nameof(song));
        if (Sides.IsEmpty || Sides.Any(static side => side is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(sides));
        _firstPlayable = Enumerable.Range(0, Slots.Length).First(index => Slots[index].Chart is not null);
        foreach (var side in Sides) _selected[side] = _firstPlayable;
        WindowStart = Math.Max(0, _firstPlayable - VisibleCount + 1);
    }

    public ImmutableArray<SongChartDescriptor> Charts { get; }
    public ImmutableArray<SongChartSlot> Slots { get; }
    public ImmutableArray<int> Sides { get; }
    public int WindowStart { get; private set; }
    public bool Ready => Sides.All(IsConfirmed);
    public int Revision { get; private set; }
    public int Selected(int side) { validateSide(side); return _selected[side]; }
    public bool IsConfirmed(int side) { validateSide(side); return _confirmed[side]; }
    public bool IsUtility(int side) { validateSide(side); return _utilities[side]; }
    public bool CanEnterUtilities(int side) => !IsConfirmed(side) && Selected(side) == _firstPlayable;
    public void EnterUtilities(int side)
    {
        validateSide(side);
        _utilities[side] = true;
        updateWindow(Sides.FirstOrDefault(other => !_utilities[other], side));
    }
    public void LeaveUtilities(int side) { validateSide(side); _utilities[side] = false; updateWindow(side); }
    public SongChartDescriptor Choice(int side) => Slots[Selected(side)].Chart!;

    public void Move(int side, int delta)
    {
        validateSide(side);
        if (_confirmed[side]) return;
        var next = (int)Math.Clamp((long)_selected[side] + delta, _firstPlayable, Slots.Length - 1L);
        while (next >= _firstPlayable && next < Slots.Length && Slots[next].Chart is null)
            next += delta < 0 ? -1 : 1;
        if (next < _firstPlayable || next >= Slots.Length) return;
        if (next == _selected[side]) return;
        _selected[side] = next;
        updateWindow(side);
        Revision++;
    }

    private void updateWindow(int focused)
    {
        var visible = Sides.Where(side => !_utilities[side]).Select(side => _selected[side]).ToArray();
        if (visible.Length == 0 || visible.Max() < VisibleCount) { WindowStart = 0; return; }
        var maximumStart = Math.Max(0, Slots.Length - VisibleCount);
        var lower = Math.Max(0, visible.Max() - VisibleCount + 1);
        var upper = Math.Min(visible.Min(), maximumStart);
        if (lower <= upper)
            WindowStart = Math.Clamp(WindowStart, lower, upper);
        else
            WindowStart = Math.Clamp(WindowStart, Math.Max(0, _selected[focused] - VisibleCount + 1),
                Math.Min(_selected[focused], maximumStart));
    }

    public void Confirm(int side) { validateSide(side); _confirmed[side] = true; Revision++; }
    public void UndoConfirm(int side) { validateSide(side); _confirmed[side] = false; Revision++; }

    private void validateSide(int side)
    {
        if (!Sides.Contains(side)) throw new ArgumentOutOfRangeException(nameof(side));
    }
}
