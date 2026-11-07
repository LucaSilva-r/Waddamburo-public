using System.Collections.Immutable;

namespace Waddamburo.Catalog.Tests;

public sealed class ChartModKeyframeTests
{
    private static PlayableChart Chart() => new(new ChartKey(new SongKey(SongSourceKind.Stock, "synthetic"), "oni"),
        TimeSpan.Zero, TimeSpan.FromSeconds(20), [], [new(TimeSpan.Zero, 120, 4, 4)],
        [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], []);

    [Fact]
    public void KeysHoldInterpolateOvershootAndSeekWithoutState()
    {
        var cues = ChartModFile.Read("""{"wddmod":1,"tracks":{"don.yaw":[{"beat":8,"value":30},{"beat":16,"value":390,"ease":"outBack"},{"beat":20,"value":90,"curve":[0.3,0,0.2,1]}]}}"""u8, null);
        var chart = Chart();
        var edited = ChartModFile.Apply(chart, cues);
        var mods = edited.Effects.Mods.ToArray();
        double At(double seconds) => ChartMods.ValueAt(mods, TimeSpan.FromSeconds(seconds), 0);
        Assert.Equal(30, At(-2));
        Assert.Equal(30, At(0));
        Assert.Equal(30, At(2));
        Assert.Equal(390, At(8));
        Assert.True(At(7) > 390);
        Assert.Equal(90, At(12));
        Assert.Equal(30, At(0)); // Seek backwards after sampling the end.
        Assert.Equal(ChartHash.Compute(chart, TaikoCourse.Oni), ChartHash.Compute(edited, TaikoCourse.Oni));
    }

    [Fact]
    public void LayoutAppliesOnlyToChartsWithoutModchartChanges()
    {
        var layout = ChartModFile.ReadTracks("""{"wddmod":1,"tracks":{"don.yaw":[{"beat":0,"value":45}]}}"""u8, null);
        var plain = ChartModFile.ApplyLayout(Chart(), layout);
        Assert.Equal(45, ChartMods.ValueAt(plain.Effects.Mods.ToArray(), TimeSpan.FromSeconds(5), 0));

        var authored = ChartModFile.Apply(Chart(), ChartModFile.Read("""{"wddmod":1,"tracks":{"don.roll":[{"beat":0,"value":10}]}}"""u8, null));
        Assert.Same(authored, ChartModFile.ApplyLayout(authored, layout)); // chart effects replace the layout, no merge
    }

    [Fact]
    public void FirstKeyHoldsEvenDuringAnInitialDelayAndStepChangesOnlyAtItsKey()
    {
        var chart = Chart() with { Effects = new() { Beats = [new(TimeSpan.Zero, 0, 0), new(TimeSpan.FromSeconds(2), 0, 2)] } };
        var cues = ChartModFile.Read("""{"wddmod":1,"tracks":{"notes.alpha":[{"beat":4,"value":0.2},{"beat":8,"value":1,"ease":"step"}]}}"""u8, null);
        var mods = ChartModFile.Apply(chart, cues).Effects.Mods.ToArray();
        Assert.Equal(.2, ChartMods.ValueAt(mods, TimeSpan.Zero, 1));
        Assert.Equal(.2, ChartMods.ValueAt(mods, TimeSpan.FromSeconds(5.99), 1));
        Assert.Equal(1, ChartMods.ValueAt(mods, TimeSpan.FromSeconds(6), 1));
    }

    [Fact]
    public void WriterRoundTripsCurvesAndUsesStableTrackOrder()
    {
        var tracks = new Dictionary<string, ImmutableArray<ChartModKeyframe>>
        {
            ["notes.alpha"] = [],
            ["don.yaw"] = [new(8, 0), new(16, 360, ChartEase.OutBack), new(20, 90, Curve: new(.3, -.5, .2, 1.5))],
        };
        var bytes = ChartModFile.Write(tracks);
        var read = ChartModFile.ReadTracks(bytes, null);
        Assert.Equal(tracks["don.yaw"].AsEnumerable(), read["don.yaw"].AsEnumerable());
        Assert.Empty(read["notes.alpha"]);
        Assert.Equal(bytes, ChartModFile.Write(read));
    }

    [Fact]
    public void CourseOverridesOnlyItsTracksAndEmptyArrayCanDisableOne()
    {
        var json = """{"wddmod":1,"tracks":{"don.x":[{"beat":0,"value":1}]},"courses":{"*":{"tracks":{"don.yaw":[{"beat":0,"value":30}]}},"oni":{"tracks":{"don.yaw":[]}},"easy":{"tracks":{"don.x":[{"beat":0,"value":9}]}}}}"""u8;
        Assert.Empty(ChartModFile.ReadTracks(json, TaikoCourse.Oni)["don.yaw"]);
        Assert.Equal(30, ChartModFile.ReadTracks(json, null)["don.yaw"][0].Value);
        Assert.Equal(9, ChartModFile.ReadTracks(json, TaikoCourse.Easy)["don.x"][0].Value);
        Assert.Equal(1, ChartModFile.ReadTracks(json, TaikoCourse.Oni)["don.x"][0].Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"wddmod\":\"1\"}")]
    [InlineData("{\"wddmod\":1} trailing")]
    [InlineData("{\"wddmod\":1,\"changes\":[]}")]
    [InlineData("{\"wddmod\":1,\"tracks\":{\"don.x\":[{\"beat\":0,\"value\":1,\"curve\":[2,0,0,1]}]}}")]
    [InlineData("{\"wddmod\":1,\"tracks\":{\"don.x\":[{\"beat\":0,\"value\":1},{\"beat\":0,\"value\":2}]}}")]
    [InlineData("{\"wddmod\":1,\"tracks\":{\"don.x\":[{\"beat\":0,\"value\":1,\"ease\":\"linear\",\"curve\":[0,0,1,1]}]}}")]
    public void MalformedFilesAreReportedAsInvalidData(string json) =>
        Assert.Throws<InvalidDataException>(() => ChartModFile.Read(System.Text.Encoding.UTF8.GetBytes(json), null));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(.3, .2)]
    public void BezierSolverInvertsMonotonicXIncludingFlatTangents(double x1, double x2)
    {
        var c = new ChartModCurve(x1, -.5, x2, 1.5);
        Assert.Equal(0, ChartMods.Bezier(c, 0));
        Assert.Equal(1, ChartMods.Bezier(c, 1));
        for (var i = 1; i < 100; i++)
        {
            var u = i / 100d;
            double Cubic(double a, double b) => 3 * (1-u) * (1-u) * u * a + 3 * (1-u) * u * u * b + u*u*u;
            Assert.Equal(Cubic(c.Y1, c.Y2), ChartMods.Bezier(c, Cubic(x1, x2)), 6);
        }
        Assert.True(ChartMods.Bezier(c, .001) < 0);
    }

    [Fact]
    public void DrawingBlockRoundTripsCurvesAndReadsPreviousVersion()
    {
        var chart = Chart() with { Effects = new() { Mods = [new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "don", "yaw", 360, ChartEase.Linear, new(.3, 0, .2, 1))] } };
        var data = ChartDrawing.Serialize(chart);
        Assert.Equal(chart.Effects.Mods.AsEnumerable(), ChartDrawing.Apply(Chart(), data).Effects.Mods.AsEnumerable());
        var named = chart with { Effects = chart.Effects with { Mods = [chart.Effects.Mods[0] with { Curve = null }] } };
        var old = ChartDrawing.Serialize(named)[..^1]; // v1 has no optional-curve flag.
        old[4] = 1;
        Assert.Equal(named.Effects.Mods.AsEnumerable(), ChartDrawing.Apply(Chart(), old).Effects.Mods.AsEnumerable());
        var plain = ChartDrawing.Serialize(Chart());
        plain[4] = 1; // With no mods, the old drawing layout is otherwise identical.
        Assert.Empty(ChartDrawing.Apply(Chart(), plain).Effects.Mods);
        BitConverter.GetBytes(2d).CopyTo(data, data.Length - 32);
        Assert.Throws<InvalidDataException>(() => ChartDrawing.Apply(Chart(), data));
    }
}
