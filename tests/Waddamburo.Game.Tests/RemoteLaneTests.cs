using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;

namespace Waddamburo.Game.Tests;

public sealed class RemoteLaneTests
{
    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108));
    private static readonly TimeSpan Strong = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    private static TimeSpan ms(double value) => TimeSpan.FromMilliseconds(value);

    // Dense on purpose: notes 100 ms apart, with big notes, so a hit applied too early or late changes the result.
    private static PlayableChart chart() => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(8),
        Enumerable.Range(0, 60).Select(index => new PlayableHitObject(ms(1_000 + index * 100),
            (index % 7) switch { 0 => PlayableNoteKind.BigDon, 3 => PlayableNoteKind.BigKa, 1 or 4 => PlayableNoteKind.Ka, _ => PlayableNoteKind.Don })),
        [new ChartTimingPoint(TimeSpan.Zero, 150, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)],
        [new PlayableLongNote(ms(7_100), ms(7_600), PlayableLongNoteKind.Roll)]);

    // A player who is mostly on time, sometimes late or early, misses now and then, and drums the roll.
    private static List<TaikoReplayInput> play(int seed)
    {
        var random = new Random(seed);
        var inputs = new List<TaikoReplayInput>();
        foreach (var note in chart().HitObjects)
        {
            if (random.Next(10) == 0)
                continue;
            var ka = note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;
            var time = note.StartTime + ms(random.Next(-70, 71));
            inputs.Add(new(ka ? TaikoInputAction.LeftKa : TaikoInputAction.LeftDon, time));
            if (note.IsStrong && random.Next(3) > 0)
                inputs.Add(new(ka ? TaikoInputAction.RightKa : TaikoInputAction.RightDon, time + ms(random.Next(0, 40))));
        }
        for (var time = ms(7_120); time < ms(7_600); time += ms(40))
            inputs.Add(new(TaikoInputAction.LeftDon, time));
        return [.. inputs.OrderBy(static input => input.Time)];
    }

    private static List<string> judge(Action<TaikoJudgementSession> run)
    {
        var session = new TaikoJudgementSession(chart(), Windows, Strong);
        var judged = new List<string>();
        // A miss by timeout is judged when the clock passes it, so its offset depends on frame timing, here as in a
        // game: only a hit's offset is the player's.
        session.Judged += judgement => judged.Add(judgement.Result == TaikoHitResult.Miss
            ? $"{judgement.NoteIndex}:Miss:{judgement.StrongHitCompleted}"
            : $"{judgement.NoteIndex}:{judgement.Result}:{judgement.Offset?.TotalMilliseconds:F3}:{judgement.StrongHitCompleted}");
        session.LongNoteHit += progress => judged.Add($"roll:{progress.Hits}");
        run(session);
        return judged;
    }

    private static List<string> solo(IReadOnlyList<TaikoReplayInput> inputs) => judge(session =>
    {
        foreach (var input in inputs)
            session.SubmitInput(input.Action, input.Time);
        session.AdvanceTo(TimeSpan.MaxValue);
    });

    [Theory]
    [InlineData(1, 0.0, 0)]
    [InlineData(2, 0.3, 0)]
    [InlineData(3, 0.3, 4)]
    [InlineData(4, 0.6, 9)]
    public void TheOtherPlayersLaneEndsAsTheirOwnGameDidThroughALossyLine(int seed, double loss, int reorder)
    {
        var inputs = play(seed);
        var random = new Random(seed * 31);
        var sender = new HitRelay();
        var receiver = new HitRelay();
        var feed = new RemoteLaneFeed { Delay = ms(150) };
        var inFlight = new List<(int Arrives, byte[] Packet)>();
        var next = 0;

        var remote = judge(session =>
        {
            for (var tick = 0; tick < 700; tick++)
            {
                var now = TimeSpan.FromTicks(tick * Tick.Ticks);
                // Their game: hits as they happen, a packet each tick, some lost, some held up.
                for (; next < inputs.Count && inputs[next].Time <= now; next++)
                    sender.Record(inputs[next].Action, inputs[next].Time);
                sender.SetClock(now);
                if (random.NextDouble() >= loss)
                    inFlight.Add((tick + 4 + random.Next(reorder + 1), sender.Build()));
                foreach (var (_, packet) in inFlight.Where(flight => flight.Arrives == tick).ToList())
                    Assert.True(receiver.Read(packet, feed));
                inFlight.RemoveAll(flight => flight.Arrives == tick);
                // ... and the confirmations back, through the same line.
                if (random.NextDouble() >= loss)
                    sender.Read(receiver.Build(), new RemoteLaneFeed());
                // This game, showing their lane 150 ms behind.
                feed.Apply(session, now);
                Assert.True(feed.Known >= session.CurrentTime, "The lane went past what had arrived.");
            }
            // The song is over and everything has arrived.
            while (receiver.Received < inputs.Count)
            {
                receiver.Read(sender.Build(), feed);
                sender.Read(receiver.Build(), new RemoteLaneFeed());
            }
            feed.Apply(session, TimeSpan.MaxValue - ms(1_000));
            feed.Receive([], TimeSpan.MaxValue);
            feed.Apply(session, TimeSpan.MaxValue - ms(1_000));
        });

        Assert.Equal(inputs.Count, receiver.Received);
        Assert.Equal(solo(inputs), remote);
    }

    [Fact]
    public void ALatePacketMakesNotesWaitRatherThanMiss()
    {
        var feed = new RemoteLaneFeed { Delay = ms(100) };
        var missed = 0;
        var session = new TaikoJudgementSession(chart(), Windows, Strong);
        session.Judged += judgement => missed += judgement.Result == TaikoHitResult.Miss ? 1 : 0;

        // Nothing heard from them for two seconds past the first note.
        feed.Apply(session, ms(3_000));
        Assert.Equal(0, missed);
        Assert.True(session.CurrentTime < ms(1_000));

        feed.Receive([new(TaikoInputAction.LeftDon, ms(1_000))], ms(1_050));
        feed.Apply(session, ms(3_000));
        Assert.Equal(0, missed);
        Assert.Equal(ms(1_050), session.CurrentTime);
    }

    [Fact]
    public void BrokenPacketsAreRefusedAndChangeNothing()
    {
        var sender = new HitRelay();
        sender.Record(TaikoInputAction.LeftDon, ms(1_000));
        sender.SetClock(ms(1_100));
        var good = sender.Build();
        var receiver = new HitRelay();
        var feed = new RemoteLaneFeed();

        byte[][] broken =
        [
            [],
            [0x02, .. good[1..]], // not ours
            good[..^1], // cut short
            [.. good[..17], 200, .. good[18..]], // more hits than it carries
            [.. good[..^1], 9], // no such drum
            [.. good[..9], .. BitConverter.GetBytes(ms(900).Ticks), .. good[17..]], // a hit after how far it says it got
        ];
        foreach (var packet in broken)
            Assert.False(receiver.Read(packet, feed));
        Assert.Equal(0, receiver.Received);
        Assert.Equal(TimeSpan.MinValue, feed.Known);

        Assert.True(receiver.Read(good, feed));
        Assert.True(receiver.Read(good, feed)); // again: nothing new
        Assert.Equal(1, receiver.Received);
        Assert.Equal(ms(1_100), feed.Known);
    }

    [Fact]
    public void AStalledLineSendsWhatFitsAndOnlyClaimsThat()
    {
        var sender = new HitRelay();
        for (var index = 0; index < HitRelay.MaxInputsPerPacket + 10; index++)
            sender.Record(TaikoInputAction.LeftDon, ms(1_000 + index * 10));
        sender.SetClock(ms(5_000));
        var receiver = new HitRelay();
        var feed = new RemoteLaneFeed();

        receiver.Read(sender.Build(), feed);
        Assert.Equal(HitRelay.MaxInputsPerPacket, receiver.Received);
        Assert.Equal(ms(1_000 + (HitRelay.MaxInputsPerPacket - 1) * 10), feed.Known);

        sender.Read(receiver.Build(), new RemoteLaneFeed());
        receiver.Read(sender.Build(), feed);
        Assert.Equal(HitRelay.MaxInputsPerPacket + 10, receiver.Received);
        Assert.Equal(ms(5_000), feed.Known);
    }
}
