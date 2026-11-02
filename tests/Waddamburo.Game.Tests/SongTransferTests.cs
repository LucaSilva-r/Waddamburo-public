using Waddamburo.Game.Online;

namespace Waddamburo.Game.Tests;

public sealed class SongTransferTests
{
    [Fact]
    public void ABlobArrivesWholeThroughALinkThatLosesAndReordersPackets()
    {
        var sender = new SongTransfer { Resend = TimeSpan.Zero };
        var receiver = new SongTransfer();
        var blob = new byte[300_000 + 17];
        new Random(1).NextBytes(blob);
        var random = new Random(2);
        sender.Send(blob);

        byte[]? arrived = null;
        for (var pump = 0; pump < 2000 && arrived is null; pump++)
        {
            List<byte[]> confirmations = [];
            // A third lost each way; what's left arrives shuffled.
            foreach (var packet in sender.Due().Where(_ => random.NextDouble() > 0.33).OrderBy(_ => random.Next()))
                if (receiver.Read(packet, out var reply) && reply is not null)
                    confirmations.Add(reply);
            foreach (var reply in confirmations.Where(_ => random.NextDouble() > 0.33))
                Assert.True(sender.Read(reply, out _));
            arrived = receiver.TakeComplete();
        }

        Assert.Equal(blob, arrived);
        Assert.Null(receiver.TakeComplete());
    }

    [Fact]
    public void ANewBlobReplacesOneUnderWayAndJunkIsIgnored()
    {
        var sender = new SongTransfer { Resend = TimeSpan.Zero };
        var receiver = new SongTransfer();
        sender.Send(new byte[50_000]);
        foreach (var packet in sender.Due().Take(3))
            receiver.Read(packet, out _);
        var second = Enumerable.Range(0, 5000).Select(static index => (byte)index).ToArray();
        sender.Send(second);

        byte[]? arrived = null;
        for (var pump = 0; pump < 100 && arrived is null; pump++)
        {
            foreach (var packet in sender.Due())
                if (receiver.Read(packet, out var reply) && reply is not null)
                    sender.Read(reply, out _);
            arrived = receiver.TakeComplete();
        }

        Assert.Equal(second, arrived);
        Assert.Null(sender.SendProgress);
        Assert.False(receiver.Read([SongTransfer.ChunkType, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out _) && receiver.TakeComplete() is not null);
        Assert.False(receiver.Read([0x7F, 1, 2], out _));
    }
}
