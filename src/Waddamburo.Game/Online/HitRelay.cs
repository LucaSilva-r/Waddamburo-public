using System.Buffers.Binary;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Online;

/// <summary>
/// One player's drum hits over <see cref="PeerLink"/>, which may lose or reorder packets. Every packet repeats the hits the
/// other side hasn't confirmed yet, with how far this game had got, and confirms how many of theirs have arrived; so hits
/// arrive in order, once each, and "how far" never runs ahead of a hit that's still missing.
/// </summary>
/// <remarks>Packet: type, received (u32), first (u32), known (i64 ticks), count (u8), count × (time i64 ticks, action u8).</remarks>
public sealed class HitRelay
{
    public const byte PacketType = 0x01;
    private const int HeaderLength = 1 + 4 + 4 + 8 + 1;
    private const int InputLength = 9;

    /// <summary>A packet repeats at most this many unconfirmed hits; the rest follow once these are confirmed.</summary>
    public const int MaxInputsPerPacket = 64;

    private readonly List<TaikoReplayInput> _sent = [];
    private readonly object _lock = new();
    private int _confirmed;
    private int _received;
    private TimeSpan _clock = TimeSpan.MinValue;

    /// <summary>How many of the other player's hits have arrived.</summary>
    public int Received
    {
        get
        {
            lock (_lock)
                return _received;
        }
    }

    /// <summary>A hit of this player's, in judgement time.</summary>
    public void Record(TaikoInputAction action, TimeSpan time)
    {
        lock (_lock)
            _sent.Add(new TaikoReplayInput(action, time));
    }

    /// <summary>How far this player's game has got: every hit up to it has been recorded.</summary>
    public void SetClock(TimeSpan time)
    {
        lock (_lock)
            if (time > _clock)
                _clock = time;
    }

    /// <summary>The next packet to send: the unconfirmed hits, and confirmation of the other player's.</summary>
    public byte[] Build()
    {
        lock (_lock)
        {
            var count = Math.Min(_sent.Count - _confirmed, MaxInputsPerPacket);
            // Cut short: the other side may only go as far as the last hit it gets.
            var known = _confirmed + count < _sent.Count ? _sent[_confirmed + count - 1].Time : _clock;
            var packet = new byte[HeaderLength + count * InputLength];
            packet[0] = PacketType;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), (uint)_received);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), (uint)_confirmed);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(9), known.Ticks);
            packet[17] = (byte)count;
            for (var index = 0; index < count; index++)
            {
                var input = _sent[_confirmed + index];
                var offset = HeaderLength + index * InputLength;
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(offset), input.Time.Ticks);
                packet[offset + 8] = (byte)input.Action;
            }
            return packet;
        }
    }

    /// <summary>
    /// Reads a packet from the other player into <paramref name="feed"/>; false when it's not one or doesn't add up (it
    /// comes from the other player, so nothing in it is trusted).
    /// </summary>
    public bool Read(ReadOnlySpan<byte> packet, RemoteLaneFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (packet.Length < HeaderLength || packet[0] != PacketType)
            return false;
        var confirmed = BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]);
        var first = BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]);
        var known = TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(packet[9..]));
        int count = packet[17];
        if (count > MaxInputsPerPacket || packet.Length != HeaderLength + count * InputLength)
            return false;
        var inputs = new TaikoReplayInput[count];
        var previous = TimeSpan.MinValue;
        for (var index = 0; index < count; index++)
        {
            var offset = HeaderLength + index * InputLength;
            var time = TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(packet[offset..]));
            var action = packet[offset + 8];
            if (action > (byte)TaikoInputAction.RightKa || time < previous || time > known)
                return false;
            inputs[index] = new TaikoReplayInput((TaikoInputAction)action, time);
            previous = time;
        }
        lock (_lock)
        {
            if (confirmed <= (uint)_sent.Count && confirmed > (uint)_confirmed)
                _confirmed = (int)confirmed;
            // An old packet, or one past a gap: nothing new that can be used in order.
            if (first > (uint)_received || first + (uint)count < (uint)_received)
                return true;
            var fresh = inputs[(int)((uint)_received - first)..];
            _received += fresh.Length;
            feed.Receive(fresh, known);
        }
        return true;
    }
}
