using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class TaikoLongNotePresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BalloonStartsOnceCountsDownAndEndsOnce(bool pop)
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Balloon, 2);
        var session = sessionFor(chart);
        var layer = TestMovies.RecordingMovie(calls);
        var don = new Character();
        var completed = new List<(PlayableLongNoteKind Kind, bool Succeeded)>();
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => layer, layer, layer,
            onBalloonCompleted: (kind, succeeded) => completed.Add((kind, succeeded)),
            onNoteStarted: started.Add);
        presentation.Character = don;
        var told = listen(presentation);
        Assert.False(presentation.BalloonVisible);
        // The balloon waits on the target until the first don opens it, already counting that hit.
        presentation.Update(TimeSpan.FromSeconds(1));
        Assert.False(presentation.BalloonVisible);
        Assert.True(presentation.IsHeld(0, TimeSpan.FromSeconds(1.1)));
        presentation.Update(TimeSpan.FromSeconds(1.1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.2));
        Assert.True(presentation.BalloonVisible);
        Assert.False(presentation.IsHeld(0, TimeSpan.FromSeconds(1.2)));
        Assert.Equal("opened Balloon", don.Told[^1]);
        if (pop) session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.3));
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(4));
        Assert.Equal(["start Balloon 2", "count 1", $"end Balloon {pop}", "none"], told);
        Assert.Empty(calls); // the overlay's calls are the gameplay adapter's
        Assert.Empty(layer.Player.Diagnostics);
        Assert.True(presentation.BalloonVisible);
        Assert.Equal($"closed Balloon {pop}", don.Told[^1]);
        Assert.Equal([(PlayableLongNoteKind.Balloon, pop)], completed);
        presentation.AdvanceAnimations();
        Assert.False(presentation.BalloonVisible);
        Assert.Equal($"closed Balloon {pop}", don.Told[^1]);
        var motionCount = don.Told.Count;
        presentation.AdvanceAnimations();
        Assert.Equal(motionCount, don.Told.Count);
        Assert.Single(completed);
        Assert.Equal([PlayableLongNoteKind.Balloon], started);
    }

    [Fact]
    public void KusudamaCompletionReportsItsOwnKind()
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Kusudama, 1);
        var session = sessionFor(chart);
        var layer = TestMovies.RecordingMovie(calls);
        var completed = new List<(PlayableLongNoteKind Kind, bool Succeeded)>();
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => layer, layer, layer,
            kusudama: layer, onBalloonCompleted: (kind, succeeded) => completed.Add((kind, succeeded)),
            onNoteStarted: started.Add);
        var told = listen(presentation);
        presentation.Update(TimeSpan.FromSeconds(1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.1));
        presentation.Update(TimeSpan.FromSeconds(3));
        Assert.Equal([(PlayableLongNoteKind.Kusudama, true)], completed);
        Assert.Contains("end Kusudama True", told);
        Assert.Equal([PlayableLongNoteKind.Kusudama], started);
    }

    [Fact]
    public void RollsHaveIndependentWidthsAndTellEachHitOnce()
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Roll);
        var session = sessionFor(chart);
        var layer = TestMovies.RecordingMovie(calls);
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => TestMovies.RecordingMovie(calls), layer, layer,
            onNoteStarted: started.Add, movies: new Told(calls));
        var told = listen(presentation);
        var noteLayer = Assert.Single(presentation.NoteLayers(TimeSpan.Zero, (time, _, _) => (400 + (float)time.TotalSeconds * 100, 250f), (400, 250))).Layer;
        Assert.NotSame(layer.Player, noteLayer.Player);
        Assert.Contains(("sized", 100d), calls);
        Assert.Equal(100d, noteLayer.Player.Provided("note.length").AsNumber());
        presentation.Update(TimeSpan.FromSeconds(1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.1));
        session.SubmitInput(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1.2));
        presentation.AdvanceAnimations();
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(3));
        Assert.Equal(["count 1", "count 2", "none"], told);
        Assert.Equal(2, calls.Count(c => c.Item1 == "struck"));
        Assert.Contains(calls, c => c.Item1 == "stepped");
        Assert.Equal([PlayableLongNoteKind.Roll], started);
    }

    [Fact]
    public void UnhitRollNeverCountsAHit()
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Roll);
        var session = sessionFor(chart);
        var layer = TestMovies.RecordingMovie(calls);
        var presentation = new TaikoLongNotePresentation(chart, session, _ => TestMovies.RecordingMovie(calls), layer, layer);
        var told = listen(presentation);
        presentation.Update(TimeSpan.FromSeconds(1));
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(told, line => line.StartsWith("count", StringComparison.Ordinal) && line != "count 0");
    }

    [Theory]
    [InlineData(PlayableLongNoteKind.Roll)]
    [InlineData(PlayableLongNoteKind.BigRoll)]
    public void RollTailRemainsVisibleAfterEndUntilItLeavesTheScreen(PlayableLongNoteKind kind)
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(kind);
        var layer = TestMovies.RecordingMovie(calls);
        var presentation = new TaikoLongNotePresentation(chart, sessionFor(chart), _ => TestMovies.RecordingMovie(calls), layer, layer,
            movies: new Told(calls));

        (float, float) position(TimeSpan noteTime, TimeSpan _, ChartBranch? __) => (400 + (float)(noteTime - TimeSpan.FromSeconds(6.1)).TotalSeconds * 100, 250);
        var visible = Assert.Single(presentation.NoteLayers(TimeSpan.FromSeconds(6.1), position, (400, 250)));
        Assert.Equal(-110, visible.Layer.Transform.X);
        Assert.Equal(100, calls.Last(call => call.Item1 == "sized").Item2);

        (float, float) offscreen(TimeSpan noteTime, TimeSpan _, ChartBranch? __) => (400 + (float)(noteTime - TimeSpan.FromSeconds(7.1)).TotalSeconds * 100, 250);
        Assert.Empty(presentation.NoteLayers(TimeSpan.FromSeconds(7.1), offscreen, (400, 250)));
    }

    /// <summary>What the long notes' movies are told beyond the provider, as (call, length).</summary>
    private sealed class Told(List<(string, double)> calls) : ITaikoLongNoteMovies
    {
        public void Shown(LumenPlayer note) => calls.Add(("shown", double.NaN));
        public void Sized(LumenPlayer note, float length) => calls.Add(("sized", length));
        public void Stepped(LumenPlayer note) => calls.Add(("stepped", double.NaN));
        public void Struck(LumenPlayer note) => calls.Add(("struck", double.NaN));
    }

    private static PlayableChart chartFor(PlayableLongNoteKind kind, int quota = 0) => new(
        new(new(SongSourceKind.Tja, "synthetic"), "presentation"), TimeSpan.Zero, TimeSpan.FromSeconds(3), [],
        [new(TimeSpan.Zero, 120, 4, 4)], [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], [],
        [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), kind, quota)]);

    // What the presentation tells: starts and ends of balloons and kusudama, and the count under way at each change.
    private static List<string> listen(TaikoLongNotePresentation presentation)
    {
        var told = new List<string>();
        presentation.Started += (kind, hits, _) => told.Add($"start {kind} {hits}");
        presentation.Ended += (kind, popped) => told.Add($"end {kind} {popped}");
        presentation.Changed += () => told.Add(presentation.Active is { } active ? $"count {active.Count}" : "none");
        return told;
    }

    private static TaikoJudgementSession sessionFor(PlayableChart chart) => new(chart,
        new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)));

    private sealed class Character : ITaikoLongNoteCharacter
    {
        public List<string> Told { get; } = [];
        public void Opened(PlayableLongNoteKind overlay) => Told.Add($"opened {overlay}");
        public void Pumped(PlayableLongNote note) => Told.Add("pumped");
        public void Closed(PlayableLongNote note, bool popped) => Told.Add($"closed {note.Kind} {popped}");
    }
}
