using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Waddamburo.Formats.Audio;

/// <summary>Reads the numeric bank-name table used by nuSound2 titles.</summary>
public sealed class NuSoundBankCatalog
{
    private const int HeaderSize = 16;
    private const int EntrySize = 48;
    private const int NameOffsetInEntry = 16;
    private const int NameCapacity = 32;

    private NuSoundBankCatalog(ImmutableArray<string> banks) => Banks = banks;

    public ImmutableArray<string> Banks { get; }

    public static NuSoundBankCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllBytes(path));
    }

    public static NuSoundBankCatalog Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("The nuSound2 bank table is truncated.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var firstNameOffset = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        if (count > 65_536 || declaredSize != data.Length || firstNameOffset < HeaderSize)
            throw new InvalidDataException("The nuSound2 bank table header is invalid.");
        if (count != 0 && firstNameOffset + ((ulong)count - 1U) * EntrySize + NameCapacity > (ulong)data.Length)
            throw new InvalidDataException("The nuSound2 bank table entries are truncated.");

        var banks = ImmutableArray.CreateBuilder<string>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var offset = checked((int)(firstNameOffset + (ulong)index * EntrySize));
            var field = data.Slice(offset, NameCapacity);
            var terminator = field.IndexOf((byte)0);
            if (terminator < 1)
                throw new InvalidDataException($"nuSound2 bank {index} has no name.");
            var name = Encoding.ASCII.GetString(field[..terminator]);
            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new InvalidDataException($"nuSound2 bank {index} has an invalid name.");
            banks.Add(name);
        }
        return new NuSoundBankCatalog(banks.MoveToImmutable());
    }

    public bool TryGetName(int bankId, out string name)
    {
        if ((uint)bankId < (uint)Banks.Length)
        {
            name = Banks[bankId];
            return true;
        }
        name = string.Empty;
        return false;
    }
}
