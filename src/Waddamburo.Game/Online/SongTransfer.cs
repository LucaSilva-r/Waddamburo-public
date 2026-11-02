using System.Buffers.Binary;
using System.Diagnostics;

namespace Waddamburo.Game.Online;

/// <summary>
/// One large blob (a song) from one player to the other over <see cref="PeerLink"/>, which may lose or reorder packets:
/// it goes in chunks, a window of them at a time, and each is sent again until the other side confirms it. Either side
/// sends at most one blob at a time; a new one replaces whatever was under way.
/// </summary>
/// <remarks>
/// Packets: chunk (type, id u32, index u32, total u32, bytes); confirmation (type, id u32, every chunk below u32 arrived,
/// u64 bitmap of the 64 after it).
/// </remarks>
public sealed class SongTransfer
{
    public const byte ChunkType = 0x04;
    public const byte AckType = 0x05;
    public const int ChunkBytes = 1100;

    /// <summary>The largest blob either side sends or takes.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private const int ChunkHeader = 13;
    private const int AckLength = 17;
    private const int Window = 512;
    private const int BurstPerPump = 96;

    private readonly object _lock = new();

    /// <summary>How long an unconfirmed chunk waits before it is sent again.</summary>
    public TimeSpan Resend { get; init; } = TimeSpan.FromMilliseconds(400);

    // Sending.
    private byte[]? _outgoing;
    private uint _outgoingId;
    private int _outgoingTotal;
    private int _confirmedBelow;
    private bool[] _confirmed = [];
    private long[] _sentAt = [];
    private int _nextFresh;

    // Receiving.
    private uint _incomingId;
    private byte[]? _incoming;
    private int _incomingTotal;
    private bool[] _arrived = [];
    private int _arrivedCount;
    private int _arrivedBelow;
    private byte[]? _complete;

    /// <summary>How much of the blob being sent the other side has confirmed, 0 to 1; null when nothing is being sent.</summary>
    public double? SendProgress
    {
        get
        {
            lock (_lock)
                return _outgoing is null ? null : _confirmed.Count(static done => done) / (double)_outgoingTotal;
        }
    }

    /// <summary>How much of the blob coming in has arrived, 0 to 1; null when none is.</summary>
    public double? ReceiveProgress
    {
        get
        {
            lock (_lock)
                return _incoming is null ? null : _arrivedCount / (double)_incomingTotal;
        }
    }

    /// <summary>Starts sending <paramref name="blob"/> (dropping one under way); confirmed in full, the send ends by itself.</summary>
    public void Send(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length == 0 || blob.Length > MaxBytes)
            throw new ArgumentException($"A transfer is 1 byte to {MaxBytes} bytes.", nameof(blob));
        lock (_lock)
        {
            _outgoing = blob;
            _outgoingId++;
            _outgoingTotal = (blob.Length + ChunkBytes - 1) / ChunkBytes;
            _confirmed = new bool[_outgoingTotal];
            _sentAt = new long[_outgoingTotal];
            _confirmedBelow = 0;
            _nextFresh = 0;
        }
    }

    /// <summary>Stops sending, and forgets anything half received.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _outgoing = null;
            _incoming = null;
            _complete = null;
            _arrived = [];
            _arrivedBelow = 0;
            _arrivedCount = 0;
        }
    }

    /// <summary>A blob that has arrived in full, once; null otherwise.</summary>
    public byte[]? TakeComplete()
    {
        lock (_lock)
        {
            var complete = _complete;
            _complete = null;
            return complete;
        }
    }

    /// <summary>The chunks to send now: those not confirmed whose last send is old, then fresh ones within the window.</summary>
    public IReadOnlyList<byte[]> Due()
    {
        List<byte[]> due = [];
        lock (_lock)
        {
            if (_outgoing is not { } blob)
                return due;
            var now = Stopwatch.GetTimestamp();
            var end = Math.Min(_outgoingTotal, _confirmedBelow + Window);
            for (var index = _confirmedBelow; index < Math.Min(end, _nextFresh) && due.Count < BurstPerPump; index++)
            {
                if (_confirmed[index] || Stopwatch.GetElapsedTime(_sentAt[index], now) < Resend)
                    continue;
                due.Add(chunk(blob, index));
                _sentAt[index] = now;
            }
            while (_nextFresh < end && due.Count < BurstPerPump)
            {
                due.Add(chunk(blob, _nextFresh));
                _sentAt[_nextFresh++] = now;
            }
        }
        return due;
    }

    /// <summary>
    /// Reads a packet from the other player: true when it was one of these. A chunk is answered with
    /// <paramref name="reply"/>, the confirmation to send back.
    /// </summary>
    public bool Read(ReadOnlySpan<byte> packet, out byte[]? reply)
    {
        reply = null;
        if (packet.Length == AckLength && packet[0] == AckType)
        {
            confirm(BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]), BinaryPrimitives.ReadInt32LittleEndian(packet[5..]),
                BinaryPrimitives.ReadUInt64LittleEndian(packet[9..]));
            return true;
        }
        if (packet.Length <= ChunkHeader || packet.Length > ChunkHeader + ChunkBytes || packet[0] != ChunkType)
            return false;
        var id = BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]);
        var index = BinaryPrimitives.ReadInt32LittleEndian(packet[5..]);
        var total = BinaryPrimitives.ReadInt32LittleEndian(packet[9..]);
        var bytes = packet[ChunkHeader..];
        if (total <= 0 || total > (MaxBytes + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total
            || index < total - 1 && bytes.Length != ChunkBytes)
            return true;
        lock (_lock)
        {
            if (id != _incomingId)
            {
                // A new blob: whatever came before is dropped.
                _incomingId = id;
                _incomingTotal = total;
                _incoming = new byte[total * ChunkBytes];
                _arrived = new bool[total];
                _arrivedCount = 0;
                _arrivedBelow = 0;
            }
            if (_incoming is { } incoming && total == _incomingTotal && !_arrived[index])
            {
                bytes.CopyTo(incoming.AsSpan(index * ChunkBytes));
                if (index == total - 1)
                    _lastChunkLength = bytes.Length;
                _arrived[index] = true;
                _arrivedCount++;
                while (_arrivedBelow < total && _arrived[_arrivedBelow])
                    _arrivedBelow++;
                if (_arrivedCount == total)
                {
                    _complete = incoming[..((total - 1) * ChunkBytes + _lastChunkLength)];
                    _incoming = null;
                }
            }
            // Arrived in full (or dropped): the confirmation still stops the other side resending.
            reply = ack();
        }
        return true;
    }

    private int _lastChunkLength;

    private byte[] ack()
    {
        var reply = new byte[AckLength];
        reply[0] = AckType;
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1), _incomingId);
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(5), _arrivedBelow);
        ulong bits = 0;
        for (var bit = 0; bit < 64 && _arrivedBelow + bit < _arrived.Length; bit++)
            if (_arrived[_arrivedBelow + bit])
                bits |= 1UL << bit;
        BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(9), bits);
        return reply;
    }

    private void confirm(uint id, int below, ulong bits)
    {
        lock (_lock)
        {
            if (_outgoing is null || id != _outgoingId || below < 0 || below > _outgoingTotal)
                return;
            for (var index = _confirmedBelow; index < below; index++)
                _confirmed[index] = true;
            for (var bit = 0; bit < 64 && below + bit < _outgoingTotal; bit++)
                if ((bits & (1UL << bit)) != 0)
                    _confirmed[below + bit] = true;
            while (_confirmedBelow < _outgoingTotal && _confirmed[_confirmedBelow])
                _confirmedBelow++;
            if (_confirmedBelow == _outgoingTotal)
                _outgoing = null;
        }
    }

    private byte[] chunk(byte[] blob, int index)
    {
        var start = index * ChunkBytes;
        var length = Math.Min(ChunkBytes, blob.Length - start);
        var packet = new byte[ChunkHeader + length];
        packet[0] = ChunkType;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), _outgoingId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5), index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(9), _outgoingTotal);
        blob.AsSpan(start, length).CopyTo(packet.AsSpan(ChunkHeader));
        return packet;
    }
}
