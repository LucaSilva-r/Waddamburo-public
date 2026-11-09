using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class NoteGeometryTests
{
    private const float HitX = 400, HitY = 250;
    private static readonly SongKey Song = new(SongSourceKind.Tja, "geometry");

    // 120 BPM: a second is two beats, 474 px at scroll 1.
    private static PlayableChart chart(ChartScrollPoint? scroll = null, ChartEffects? effects = null) => new PlayableChart(
        new ChartKey(Song, "oni"), TimeSpan.Zero, TimeSpan.FromSeconds(30),
        [new PlayableHitObject(TimeSpan.FromSeconds(10), PlayableNoteKind.Don)],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [scroll ?? new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)])
    { Effects = effects ?? ChartEffects.None };

    private static NoteGeometry geometry(PlayableChart chart) => new(chart, HitX, HitY, new ChartModValues(chart.Effects.Mods));

    private static readonly TimeSpan Note = TimeSpan.FromSeconds(10);

    [Fact]
    public void APlainNoteScrollsInFromTheRightUprightAndOpaque()
    {
        var plain = geometry(chart());

        Assert.Equal((HitX + 474, HitY), plain.Point(Note, Note - TimeSpan.FromSeconds(1), Note, null));
        var placed = plain.Place(Note, Note - TimeSpan.FromSeconds(1), null, hitNote: true)!.Value;
        Assert.Equal((1f, 0f, 0f, 1f, HitX + 474, HitY), (placed.Transform.M11, placed.Transform.M12, placed.Transform.M21, placed.Transform.M22, placed.Transform.X, placed.Transform.Y));
        Assert.Equal(1, placed.Alpha);
        Assert.Null(plain.Place(Note, Note - TimeSpan.FromSeconds(3), null)); // off the stage's right
        Assert.Equal(NoteGeometry.VisibleWindow(Note, 474, HitX), plain.Window(Note, null));
    }

    [Fact]
    public void ComplexScrollAndATargetMoveBendThePath()
    {
        var moved = geometry(chart(new ChartScrollPoint(TimeSpan.Zero, 1) { Imaginary = 1 },
            new ChartEffects { JudgeMoves = [new ChartJudgeMove(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 100, -50)] }));

        // Half a second early, after the move: 237 px right and 237 px below the moved target.
        var (x, y) = moved.Point(Note, Note - TimeSpan.FromSeconds(0.5), Note, null);
        Assert.Equal(HitX + 100 + 237, x, 3);
        Assert.Equal(HitY - 50 + 237, y, 3);
        // A bar line stands across a slanted path (45 degrees here).
        var bar = moved.Place(Note, Note - TimeSpan.FromSeconds(0.5), null, barLine: true)!.Value.Transform;
        Assert.Equal(bar.M11, bar.M12, 5);
    }

    [Fact]
    public void SuddenHidesThenHoldsANoteUntilItsWait()
    {
        var sudden = geometry(chart(new ChartScrollPoint(TimeSpan.Zero, 1) { Show = TimeSpan.FromSeconds(2), Wait = TimeSpan.FromSeconds(1) }));

        Assert.Null(sudden.Place(Note, Note - TimeSpan.FromSeconds(2.5), null, hitNote: true));
        // Shown 2 s early but held where it is due 1 s out, then it moves.
        Assert.Equal(HitX + 474, sudden.Place(Note, Note - TimeSpan.FromSeconds(1.5), null, hitNote: true)!.Value.Transform.X, 3);
        Assert.Equal(HitX + 237, sudden.Place(Note, Note - TimeSpan.FromSeconds(0.5), null, hitNote: true)!.Value.Transform.X, 3);
    }

    [Fact]
    public void ScrollingByBeatStandsStillThroughADelay()
    {
        // The beat count stops for a second from 8 s (a #DELAY), so the note is due at beat 18, not 20.
        var beats = geometry(chart(new ChartScrollPoint(TimeSpan.Zero, 1) { ByBeat = true }, new ChartEffects
        {
            Beats = [new ChartBeatPoint(TimeSpan.Zero, 0, 2), new ChartBeatPoint(TimeSpan.FromSeconds(8), 16, 0),
                new ChartBeatPoint(TimeSpan.FromSeconds(9), 16, 2)],
        }));

        var during = beats.Point(Note, TimeSpan.FromSeconds(8.2), Note, null);
        Assert.Equal(during, beats.Point(Note, TimeSpan.FromSeconds(8.8), Note, null));
        Assert.Equal(HitX + 2 * 948 / 4f, during.X, 3); // two beats out
    }

    [Fact]
    public void NoteModsTurnSizeAndFadeTheNote()
    {
        var modded = geometry(chart(effects: new ChartEffects
        {
            Mods = [new ChartModEase(TimeSpan.Zero, TimeSpan.Zero, "notes", "zoom", 2, ChartEase.Linear),
                new ChartModEase(TimeSpan.Zero, TimeSpan.Zero, "notes", "alpha", 0.5, ChartEase.Linear),
                new ChartModEase(TimeSpan.Zero, TimeSpan.Zero, "notes", "dizzy", 1, ChartEase.Linear)],
        }));

        var placed = modded.Place(Note, Note - TimeSpan.FromSeconds(0.5), null, hitNote: true)!.Value;
        Assert.Equal(2, Math.Sqrt(placed.Transform.M11 * placed.Transform.M11 + placed.Transform.M12 * placed.Transform.M12), 3);
        Assert.NotEqual(0, placed.Transform.M12); // turned by how far it is
        Assert.Equal(0.5f, placed.Alpha);
        // A bar line is not a hit note: no note mods.
        Assert.Equal(1, modded.Place(Note, Note - TimeSpan.FromSeconds(0.5), null, barLine: true)!.Value.Transform.M11);
    }

    [Fact]
    public void NoteWobblesFadeOutSoTheNoteArrivesOnTheTarget()
    {
        var wobbly = geometry(chart(effects: new ChartEffects
        {
            Mods = [.. new[] { "drunk", "tipsy", "wave", "beat" }.Select(static mod =>
                new ChartModEase(TimeSpan.Zero, TimeSpan.Zero, "notes", mod, 3, ChartEase.Linear))],
        }));

        Assert.Equal((HitX, HitY), wobbly.Point(Note, Note, Note, null, hitNote: true));
        // Further out the wobble is in full.
        Assert.NotEqual((HitX + 474, HitY), wobbly.Point(Note, Note - TimeSpan.FromSeconds(1), Note, null, hitNote: true));
    }

    [Fact]
    public void AnElementTurnsAboutItsPivotAndRolesMapToUniversalTargets()
    {
        // 90 degrees about (100, 100): the pivot stays, a point right of it goes below it.
        var (matrix, alpha) = StageEffects.Transform(0, 0, 90, 1, 1, 0.5, (100, 100))!.Value;
        var pivot = matrix.Transform(100, 100);
        var right = matrix.Transform(110, 100);
        Assert.Equal((100f, 100f), (MathF.Round(pivot.X, 3), MathF.Round(pivot.Y, 3)));
        Assert.Equal((100f, 110f), (MathF.Round(right.X, 3), MathF.Round(right.Y, 3)));
        Assert.Equal(0.5f, alpha);
        Assert.Null(StageEffects.Transform(0, 0, 0, 1, 1, 1, (100, 100)));

        Assert.Equal("dancers", StageEffects.TargetOf("dance"));
        Assert.Equal("gauge", StageEffects.TargetOf("gage_don_1p_hard"));
        Assert.Equal("playfield", StageEffects.TargetOf("onp_don"));
        Assert.Null(StageEffects.TargetOf("action_fusen_1p"));
    }
}
