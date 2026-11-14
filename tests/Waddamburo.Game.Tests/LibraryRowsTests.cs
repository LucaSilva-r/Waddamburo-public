using Waddamburo.Game.Library;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class LibraryRowsTests
{
    private static LibraryNode folder(string name, params LibraryNode[] nodes)
    {
        var node = new LibraryNode(LibraryNodeKind.Folder, name);
        node.Children.AddRange(nodes);
        return node;
    }

    private static LibraryNode chart(string name) => new(LibraryNodeKind.Chart, name) { Stars = 7 };

    [Fact]
    public void FoldersOpenInPlaceAndTheCursorStaysOnItsNode()
    {
        var rows = new LibraryRows([folder("Green", folder("六段", chart("a"), chart("b"))), folder("Blue")]);
        Assert.Equal(["Green", "Blue"], rows.Rows.Select(static row => row.Node.Title));
        Assert.Null(rows.Open(0));
        Assert.Null(rows.Open(1));
        Assert.Equal([("Green", 0), ("六段", 1), ("a", 2), ("b", 2), ("Blue", 0)], rows.Rows.Select(static row => (row.Node.Title, row.Depth)));
        Assert.Null(rows.Select(4));
        Assert.Null(rows.Close(1)); // the course closes under the cursor: it stays on Blue
        Assert.Equal("Blue", rows.Selected?.Title);
        Assert.Equal(2, rows.Cursor);
        Assert.NotNull(rows.Open(2)); // Blue holds nothing
        Assert.NotNull(rows.Select(9));
    }

    [Fact]
    public void OnlyTheWindowIsPublishedAndRowsThatLeaveItAreDropped()
    {
        var rows = new LibraryRows([folder("A"), folder("B"), folder("C")]);
        var movie = FreeMovies.Load("dani_select/dani_select");
        Assert.Null(rows.Window(0, 2));
        rows.Publish(movie);
        Assert.Equal(3d, movie.Provided("rows.count").AsNumber());
        Assert.Equal("A", movie.Provided("row.title#0").AsString());
        Assert.Equal(LumenHostValueKind.Undefined, movie.Provided("row.title#2").Kind);
        Assert.Null(rows.Window(1, 2));
        rows.Publish(movie);
        Assert.Equal(LumenHostValueKind.Undefined, movie.Provided("row.title#0").Kind);
        Assert.Equal("C", movie.Provided("row.title#2").AsString());
    }
}
