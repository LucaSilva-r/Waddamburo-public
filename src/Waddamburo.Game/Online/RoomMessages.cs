using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace Waddamburo.Game.Online;

/// <summary>
/// The players' few messages to each other (a song picked, ready, go) over <see cref="PeerLink"/>, which may lose or
/// reorder them: each is sent again until the other side confirms it, and they arrive once each, in order.
/// </summary>
/// <remarks>Packets: message (type, seq u32, UTF-8 JSON); confirmation (type, every seq up to u32 has arrived).</remarks>
public sealed class RoomMessages
{
    public const byte MessageType = 0x02;
    public const byte AckType = 0x03;
    private const int MaxMessageBytes = 1024;
    private static readonly TimeSpan Resend = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private readonly List<(uint Seq, byte[] Packet, long SentAt)> _unconfirmed = [];
    private readonly Dictionary<uint, JsonElement> _early = [];
    private readonly Queue<JsonElement> _arrived = new();
    private uint _nextSeq = 1;
    private uint _delivered;

    /// <summary>Queues a message: an object whose "kind" says what it is. Sent by <see cref="Due"/>.</summary>
    public void Send(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var output = new MemoryStream();
        output.WriteByte(MessageType);
        output.Write(new byte[4]);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        var packet = output.ToArray();
        if (packet.Length - 5 > MaxMessageBytes)
            throw new ArgumentException("A room message is at most 1 kB.", nameof(write));
        lock (_lock)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), _nextSeq);
            _unconfirmed.Add((_nextSeq++, packet, 0));
        }
    }

    /// <summary>The packets to send now: unconfirmed messages not sent in the last quarter second.</summary>
    public IReadOnlyList<byte[]> Due()
    {
        var now = Stopwatch.GetTimestamp();
        List<byte[]> due = [];
        lock (_lock)
        {
            for (var index = 0; index < _unconfirmed.Count; index++)
            {
                var (seq, packet, sentAt) = _unconfirmed[index];
                if (sentAt != 0 && Stopwatch.GetElapsedTime(sentAt, now) < Resend)
                    continue;
                due.Add(packet);
                _unconfirmed[index] = (seq, packet, now);
            }
        }
        return due;
    }

    /// <summary>
    /// Reads a packet from the other player: true when it was one of these. A message is answered with
    /// <paramref name="reply"/>, the confirmation to send back.
    /// </summary>
    public bool Read(ReadOnlySpan<byte> packet, out byte[]? reply)
    {
        reply = null;
        if (packet.Length == 5 && packet[0] == AckType)
        {
            var upTo = BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]);
            lock (_lock)
                _unconfirmed.RemoveAll(entry => entry.Seq <= upTo);
            return true;
        }
        if (packet.Length < 6 || packet.Length - 5 > MaxMessageBytes || packet[0] != MessageType)
            return false;
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]);
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(packet[5..].ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }
        lock (_lock)
        {
            if (seq > _delivered && seq <= _delivered + 64)
                _early[seq] = message;
            while (_early.Remove(_delivered + 1, out var next))
            {
                _arrived.Enqueue(next);
                _delivered++;
            }
            reply = new byte[5];
            reply[0] = AckType;
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1), _delivered);
        }
        return true;
    }

    /// <summary>The next message from the other player, in the order they sent them.</summary>
    public bool TryTake(out JsonElement message)
    {
        lock (_lock)
            return _arrived.TryDequeue(out message);
    }

    /// <summary>A message's kind; empty when it has none.</summary>
    public static string KindOf(JsonElement message) =>
        message.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : "";
}
