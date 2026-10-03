using System.Collections.Immutable;
using System.Numerics;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Don;

public readonly record struct DonBoneChannel(int ValueOffset, int ValueCount, int ParentIndex);

/// <summary>
/// Asset-described Don palette layout and row-vector pose evaluation. The inverse bind pose is
/// kept separate so callers can reuse it for every frame.
/// </summary>
public sealed class DonSkeleton
{
    public const int CharacterValuesPerFrame = 294;

    /// <summary>Katsudon and 2011: each limb stops after its end bone (54 values, not 69).</summary>
    public const int ShortLimbCharacterValuesPerFrame = 234;
    public const int AccessoryValuesPerFrame = 15;

    private DonSkeleton(ImmutableArray<DonBoneChannel> channels, int? expressionOffset)
    {
        Channels = channels;
        ExpressionOffset = expressionOffset;
    }

    public ImmutableArray<DonBoneChannel> Channels { get; }

    public int? ExpressionOffset { get; }

    public static DonSkeleton ForAnimation(DonAnimationFile animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        return animation.ValuesPerFrame switch
        {
            CharacterValuesPerFrame => createCharacter(limbValues: 69),
            ShortLimbCharacterValuesPerFrame => createCharacter(limbValues: 54),
            AccessoryValuesPerFrame => new DonSkeleton(
                [new(0, 6, -1), new(6, 9, 0)],
                expressionOffset: null),
            _ => throw new FormatReadException(
                $"Unsupported Don animation stride {animation.ValuesPerFrame}",
                sizeof(float)),
        };
    }

    public ImmutableArray<Matrix4x4> EvaluateWorld(ReadOnlySpan<float> frame)
    {
        var required = Channels
            .Where(static channel => channel.ValueOffset >= 0)
            .Max(static channel => channel.ValueOffset + channel.ValueCount);
        if (frame.Length < required)
            throw new ArgumentException($"Pose requires at least {required} values.", nameof(frame));

        var result = ImmutableArray.CreateBuilder<Matrix4x4>(Channels.Length);
        foreach (var channel in Channels)
        {
            var local = localTransform(frame, channel);
            result.Add(channel.ParentIndex < 0 ? local : local * result[channel.ParentIndex]);
        }
        return result.MoveToImmutable();
    }

    public ImmutableArray<Matrix4x4> CreateSkinningPalette(
        ReadOnlySpan<float> bindFrame,
        ReadOnlySpan<float> animatedFrame)
    {
        var bindWorld = EvaluateWorld(bindFrame);
        var animatedWorld = EvaluateWorld(animatedFrame);
        var result = ImmutableArray.CreateBuilder<Matrix4x4>(Channels.Length);
        for (var index = 0; index < Channels.Length; index++)
        {
            if (!Matrix4x4.Invert(bindWorld[index], out var inverseBind))
                throw new InvalidDataException($"Don bind transform {index} is not invertible.");
            result.Add(inverseBind * animatedWorld[index]);
        }
        return result.MoveToImmutable();
    }

    public int GetExpression(ReadOnlySpan<float> frame)
    {
        if (ExpressionOffset is not int offset)
            return 0;
        if ((uint)offset >= (uint)frame.Length)
            throw new ArgumentException("Pose does not contain its expression value.", nameof(frame));
        return Math.Clamp((int)MathF.Round(frame[offset]), 0, 11);
    }

    public static int ExpandCompactPaletteIndex(int index, int modelBoneCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return modelBoneCount is 25 or 26 && index >= 2
            ? index == 26
                ? 38
                : 2 + 9 * ((index - 2) / 6) + (index - 2) % 6
            : index;
    }

    // Root, body, then per limb a base, four segments and an end bone; the 69-value layout adds two
    // 6-value channels and a 3-value one (left at identity in the 54-value one, which lacks them). The
    // expression follows the limbs.
    private static DonSkeleton createCharacter(int limbValues)
    {
        var channels = ImmutableArray.CreateBuilder<DonBoneChannel>(39);
        channels.Add(new(0, 6, -1));
        channels.Add(new(6, 9, 0));
        for (var limb = 0; limb < 4; limb++)
        {
            var valueOffset = 15 + limbValues * limb;
            var boneOffset = channels.Count;
            channels.Add(new(valueOffset, 9, 0));
            for (var segment = 1; segment <= 4; segment++)
                channels.Add(new(valueOffset + 9 * segment, 9, boneOffset + segment - 1));
            channels.Add(new(valueOffset + 45, 9, boneOffset + 4));
            var extras = limbValues == 69;
            channels.Add(new(extras ? valueOffset + 54 : -1, extras ? 6 : 0, boneOffset + 4));
            channels.Add(new(extras ? valueOffset + 60 : -1, extras ? 6 : 0, 0));
            channels.Add(new(extras ? valueOffset + 66 : -1, extras ? 3 : 0, boneOffset + 7));
        }
        channels.Add(new(-1, 0, 1));
        return new DonSkeleton(channels.MoveToImmutable(), 15 + 4 * limbValues);
    }

    private static Matrix4x4 localTransform(ReadOnlySpan<float> frame, DonBoneChannel channel)
    {
        if (channel.ValueOffset < 0)
            return Matrix4x4.Identity;
        var offset = channel.ValueOffset;
        var translation = Matrix4x4.CreateTranslation(frame[offset], frame[offset + 1], frame[offset + 2]);
        if (channel.ValueCount == 3)
            return translation;
        var rotation = Matrix4x4.CreateRotationX(frame[offset + 3])
            * Matrix4x4.CreateRotationY(frame[offset + 4])
            * Matrix4x4.CreateRotationZ(frame[offset + 5]);
        var scale = channel.ValueCount == 9
            ? Matrix4x4.CreateScale(frame[offset + 6], frame[offset + 7], frame[offset + 8])
            : Matrix4x4.Identity;
        return scale * rotation * translation;
    }
}
