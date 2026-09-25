using System.IO.Compression;

namespace Waddamburo.Game.Gameplay;

public readonly record struct TaikoReplayInput(TaikoInputAction Action, TimeSpan Time);

/// <summary>
/// One lane's drum inputs as the judgement session received them. Judgement is deterministic, so
/// feeding them to a new session over the same chart reproduces the play (or rescores it under
/// newer rules).
/// </summary>
public sealed class TaikoReplay
{
    private const byte FormatVersion = 1;
    private readonly List<TaikoReplayInput> _inputs = [];

    public IReadOnlyList<TaikoReplayInput> Inputs => _inputs;

    public void Add(TaikoInputAction action, TimeSpan time) => _inputs.Add(new(action, time));

    /// <summary>Gzipped: version byte, count, then (100 ns tick delta, action) pairs.</summary>
    public byte[] Encode()
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(new GZipStream(output, CompressionLevel.Optimal)))
        {
            writer.Write(FormatVersion);
            writer.Write7BitEncodedInt(_inputs.Count);
            var previous = 0L;
            foreach (var input in _inputs)
            {
                writer.Write7BitEncodedInt64(input.Time.Ticks - previous);
                writer.Write((byte)input.Action);
                previous = input.Time.Ticks;
            }
        }
        return output.ToArray();
    }

    public static TaikoReplay Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var reader = new BinaryReader(new GZipStream(new MemoryStream(data), CompressionMode.Decompress));
        if (reader.ReadByte() != FormatVersion)
            throw new InvalidDataException("Unknown replay format.");
        var replay = new TaikoReplay();
        var ticks = 0L;
        for (var count = reader.Read7BitEncodedInt(); count > 0; count--)
        {
            ticks += reader.Read7BitEncodedInt64();
            replay.Add((TaikoInputAction)reader.ReadByte(), TimeSpan.FromTicks(ticks));
        }
        return replay;
    }

    /// <summary>Feeds the inputs to <paramref name="session"/> and runs it past the chart's end.</summary>
    public void Play(TaikoJudgementSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var input in _inputs)
            session.SubmitInput(input.Action, input.Time);
        session.AdvanceTo(TimeSpan.MaxValue);
    }
}
