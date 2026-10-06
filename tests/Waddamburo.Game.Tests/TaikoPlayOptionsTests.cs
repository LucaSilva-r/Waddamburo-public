using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoPlayOptionsTests
{
    private static PlayableChart chart(int notes = 100) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(notes + 10),
        Enumerable.Range(0, notes).Select(index => new PlayableHitObject(TimeSpan.FromSeconds(index + 1),
            index % 10 == 9 ? PlayableNoteKind.BigDon : index % 2 == 0 ? PlayableNoteKind.Don : PlayableNoteKind.Ka)),
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false), new ChartEffectPoint(TimeSpan.FromSeconds(50), true)],
        [new ChartBarLine(TimeSpan.Zero, true)],
        [new PlayableLongNote(TimeSpan.FromSeconds(notes + 2), TimeSpan.FromSeconds(notes + 3), PlayableLongNoteKind.Kusudama, 20)]);

    [Fact]
    public void SongSelectBitsReadBackAsTheyWereChosen()
    {
        // GetSession: 1 << item id per row (真打 1, よんばい 4, ドロン 5, でたらめ 8), plus 0 for rows left alone.
        var options = TaikoPlayOptions.FromSession(1 << 0 | 1 << 1 | 1 << 4 | 1 << 5 | 1 << 8);

        Assert.Equal(new TaikoPlayOptions(true, 14, true, false, TaikoRandom.Detarame), options);
        Assert.Equal(1 << 1 | 1 << 4 | 1 << 5 | 1 << 8, options.SessionBits);
        // Nijiiro's speeds: the exact one is kept beside its art's range (1.3x shows ばいそく).
        var slow = TaikoPlayOptions.FromSession(1 << 2, speedIndex: 3);
        Assert.Equal((1.3, 2, "1.3x"), (slow.ScrollSpeed, slow.SpeedIcon, TaikoPlayOptions.SpeedText(3)));
        Assert.Equal(slow, TaikoPlayOptions.FromBits(slow.Bits));
        Assert.Equal(options, TaikoPlayOptions.FromBits(options.Bits));
        Assert.Equal(TaikoPlayOptions.None, TaikoPlayOptions.FromSession(1));
        Assert.False(TaikoPlayOptions.FromSession(1).Any);
    }

    [Fact]
    public void AbekobeSwapsEveryNoteAndRandomSwapsByItsChanceTheSameForTheSameSeed()
    {
        var original = chart(1000);
        var reversed = new TaikoPlayOptions(false, 1, false, true, TaikoRandom.None).Apply(original, 1);
        static bool isDon(PlayableHitObject note) => note.Kind is PlayableNoteKind.Don or PlayableNoteKind.BigDon;
        Assert.All(original.HitObjects.Zip(reversed.HitObjects), pair =>
            Assert.Equal((!isDon(pair.First), pair.First.IsStrong), (isDon(pair.Second), pair.Second.IsStrong)));

        var detarame = new TaikoPlayOptions(false, 1, false, false, TaikoRandom.Detarame);
        var swapped = original.HitObjects.Zip(detarame.Apply(original, 7).HitObjects).Count(pair => pair.First.Kind != pair.Second.Kind);
        Assert.InRange(swapped, 420, 580); // about half
        Assert.Equal(detarame.Apply(original, 7).HitObjects.ToArray(), detarame.Apply(original, 7).HitObjects.ToArray());
    }

    [Fact]
    public void SpeedScalesTheScrollAndNoOptionsLeaveTheChartAlone()
    {
        var original = chart();
        Assert.Same(original, TaikoPlayOptions.None.Apply(original, 1));
        Assert.Equal(3, new TaikoPlayOptions(false, 12, false, false, TaikoRandom.None).Apply(original, 1).ScrollPoints[0].Multiplier);
    }

    [Fact]
    public void ShinuchiScoresEveryHitTheSameAndAimsAtAMillion()
    {
        var played = new TaikoPlayOptions(true, 1, false, false, TaikoRandom.None).Apply(chart(), 1);

        // 100 notes + 10 big ones; the kusudama became a balloon worth 19 x 300 + 5000.
        Assert.Equal(PlayableLongNoteKind.Balloon, played.LongNotes[0].Kind);
        Assert.Equal((1_000_000 - 10_700 + 1099) / 1100 * 10, played.ShinuchiBase);
        var score = new TaikoScore(played);
        var at = TimeSpan.FromSeconds(60); // in Go-Go: no factor
        for (var note = 0; note < 3; note++)
            score.Apply(new TaikoNoteJudgement(note, played.HitObjects[note], TaikoHitResult.Great, TimeSpan.Zero, false), at);
        score.Apply(new TaikoNoteJudgement(3, played.HitObjects[3], TaikoHitResult.Good, TimeSpan.Zero, false), at);
        var shinuchi = played.ShinuchiBase!.Value;
        Assert.Equal(3 * shinuchi + (shinuchi / 2 + 9) / 10 * 10, score.Value);
    }
}
