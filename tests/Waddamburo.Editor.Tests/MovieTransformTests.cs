using System.Numerics;
using System.Text.Json.Nodes;

namespace Waddamburo.Editor.Tests;

public sealed class MovieTransformTests
{
    private static Matrix3x2 trs(Vector2 position, float angle, Vector2 scale) =>
        Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(angle * MathF.PI / 180) * Matrix3x2.CreateTranslation(position);
    private static void near(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < .002f, $"Expected {expected}, got {actual}");

    [Theory]
    [InlineData(3, 100, 80)] [InlineData(4, 50, 80)] [InlineData(5, 0, 80)] [InlineData(6, 0, 40)]
    [InlineData(7, 0, 0)] [InlineData(8, 50, 0)] [InlineData(9, 100, 0)] [InlineData(10, 100, 40)]
    public void OppositeAnchorStaysFixedWhileTheGrabbedHandleFollowsThePointer(int handle, float x, float y)
    {
        var pivot = new Vector2(50, 40);
        var anchor = MovieTransform.ScaleAnchor(handle, Vector2.Zero, new(100, 80), pivot, false);
        near(new(x, y), anchor);
        var start = new Vector2(100, 80) - anchor;
        if (handle is 4 or 8) start.X = 50;
        if (handle is 6 or 10) start.Y = 40;
        var point = anchor + (start - anchor) * 1.5f;
        var factors = MovieTransform.ScaleFactors(handle, start, point, anchor, false);
        var change = Matrix3x2.CreateScale(factors, anchor);
        near(anchor, Vector2.Transform(anchor, change));
        near(point, Vector2.Transform(start, change));
    }

    [Fact]
    public void AltCanSwitchAnchorMidDragWithoutAccumulatingChanges()
    {
        var start = new Vector2(100, 80);
        var point = new Vector2(150, 120);
        Vector2 result(bool alt)
        {
            var anchor = MovieTransform.ScaleAnchor(7, Vector2.Zero, start, start / 2, alt);
            return Vector2.Transform(Vector2.Zero, Matrix3x2.CreateScale(MovieTransform.ScaleFactors(7, start, point, anchor, false), anchor));
        }
        near(Vector2.Zero, result(false));
        near(new(-50, -40), result(true));
        near(Vector2.Zero, result(false));
    }

    [Fact]
    public void ShiftLocksTheDominantMovementAxisUntilReleased()
    {
        var axis = 0;
        near(new(30, 20), MovieTransform.ConstrainMove(new(30, 20), 0, false, ref axis));
        near(new(30, 0), MovieTransform.ConstrainMove(new(30, 20), 0, true, ref axis));
        near(new(35, 0), MovieTransform.ConstrainMove(new(35, 100), 0, true, ref axis));
        near(new(35, 100), MovieTransform.ConstrainMove(new(35, 100), 0, false, ref axis));
        near(new(0, 100), MovieTransform.ConstrainMove(new(35, 100), 0, true, ref axis));
    }

    [Theory]
    [InlineData(0)] [InlineData(37)] [InlineData(-90)]
    public void GroupRotationAndUniformScaleWorkThroughRotatedScaledParents(float rotation)
    {
        var parent = trs(new(80, -20), rotation, new(2));
        var change = Matrix3x2.CreateRotation(.7f, new(100, 50)) * Matrix3x2.CreateScale(1.4f, new Vector2(100, 50));
        foreach (var position in new[] { new Vector2(10, 20), new Vector2(100, 40) })
        {
            var original = trs(position, 15, new(1.2f, .8f));
            Assert.True(MovieTransform.TryApply(original, parent, change, out var p, out var a, out var scale));
            var actual = trs(p, a, scale) * parent;
            foreach (var point in new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY })
                near(Vector2.Transform(point, original * parent * change), Vector2.Transform(point, actual));
        }
    }

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(6)] [InlineData(7)] [InlineData(10)]
    public void ShiftPreservesProportionsForCornersAndSides(int handle)
    {
        var factors = MovieTransform.ScaleFactors(handle, new(100, 80), new(150, 100), Vector2.Zero, true);
        Assert.Equal(factors.X, factors.Y);
    }

    [Fact]
    public void ReflectionsRoundTripThroughDecomposition()
    {
        var local = trs(new(10, 20), 30, new(-2, 3));
        Assert.True(MovieTransform.TryApply(local, Matrix3x2.Identity, Matrix3x2.Identity, out var p, out var a, out var scale));
        near(Vector2.Transform(new(4, 5), local), Vector2.Transform(new(4, 5), trs(p, a, scale)));
    }

    [Fact]
    public void UnsupportedTransformsAreRefusedBeforeWriting()
    {
        Assert.False(MovieTransform.TryApply(trs(Vector2.Zero, 45, Vector2.One), Matrix3x2.Identity, Matrix3x2.CreateScale(2, 1), out _, out _, out _));
        Assert.False(MovieTransform.TryApply(Matrix3x2.Identity, Matrix3x2.CreateScale(0), Matrix3x2.Identity, out _, out _, out _));
        Assert.False(MovieTransform.TryApply(Matrix3x2.Identity, Matrix3x2.Identity, Matrix3x2.CreateScale(0), out _, out _, out _));
    }

    [Fact]
    public void SelectedAncestorsExcludeDescendantsWithoutMatchingSimilarDepths()
    {
        Assert.True(MovieTransform.HasSelectedAncestor("1/2/3", ["1", "1/2/3"]));
        Assert.False(MovieTransform.HasSelectedAncestor("10/2", ["1", "10/2"]));
        Assert.True(MovieTransform.HasSelectedAncestor("10/2", [""]));
    }

    [Fact]
    public void AWholeGestureAcrossTwoLayersIsOneUndoAndRedoStep()
    {
        var folder = Directory.CreateTempSubdirectory("movie-transform-test");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "movie.json"), """{"sprites":{"main":{"layers":[{"keys":[{"frame":0}]},{"keys":[{"frame":0}]}]}}}""");
            var document = new MovieDocument(folder.FullName);
            var original = document.Root.ToJsonString();
            for (var frame = 1; frame <= 3; frame++)
                document.Edit(root =>
                {
                    foreach (var layer in root["sprites"]!["main"]!["layers"]!.AsArray().OfType<JsonObject>())
                        MovieDocument.Set(MovieDocument.KeyAt(layer, 0), "x", frame * 10);
                }, continues: frame > 1);
            var final = document.Root.ToJsonString();
            document.Undo();
            Assert.Equal(original, document.Root.ToJsonString());
            Assert.False(document.CanUndo);
            document.Redo();
            Assert.Equal(final, document.Root.ToJsonString());
        }
        finally { folder.Delete(true); }
    }
}
