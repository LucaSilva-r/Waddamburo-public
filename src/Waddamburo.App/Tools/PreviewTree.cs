namespace Waddamburo.App.Tools;

/// <summary>Browser hierarchy independent of rendering, decoders, and user-owned file contents.</summary>
internal sealed class PreviewTree
{
    internal sealed class Node(string label, string path, bool folder = false, string? movie = null)
    {
        public string Label { get; } = label;
        public string Path { get; } = path;
        public bool IsFolder { get; } = folder;
        public string? Movie { get; } = movie;
        public Node? Parent { get; private set; }
        public List<Node> Children { get; } = [];
        public bool Expanded { get; set; }
        public bool MoviesLoaded { get; set; }
        public bool IsArchive => !IsFolder && Movie is null
            && System.IO.Path.GetExtension(Path).Equals(".ddp", StringComparison.OrdinalIgnoreCase);
        public bool CanExpand => IsFolder || IsArchive;

        public void Add(Node child)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    internal readonly record struct Row(Node Node, int Depth);
    public Node Root { get; }

    public PreviewTree(string directory, IEnumerable<string> files)
    {
        directory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        Root = new Node(System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            is { Length: > 0 } name ? name : directory, directory, folder: true) { Expanded = true };
        var folders = new Dictionary<string, Node>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { [directory] = Root };
        Node folder(string path)
        {
            if (folders.TryGetValue(path, out var existing)) return existing;
            var parent = folder(System.IO.Path.GetDirectoryName(path)
                ?? throw new ArgumentException("Files must be inside the browser root.", nameof(files)));
            var created = new Node(System.IO.Path.GetFileName(path), path, folder: true);
            parent.Add(created);
            folders.Add(path, created);
            return created;
        }
        foreach (var path in files)
            folder(System.IO.Path.GetDirectoryName(path)!).Add(new Node(System.IO.Path.GetFileName(path), path));
        sort(Root);
    }

    public List<Row> Visible()
    {
        var rows = new List<Row>();
        void visit(Node node, int depth)
        {
            rows.Add(new Row(node, depth));
            if (node.Expanded)
                foreach (var child in node.Children) visit(child, depth + 1);
        }
        visit(Root, 0);
        return rows;
    }

    public static void Reveal(Node node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            parent.Expanded = true;
    }

    public Node? FindFile(string path)
    {
        Node? find(Node node)
        {
            if (!node.IsFolder && node.Movie is null && node.Path == path) return node;
            foreach (var child in node.Children)
                if (find(child) is { } found) return found;
            return null;
        }
        return find(Root);
    }

    public static void SetMovies(Node archive, IEnumerable<string> names)
    {
        archive.Children.Clear();
        foreach (var name in names)
            archive.Add(new Node(System.IO.Path.GetFileName(name.Replace('\\', '/')), archive.Path, movie: name));
        archive.MoviesLoaded = true;
        archive.Expanded = true;
    }

    private static void sort(Node node)
    {
        node.Children.Sort((left, right) => left.IsFolder != right.IsFolder
            ? left.IsFolder ? -1 : 1
            : StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label));
        foreach (var child in node.Children) sort(child);
    }
}
