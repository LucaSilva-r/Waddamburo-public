using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Library;

public enum LibraryNodeKind { Folder, Song, Chart, Course }

/// <summary>One node of the library tree (free-movies.md, Song select: one library tree).</summary>
public sealed class LibraryNode(LibraryNodeKind kind, string title)
{
    public LibraryNodeKind Kind => kind;
    public string Title => title;
    public string? Subtitle { get; init; }
    public string? Artist { get; init; }
    /// <summary>A chart's stars (a course's: its songs' are its charts).</summary>
    public int? Stars { get; init; }
    /// <summary>A chart's course: easy, normal, hard, oni, ura; none for a chart that has no course.</summary>
    public string? Course { get; init; }
    /// <summary>A course whose songs are all in the library, a chart that can be played.</summary>
    public bool Playable { get; init; } = true;
    /// <summary>What the engine knows it by (a course's status, a chart's key).</summary>
    public object? Tag { get; init; }
    public List<LibraryNode> Children { get; } = [];
}

/// <summary>
/// The visible rows of a library tree, as a movie lists them: a folder (or song, or course) opens in place, its nodes
/// under it one level deeper; one cursor. Published to a movie as rows.count, selected.row and, for the window the
/// movie asked for (rows.window), the row.* lists.
/// </summary>
public sealed class LibraryRows
{
    private readonly List<LibraryNode> _top;
    private readonly HashSet<LibraryNode> _open = [];
    private List<(LibraryNode Node, int Depth)> _rows = [];
    private (int First, int Count) _window = (0, 0);
    private readonly HashSet<int> _published = [];

    public LibraryRows(IEnumerable<LibraryNode> top)
    {
        _top = [.. top];
        rebuild();
    }

    public IReadOnlyList<(LibraryNode Node, int Depth)> Rows => _rows;

    /// <summary>The row under the cursor.</summary>
    public int Cursor { get; private set; }

    public LibraryNode? Selected => (uint)Cursor < (uint)_rows.Count ? _rows[Cursor].Node : null;

    /// <summary>The selection changed since the last publish (a movie's details follow it).</summary>
    public bool SelectionChanged { get; private set; } = true;

    // Request answers: null taken, else why not.
    public string? Window(int first, int count)
    {
        if (first < 0 || count < 0 || count > 200)
            return "rows.window takes a first row of 0 or more and up to 200 rows";
        _window = (first, count);
        return null;
    }

    public string? Select(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return $"there is no row {index} ({_rows.Count} rows)";
        if (index != Cursor) SelectionChanged = true;
        Cursor = index;
        return null;
    }

    public string? Open(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return $"there is no row {index} ({_rows.Count} rows)";
        var node = _rows[index].Node;
        if (node.Children.Count == 0)
            return $"row {index} ({node.Title}) has nothing to open";
        if (_open.Add(node)) rebuild();
        return null;
    }

    public string? Close(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return $"there is no row {index} ({_rows.Count} rows)";
        var node = _rows[index].Node;
        if (!_open.Remove(node))
            return $"row {index} ({node.Title}) is not open";
        // The cursor stays on what it was on, or on the closed row when that is now hidden.
        var selected = Selected;
        rebuild();
        var at = selected is null ? -1 : _rows.FindIndex(row => row.Node == selected);
        Select(at >= 0 ? at : Math.Min(index, _rows.Count - 1));
        return null;
    }

    private void rebuild()
    {
        var rows = new List<(LibraryNode, int)>();
        void add(IEnumerable<LibraryNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                rows.Add((node, depth));
                if (_open.Contains(node)) add(node.Children, depth + 1);
            }
        }
        add(_top, 0);
        var selected = Selected;
        _rows = rows;
        Cursor = selected is null ? 0 : Math.Max(0, _rows.FindIndex(row => row.Node == selected));
    }

    /// <summary>Tells the movie where the rows stand (each value only when it changed: the provider drops repeats).</summary>
    public void Publish(LumenPlayer movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        movie.Publish("rows.count", LumenHostValue.FromNumber(_rows.Count));
        movie.Publish("selected.row", LumenHostValue.FromNumber(Cursor));
        movie.Publish("selected.kind", Selected is { } chosen ? LumenHostValue.FromString(chosen.Kind.ToString().ToLowerInvariant()) : LumenHostValue.Undefined);
        movie.Publish("selected.title", Selected is { } titled ? LumenHostValue.FromString(titled.Title) : LumenHostValue.Undefined);
        var end = Math.Min(_rows.Count, _window.First + _window.Count);
        foreach (var index in _published.Where(index => index < _window.First || index >= end).ToArray())
        {
            foreach (var name in Names) movie.PublishItem(name, index, LumenHostValue.Undefined);
            _published.Remove(index);
        }
        for (var index = _window.First; index < end; index++)
        {
            var (node, depth) = _rows[index];
            static LumenHostValue text(string? value) => value is null ? LumenHostValue.Undefined : LumenHostValue.FromString(value);
            movie.PublishItem("row.kind", index, LumenHostValue.FromString(node.Kind.ToString().ToLowerInvariant()));
            movie.PublishItem("row.depth", index, LumenHostValue.FromNumber(depth));
            movie.PublishItem("row.opened", index, LumenHostValue.FromBoolean(_open.Contains(node)));
            movie.PublishItem("row.title", index, LumenHostValue.FromString(node.Title));
            movie.PublishItem("row.subtitle", index, text(node.Subtitle));
            movie.PublishItem("row.count", index, node.Children.Count > 0 ? LumenHostValue.FromNumber(node.Children.Count) : LumenHostValue.Undefined);
            movie.PublishItem("row.stars", index, node.Stars is { } stars ? LumenHostValue.FromNumber(stars) : LumenHostValue.Undefined);
            movie.PublishItem("row.course", index, text(node.Course));
            movie.PublishItem("row.playable", index, LumenHostValue.FromBoolean(node.Playable));
            _published.Add(index);
        }
        SelectionChanged = false;
    }

    private static readonly string[] Names = ["row.kind", "row.depth", "row.opened", "row.title", "row.subtitle", "row.count",
        "row.stars", "row.course", "row.playable"];
}
