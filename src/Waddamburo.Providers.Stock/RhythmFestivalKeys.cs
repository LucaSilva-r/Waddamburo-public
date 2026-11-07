using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// Rhythm Festival's (Steam) file cipher, its secrets read from the user's own installation, never shipped. Every
/// table, chart and song file is AES-256-CBC with PKCS#7, maybe gzip inside; its key and IV are PBKDF2-HMAC-SHA256 of
/// <c>password + name</c> with salt <c>salt + name</c>, where <c>name</c> is the file name without extension (32 bytes
/// at one iteration count for the key, 16 at another for the IV). The password and salt are Base64 string literals in
/// <c>global-metadata.dat</c>; the iteration counts are two adjacent immediate stores in <c>GameAssembly.dll</c>, kept
/// XORed with 0xFFFFFF and 0xFFFFF. Candidates are confirmed by opening <c>ReadAssets/musicinfo.bin</c>.
/// </summary>
public sealed class RhythmFestivalKeys
{
    private const int MaximumFileBytes = 64 * 1024 * 1024;

    private readonly byte[] _password;
    private readonly byte[] _salt;
    private readonly int _keyIterations;
    private readonly int _ivIterations;

    private RhythmFestivalKeys(string password, string salt, int keyIterations, int ivIterations)
    {
        _password = Encoding.UTF8.GetBytes(password);
        _salt = Encoding.UTF8.GetBytes(salt);
        _keyIterations = keyIterations;
        _ivIterations = ivIterations;
    }

    /// <summary>
    /// The installation's cipher, confirmed against <paramref name="probe"/> (an encrypted file, normally
    /// <c>musicinfo.bin</c>). Throws <see cref="InvalidDataException"/> when the binaries yield nothing that opens it.
    /// </summary>
    /// <param name="metadata">The installation's <c>il2cpp_data/Metadata/global-metadata.dat</c>.</param>
    /// <param name="assembly">The installation's <c>GameAssembly.dll</c>.</param>
    /// <param name="probe">An encrypted table the parameters must open.</param>
    public static RhythmFestivalKeys Find(string metadata, string assembly, string probe)
    {
        var strings = Base64Literals(File.ReadAllBytes(metadata));
        var counts = IterationCounts(File.ReadAllBytes(assembly));
        var data = File.ReadAllBytes(probe);
        var name = Path.GetFileNameWithoutExtension(probe);
        foreach (var (keyIterations, ivIterations) in counts)
            foreach (var password in strings)
                foreach (var salt in strings)
                {
                    if (ReferenceEquals(password, salt))
                        continue;
                    var keys = new RhythmFestivalKeys(password, salt, keyIterations, ivIterations);
                    if (keys.tryDecrypt(data, name, out var plain)
                        && ((ReadOnlySpan<byte>)plain).TrimStart((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF, 0x20, 0x0D, 0x0A, 0x09]) is [(byte)'{', ..])
                        return keys;
                }
        throw new InvalidDataException(strings.Count == 0 || counts.Count == 0
            ? "The installation's GameAssembly.dll and global-metadata.dat do not hold the file cipher's parameters."
            : "No parameters in the installation's binaries open its song table.");
    }

    /// <summary>Decrypts one file; <paramref name="name"/> is its file name (the extension is ignored).</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data, string name) =>
        tryDecrypt(data, Path.GetFileNameWithoutExtension(name), out var plain)
            ? plain : throw new InvalidDataException($"{name} does not decrypt.");

    private bool tryDecrypt(ReadOnlySpan<byte> data, string name, out byte[] plain)
    {
        plain = [];
        if (data.Length == 0 || data.Length % 16 != 0 || data.Length > MaximumFileBytes)
            return false;
        var suffix = Encoding.UTF8.GetBytes(name);
        byte[] password = [.. _password, .. suffix], salt = [.. _salt, .. suffix];
        using var aes = Aes.Create();
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(password, salt, _keyIterations, HashAlgorithmName.SHA256, 32);
        var iv = Rfc2898DeriveBytes.Pbkdf2(password, salt, _ivIterations, HashAlgorithmName.SHA256, 16);
        try
        {
            plain = aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return false;
        }
        if (plain is [0x1f, 0x8b, ..])
            plain = inflate(plain);
        return true;
    }

    private static byte[] inflate(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaximumFileBytes)
                throw new InvalidDataException("A decompressed Rhythm Festival file exceeds 64 MiB.");
        }
        return output.ToArray();
    }

    // il2cpp metadata (version 24 and later): string literal table at header +8/+12, its data at +16/+20; each
    // entry is (byte length, data index). Candidates: Base64 literals decoding to 16-64 printable ASCII characters.
    internal static List<string> Base64Literals(byte[] metadata)
    {
        var span = metadata.AsSpan();
        if (span.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(span) != 0xFAB11BAF)
            throw new InvalidDataException("global-metadata.dat is not il2cpp metadata.");
        var tableOffset = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var tableSize = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
        if (tableOffset < 0 || tableSize < 0 || (long)tableOffset + tableSize > span.Length
            || dataOffset < 0 || dataSize < 0 || (long)dataOffset + dataSize > span.Length)
            throw new InvalidDataException("global-metadata.dat has no readable string literal table.");
        var found = new List<string>();
        var decoded = new byte[64];
        for (var entry = tableOffset; entry + 8 <= tableOffset + tableSize; entry += 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[entry..]);
            var index = BinaryPrimitives.ReadInt32LittleEndian(span[(entry + 4)..]);
            if (length is < 24 or > 88 || length % 4 != 0 || index < 0 || (long)index + length > dataSize)
                continue;
            var text = Encoding.ASCII.GetString(span.Slice(dataOffset + index, length));
            if (Convert.TryFromBase64String(text, decoded, out var written) && written is >= 16 and <= 64
                && decoded.AsSpan(0, written).IndexOfAnyExceptInRange((byte)0x21, (byte)0x7e) < 0)
                found.Add(Encoding.ASCII.GetString(decoded, 0, written));
        }
        return found;
    }

    // mov dword [rcx+0x80], imm32 followed within 40 bytes by mov dword [rcx+0x84], imm32: the two counts' stores in
    // the class constructor that holds them, unmasked.
    internal static List<(int Key, int Iv)> IterationCounts(byte[] assembly)
    {
        ReadOnlySpan<byte> first = [0xC7, 0x81, 0x80, 0x00, 0x00, 0x00];
        ReadOnlySpan<byte> second = [0xC7, 0x81, 0x84, 0x00, 0x00, 0x00];
        var found = new List<(int, int)>();
        var span = assembly.AsSpan();
        for (var at = span.IndexOf(first); at >= 0 && at + 10 <= span.Length;)
        {
            var key = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 6)..]) ^ 0xFFFFFF;
            var window = span.Slice(at + 10, Math.Min(40, span.Length - at - 10));
            if (window.IndexOf(second) is var next and >= 0 && next + 10 <= window.Length)
            {
                var iv = BinaryPrimitives.ReadInt32LittleEndian(window[(next + 6)..]) ^ 0xFFFFF;
                if (key is > 0 and < 1_000_000 && iv is > 0 and < 1_000_000 && !found.Contains((key, iv)))
                    found.Add((key, iv));
            }
            var rest = span[(at + 1)..].IndexOf(first);
            at = rest < 0 ? -1 : at + 1 + rest;
        }
        return found;
    }
}
