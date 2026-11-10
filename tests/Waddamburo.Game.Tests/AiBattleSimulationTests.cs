using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class AiBattleSimulationTests
{
    private static PlayableChart chart() => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "synthetic"), "oni"), TimeSpan.Zero, TimeSpan.FromSeconds(12),
        [.. Enumerable.Range(1, 30).Select(index => new PlayableHitObject(TimeSpan.FromMilliseconds(index * 300),
            index % 2 == 0 ? PlayableNoteKind.Don : PlayableNoteKind.Ka))],
        [new ChartTimingPoint(TimeSpan.Zero, 100, 4, 4)], [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [.. Enumerable.Range(0, 5).Select(index => new ChartBarLine(TimeSpan.FromMilliseconds(index * 2400), true))],
        [new PlayableLongNote(TimeSpan.FromSeconds(9.1), TimeSpan.FromSeconds(10), PlayableLongNoteKind.Roll)]) { Level = 8 };

    private static TaikoReplay replay(PlayableChart source)
    {
        var replay = new TaikoReplay();
        foreach (var (note, index) in source.HitObjects.Select((note, index) => (note, index)))
            if (index % 7 != 0)
                replay.Add(note.Kind == PlayableNoteKind.Don ? TaikoInputAction.LeftDon : TaikoInputAction.LeftKa,
                    note.StartTime + TimeSpan.FromMilliseconds(index % 3 == 0 ? 60 : -10));
        for (var ms = 9120; ms < 10000; ms += 40)
            replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromMilliseconds(ms));
        return replay;
    }

    [Fact]
    public void LiveFramesAndOneShotReplayProduceIdenticalAiPressesAndSections()
    {
        var source = chart();
        var replayed = replay(source);
        var all = new AiBattleSimulation(source, TaikoCourse.Oni, 1234, (8, 500));
        var expected = all.Advance(replayed.Inputs, all.End + TimeSpan.FromTicks(1));
        var live = new AiBattleSimulation(source, TaikoCourse.Oni, 1234, (8, 500));
        var actual = new List<TaikoReplayInput>();
        // Frame watermarks deliberately coincide with decisions, inputs, and section boundaries.
        for (var ms = -200; ms <= 11000; ms += 10)
        {
            var time = TimeSpan.FromMilliseconds(ms);
            actual.AddRange(live.Advance([.. replayed.Inputs.TakeWhile(input => input.Time <= time)], time));
        }
        Assert.Equal(expected, actual);
        Assert.Equal(all.Outcome.Sections, live.Outcome.Sections);
        Assert.Equal(all.Timing, live.Timing);
        Assert.Equal(3, live.Outcome.Sections.Count);
        var scored = PlayRescore.Score(source, TaikoCourse.Oni, default, null, replayed,
            TaikoJudgementWindows.ForCourse(TaikoCourse.Oni));
        Assert.Equal((scored.Great, scored.Good, scored.Miss),
            (live.Outcome.Sections.Sum(section => section.Player.Great), live.Outcome.Sections.Sum(section => section.Player.Good),
                live.Outcome.Sections.Sum(section => section.Player.Miss)));
        Assert.All(live.Outcome.Sections, section => Assert.Equal(section.Player.Rolls, section.Ai.Rolls));
    }

    [Fact]
    public void SeedAndPriorArePartOfTheBattle()
    {
        var source = chart();
        var inputs = replay(source).Inputs;
        var first = new AiBattleSimulation(source, TaikoCourse.Oni, 1);
        var otherSeed = new AiBattleSimulation(source, TaikoCourse.Oni, 2);
        var otherPrior = new AiBattleSimulation(source, TaikoCourse.Oni, 1, (50, 100));
        var presses = first.Advance(inputs, first.End + TimeSpan.FromTicks(1));
        Assert.False(presses.SequenceEqual(otherSeed.Advance(inputs, otherSeed.End + TimeSpan.FromTicks(1))));
        Assert.False(presses.SequenceEqual(otherPrior.Advance(inputs, otherPrior.End + TimeSpan.FromTicks(1))));
        Assert.Throws<ArgumentException>(() => new AiBattleSimulation(source, TaikoCourse.Oni, 1, (0, -1)));
    }

    [Fact]
    public void VersionOneKeepsTheSameAiReplayForStoredServerSessions()
    {
        var source = chart();
        var simulation = new AiBattleSimulation(source, TaikoCourse.Oni, 1234, (8, 500));
        var presses = simulation.Advance(replay(source).Inputs, simulation.End + TimeSpan.FromTicks(1));
        // Pin the uncompressed stream, independent of gzip's runtime implementation.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var press in presses)
            {
                writer.Write(press.Time.Ticks);
                writer.Write((byte)press.Action);
            }
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
        Assert.Equal("9fffe25afdca88fca8ad97956fcc4212b309d34187dbdbf5ac3ba1fed09c8e70", digest);
    }

    [Fact]
    public void LiveProviderPrecisionAndStoredCanonicalNotesProduceTheSameBattle()
    {
        var original = chart();
        var source = new PlayableChart(original.Key, original.AuthoredOffset, original.Duration,
            original.HitObjects.Select(note => new PlayableHitObject(note.StartTime + TimeSpan.FromTicks(3), note.Kind, note.IsHand)),
            original.TimingPoints, original.ScrollPoints, original.EffectPoints, original.BarLines,
            original.LongNotes) { Level = original.Level };
        var stored = ChartHash.Deserialize(ChartHash.Serialize(source, TaikoCourse.Oni), source.Key.Song.Source).Chart;
        var inputs = replay(source).Inputs;
        var live = new AiBattleSimulation(source, TaikoCourse.Oni, 1234, (8, 500));
        var server = new AiBattleSimulation(stored, TaikoCourse.Oni, 1234, (8, 500));
        Assert.Equal(live.Advance(inputs, live.End + TimeSpan.FromTicks(1)), server.Advance(inputs, server.End + TimeSpan.FromTicks(1)));
        Assert.Equal(live.Outcome.Sections, server.Outcome.Sections);
        Assert.Equal(live.Timing, server.Timing);
    }

    [Fact]
    public void AiFollowsThePlayersBranchEvenWhenItsOwnHitsWouldChooseAnother()
    {
        var source = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Tja, "branch"), "oni"),
            TimeSpan.Zero, TimeSpan.FromSeconds(8),
            [new(TimeSpan.FromSeconds(0.5), PlayableNoteKind.Don),
             new(TimeSpan.FromSeconds(4.5), PlayableNoteKind.Ka) { Branch = ChartBranch.Normal },
             new(TimeSpan.FromSeconds(4.5), PlayableNoteKind.BigDon) { Branch = ChartBranch.Master }],
            [new(TimeSpan.Zero, 120, 4, 4)], [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)],
            [new(TimeSpan.Zero, true), new(TimeSpan.FromSeconds(2), true), new(TimeSpan.FromSeconds(4), true)])
        {
            Branching = new ChartBranching([new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), new(1, 1), new(1, 1), new(1, 1))], ChartBranchPoints.Default),
        };
        var replay = new TaikoReplay(); // No early hit: the player stays on normal.
        replay.Add(TaikoInputAction.LeftKa, TimeSpan.FromSeconds(4.5));
        var simulation = new AiBattleSimulation(source, TaikoCourse.Oni, 1234, (0, 0));
        var generated = simulation.Advance(replay.Inputs, simulation.End + TimeSpan.FromTicks(1));
        Assert.Contains(generated, input => input.Time == TimeSpan.FromSeconds(4.5) && input.Action is TaikoInputAction.LeftKa or TaikoInputAction.RightKa);
        Assert.Equal(2, simulation.Outcome.Sections.Sum(section => section.Ai.Great + section.Ai.Good + section.Ai.Miss));
    }
}
