using System.Collections.Immutable;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Don;

/// <summary>One fully baked, big-endian Don animation sampled at 60 Hz.</summary>
public sealed class DonAnimationFile
{
    private const int HeaderLength = sizeof(float);

    private DonAnimationFile(int frameCount, int valuesPerFrame, ImmutableArray<float> values)
    {
        FrameCount = frameCount;
        ValuesPerFrame = valuesPerFrame;
        Values = values;
    }

    public int FrameCount { get; }

    public int ValuesPerFrame { get; }

    public ImmutableArray<float> Values { get; }

    public ReadOnlySpan<float> GetFrame(int frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        return Values.AsSpan(frame * ValuesPerFrame, ValuesPerFrame);
    }

    public static DonAnimationFile Parse(ReadOnlyMemory<byte> data, ParserLimits? limits = null)
    {
        var parserLimits = limits ?? ParserLimits.Default;
        using var reader = new BoundedBinaryReader(data, ByteOrder.BigEndian, parserLimits);
        var encodedFrameCount = reader.ReadSingle();
        if (!float.IsFinite(encodedFrameCount)
            || encodedFrameCount < 1
            || encodedFrameCount != MathF.Truncate(encodedFrameCount))
        {
            throw new FormatReadException("Don animation frame count must be a positive finite integer", 0);
        }
        if (encodedFrameCount > parserLimits.MaxFrames)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxFrames),
                (long)encodedFrameCount,
                parserLimits.MaxFrames,
                0);
        }

        var frameCount = checked((int)encodedFrameCount);
        var payloadBytes = data.Length - HeaderLength;
        if ((payloadBytes & 3) != 0)
            throw new FormatReadException("Don animation payload is not a whole number of floats", HeaderLength);
        var valueCount = payloadBytes / sizeof(float);
        if (valueCount == 0 || valueCount % frameCount != 0)
            throw new FormatReadException("Don animation frames do not have a uniform stride", HeaderLength);
        if (payloadBytes > parserLimits.MaxAllocationBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxAllocationBytes),
                payloadBytes,
                parserLimits.MaxAllocationBytes,
                HeaderLength);
        }

        var values = ImmutableArray.CreateBuilder<float>(valueCount);
        for (var index = 0; index < valueCount; index++)
        {
            var value = reader.ReadSingle();
            if (!float.IsFinite(value))
                throw new FormatReadException("Don animation contains a non-finite value", reader.Offset - sizeof(float));
            values.Add(value);
        }
        return new DonAnimationFile(frameCount, valueCount / frameCount, values.MoveToImmutable());
    }
}
