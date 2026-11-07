using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class EffectPreviewTests
{
    private static PlayableChart Chart() => new(new(new(SongSourceKind.Tja, "synthetic"), "oni"), TimeSpan.Zero,
        TimeSpan.FromSeconds(16), [], [new(TimeSpan.Zero, 120, 4, 4), new(TimeSpan.FromSeconds(4), 240, 3, 4)],
        [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], [new(TimeSpan.Zero, true), new(TimeSpan.FromSeconds(4), true)]);

    [Fact]
    public void FileKeyBeatsArePreservedInsteadOfInferredFromCompiledInitialValues()
    {
        var tracks = ChartModFile.ReadTracks("""{"wddmod":1,"tracks":{"don.yaw":[{"beat":8,"value":10},{"beat":16,"value":90,"ease":"outQuad"}],"don.pitch":[{"beat":12,"value":20}]}}"""u8, null);
        var chart = ChartModFile.Apply(Chart(), tracks);
        var preview = new EffectPreview(chart, tracks);
        Assert.Equal(8, preview.Tracks["don.yaw"][0].Beat);
        Assert.Equal(12, Assert.Single(preview.Tracks["don.pitch"]).Beat);
        Assert.Equal(10, preview.ValueAt("don.yaw", TimeSpan.FromSeconds(-2)));
        Assert.Equal(70, preview.ValueAt("don.yaw", 12), 8);
        Assert.Collection(preview.Bars, v => Assert.Equal(0, v), v => Assert.Equal(8, v));
        Assert.Equal(3, preview.MeasureAt(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void EmptyAuthoredTracksRemainVisibleWithoutChangingDrawing()
    {
        var tracks = ChartModFile.ReadTracks("""{"wddmod":1,"tracks":{"don.yaw":[]}}"""u8, null);
        var chart = ChartModFile.Apply(Chart(), tracks);
        var preview = new EffectPreview(chart, tracks);
        Assert.Empty(preview.Tracks["don.yaw"]);
        Assert.Empty(chart.Effects.Mods);
        Assert.Equal(ChartHash.Compute(Chart(), TaikoCourse.Oni), ChartHash.Compute(chart, TaikoCourse.Oni));
    }

    [Fact]
    public void TempoMappingAndDelaySeekUseTheSameBeatMapAsGameplay()
    {
        var chart = Chart() with { Effects = new() { Beats = [new(TimeSpan.Zero, 0, 2), new(TimeSpan.FromSeconds(2), 4, 0), new(TimeSpan.FromSeconds(3), 4, 4)] } };
        var preview = new EffectPreview(chart);
        Assert.Equal(4, preview.BeatAt(TimeSpan.FromSeconds(2.5)));
        Assert.Equal(TimeSpan.FromSeconds(3), preview.TimeAt(4));
        Assert.Equal(TimeSpan.FromSeconds(3.5), preview.TimeAt(6));
        Assert.Equal(6, preview.BeatAt(preview.TimeAt(6)));
    }

    [Fact]
    public void TimedTjaSegmentsStillAppearWhenThereIsNoKeyframeFile()
    {
        var chart = Chart() with { Effects = new() { Mods = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "don", "yaw", 90, ChartEase.InQuad)] } };
        var preview = new EffectPreview(chart);
        Assert.Equal([2d, 6d], preview.Tracks["don.yaw"].Select(k => k.Beat));
        Assert.Equal(22.5, preview.ValueAt("don.yaw", 4));
        Assert.Empty(new EffectPreview(Chart()).Tracks);
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(600, 1200)]
    public void PanelsLeaveAnUnobscuredStageAcrossAspectRatios(int width, int height)
    {
        foreach (var layout in new[] { EditorLayout.Calculate(width, height), EditorLayout.Calculate(width, height, .3f, .32f, .6f) })
        {
            var stage = layout.Stage;
            Assert.True(stage.Width > 0 && stage.Height > 0);
            Assert.True(stage.X > layout.Targets.X + layout.Targets.Width);
            Assert.True(stage.X + stage.Width < layout.Inspector.X);
            Assert.True(stage.Y > layout.Toolbar.Height);
            Assert.True(stage.Y + stage.Height < layout.Tracks.Y);
            var region = stage.Normalized(width, height);
            Assert.InRange(region.X + region.Width, 0, 1);
            Assert.InRange(region.Y + region.Height, 0, 1);
        }
        Assert.Equal(.18f, EditorLayout.Snap(.185f, .18f));
        Assert.Equal(.25f, EditorLayout.Snap(.25f, .18f));
    }

    [Theory]
    [InlineData(-1e308, 1e308)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void GraphFitIsFiniteAndLeavesRoomAroundConstantAndExtremeValues(double min, double max)
    {
        var scale = GraphScale.Fit([min, max]);
        Assert.True(double.IsFinite(scale.Fraction(min)));
        Assert.InRange(scale.Fraction(min), 0, 1);
        Assert.InRange(scale.Fraction(max), 0, 1);
        Assert.True(double.IsFinite(scale.ValueAt(0)));
        Assert.True(double.IsFinite(scale.ValueAt(1)));
    }

    [Fact]
    public void ZoomAndPanKeepTheBeatUnderThePointerAndAllowPreroll()
    {
        var view = new BeatView();
        view.Frame(-4, 120);
        var anchor = view.BeatAt(.3);
        view.Zoom(3, .3);
        Assert.Equal(anchor, view.BeatAt(.3), 9);
        var start = view.Start;
        view.Pan(.1);
        Assert.Equal(start - view.Span * .1, view.Start, 9);
        Assert.True(view.GridStep(800) > 0);
        view.Frame(8, 8);
        Assert.InRange(view.FractionAt(8), 0, 1);
        view.Frame(1e308, 1e308);
        Assert.InRange(view.Start, -1e9, 1e9);
        Assert.True(view.Start + view.GridStep(1000) > view.Start);
    }
}
