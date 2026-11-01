using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Game.Online;

/// <summary>The parts of STUN (RFC 8489) and TURN (RFC 8656) that <see cref="PeerLink"/> speaks, IPv4 only.</summary>
public sealed class StunMessage
{
    public const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101;
    public const ushort AllocateRequest = 0x0003, AllocateSuccess = 0x0103, AllocateError = 0x0113;
    public const ushort RefreshRequest = 0x0004, RefreshSuccess = 0x0104, RefreshError = 0x0114;
    public const ushort CreatePermissionRequest = 0x0008, CreatePermissionSuccess = 0x0108, CreatePermissionError = 0x0118;
    public const ushort SendIndication = 0x0016, DataIndication = 0x0017;

    public const ushort Username = 0x0006, MessageIntegrity = 0x0008, ErrorCode = 0x0009, Lifetime = 0x000D, XorPeerAddress = 0x0012,
        Data = 0x0013, Realm = 0x0014, Nonce = 0x0015, XorRelayedAddress = 0x0016, RequestedTransport = 0x0019, XorMappedAddress = 0x0020;

    private const uint Cookie = 0x2112A442;
    private const int HeaderLength = 20;

    public StunMessage(ushort type, byte[]? transactionId = null)
    {
        Type = type;
        TransactionId = transactionId ?? RandomNumberGenerator.GetBytes(12);
    }

    public ushort Type { get; }

    public byte[] TransactionId { get; }

    public List<(ushort Type, byte[] Value)> Attributes { get; } = [];

    public int? Error => Find(ErrorCode) is { Length: >= 4 } value ? value[2] * 100 + value[3] : null;

    public byte[]? Find(ushort type)
    {
        foreach (var attribute in Attributes)
            if (attribute.Type == type)
                return attribute.Value;
        return null;
    }

    public string? Text(ushort type) => Find(type) is { } value ? Encoding.UTF8.GetString(value) : null;

    public StunMessage With(ushort type, byte[] value)
    {
        Attributes.Add((type, value));
        return this;
    }

    public StunMessage With(ushort type, string value) => With(type, Encoding.UTF8.GetBytes(value));

    public StunMessage WithAddress(ushort type, IPEndPoint endPoint) => With(type, xorAddress(endPoint));

    public IPEndPoint? Address(ushort type)
    {
        if (Find(type) is not { Length: 8 } value || value[1] != 1)
            return null;
        var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2)) ^ (Cookie >> 16));
        var address = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(4)) ^ Cookie;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPEndPoint(new IPAddress(bytes), port);
    }

    /// <summary>The wire form; with a <paramref name="key"/>, ending in a MESSAGE-INTEGRITY made with it.</summary>
    public byte[] Encode(byte[]? key = null)
    {
        var length = Attributes.Sum(static attribute => 4 + padded(attribute.Value.Length));
        var output = new byte[HeaderLength + length + (key is null ? 0 : 24)];
        BinaryPrimitives.WriteUInt16BigEndian(output, Type);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)(output.Length - HeaderLength));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), Cookie);
        TransactionId.CopyTo(output, 8);
        var offset = HeaderLength;
        foreach (var (type, value) in Attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset), type);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset + 2), (ushort)value.Length);
            value.CopyTo(output, offset + 4);
            offset += 4 + padded(value.Length);
        }
        if (key is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset), MessageIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset + 2), 20);
#pragma warning disable CA5350 // TURN's long-term credential mechanism is defined on HMAC-SHA1.
            HMACSHA1.HashData(key, output.AsSpan(0, offset)).CopyTo(output, offset + 4);
#pragma warning restore CA5350
        }
        return output;
    }

    /// <summary>A datagram read as STUN; null when it isn't one or doesn't add up (anyone can send us packets).</summary>
    public static StunMessage? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength || (data[0] & 0xC0) != 0 || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != Cookie)
            return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (length + HeaderLength != data.Length)
            return null;
        var message = new StunMessage(BinaryPrimitives.ReadUInt16BigEndian(data), data.Slice(8, 12).ToArray());
        for (var offset = HeaderLength; offset < data.Length;)
        {
            if (offset + 4 > data.Length)
                return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (offset + 4 + size > data.Length)
                return null;
            message.Attributes.Add((type, data.Slice(offset + 4, size).ToArray()));
            offset += 4 + padded(size);
        }
        return message;
    }

    /// <summary>The long-term credential key: MD5 of username:realm:password.</summary>
    public static byte[] LongTermKey(string username, string realm, string password)
    {
#pragma warning disable CA5351 // TURN's long-term credential mechanism is defined on MD5.
        return MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
#pragma warning restore CA5351
    }

    private static int padded(int length) => (length + 3) & ~3;

    private static byte[] xorAddress(IPEndPoint endPoint)
    {
        if (endPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(endPoint));
        var output = new byte[8];
        output[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)(endPoint.Port ^ (Cookie >> 16)));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(endPoint.Address.GetAddressBytes()) ^ Cookie);
        return output;
    }
}
