using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Waddamburo.Formats.Don;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Nud;

public readonly record struct NudVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TextureCoordinate,
    Vector4 Color,
    Vector4 BoneWeights,
    Vector4 BoneIndices);

public sealed record NudMaterial(
    uint Flags,
    ushort SourceBlend,
    ushort DestinationBlend,
    byte AlphaFunction,
    byte AlphaReference,
    ushort CullMode,
    ImmutableArray<uint> TextureIds)
{
    public byte ShaderKind => (byte)Flags;
}

public sealed record NudPolygon(
    ImmutableArray<NudVertex> Vertices,
    ImmutableArray<ushort> TriangleIndices,
    ImmutableArray<NudMaterial> Materials);

public sealed record NudObject(
    string Name,
    short SingleBindBone,
    ImmutableArray<NudPolygon> Polygons);

/// <summary>Validated immutable NDP3 model data suitable for renderer upload.</summary>
public sealed class NudFile
{
    private const int HeaderLength = 0x30;
    private const int ObjectLength = 0x30;
    private const int PolygonLength = 0x30;
    private const int MaterialHeaderLength = 0x20;
    private const int MaterialTextureLength = 0x18;

    private NudFile(ushort version, ushort boneCount, ImmutableArray<NudObject> objects)
    {
        Version = version;
        BoneCount = boneCount;
        Objects = objects;
    }

    public ushort Version { get; }

    public ushort BoneCount { get; }

    public ImmutableArray<NudObject> Objects { get; }

    public static NudFile Parse(ReadOnlyMemory<byte> data, ParserLimits? limits = null)
    {
        var parserLimits = limits ?? ParserLimits.Default;
        if (data.Length > parserLimits.MaxFileBytes)
            throw new FormatLimitException(nameof(ParserLimits.MaxFileBytes), data.Length, parserLimits.MaxFileBytes, 0);
        var source = data.Span;
        require(source, 0, HeaderLength, "NDP3 header");
        if (!source[..4].SequenceEqual("NDP3"u8))
            throw malformed("Invalid NDP3 magic", 0);

        var declaredSize = u32(source, 4);
        if (declaredSize != 0 && declaredSize > data.Length)
            throw new FormatReadException("NDP3 declared size exceeds the source", 4, declaredSize, data.Length);
        var version = u16(source, 8);
        var objectCount = u16(source, 10);
        var boneCount = u16(source, 14);
        enforceCount(objectCount, parserLimits.MaxRecordCount, nameof(ParserLimits.MaxRecordCount), 10);
        enforceCount(boneCount, parserLimits.MaxBones, nameof(ParserLimits.MaxBones), 14);

        var polygonBase = checkedOffset(0x30, u32(source, 0x10), 0x10, "polygon clump");
        var vertexBase = checkedOffset(polygonBase, u32(source, 0x14), 0x14, "vertex clump");
        var additionalVertexBase = checkedOffset(vertexBase, u32(source, 0x18), 0x18, "additional vertex clump");
        var nameBase = checkedOffset(additionalVertexBase, u32(source, 0x1C), 0x1C, "name table");
        require(source, HeaderLength, checked((long)objectCount * ObjectLength), "NDP3 object table");
        require(source, polygonBase, 0, "NDP3 polygon clump");
        require(source, vertexBase, 0, "NDP3 vertex clump");
        require(source, additionalVertexBase, 0, "NDP3 additional vertex clump");
        require(source, nameBase, 0, "NDP3 name table");

        long totalVertices = 0;
        long totalIndices = 0;
        var objects = ImmutableArray.CreateBuilder<NudObject>(objectCount);
        for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            var offset = HeaderLength + objectIndex * ObjectLength;
            var nameOffset = checkedOffset(nameBase, u32(source, offset + 0x20), offset + 0x20, "object name");
            var name = readName(source, nameOffset, parserLimits);
            var singleBind = i16(source, offset + 0x28);
            var polygonCount = u16(source, offset + 0x2A);
            enforceCount(polygonCount, parserLimits.MaxRecordCount, nameof(ParserLimits.MaxRecordCount), offset + 0x2A);
            var polygonTable = checked((int)u32(source, offset + 0x2C));
            require(source, polygonTable, checked((long)polygonCount * PolygonLength), "NDP3 polygon table");
            var polygons = ImmutableArray.CreateBuilder<NudPolygon>(polygonCount);
            for (var polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
            {
                var polygonOffset = polygonTable + polygonIndex * PolygonLength;
                polygons.Add(parsePolygon(
                    source,
                    polygonOffset,
                    polygonBase,
                    vertexBase,
                    additionalVertexBase,
                    singleBind,
                    boneCount,
                    parserLimits,
                    ref totalVertices,
                    ref totalIndices));
            }
            objects.Add(new NudObject(name, singleBind, polygons.MoveToImmutable()));
        }
        return new NudFile(version, boneCount, objects.MoveToImmutable());
    }

    private static NudPolygon parsePolygon(
        ReadOnlySpan<byte> source,
        int offset,
        int polygonBase,
        int vertexBase,
        int additionalVertexBase,
        short singleBind,
        int boneCount,
        ParserLimits limits,
        ref long totalVertices,
        ref long totalIndices)
    {
        var indexOffset = checkedOffset(polygonBase, u32(source, offset), offset, "index data");
        var vertexOffset = checkedOffset(vertexBase, u32(source, offset + 4), offset + 4, "vertex data");
        var additionalOffset = checkedOffset(additionalVertexBase, u32(source, offset + 8), offset + 8, "additional vertex data");
        var vertexCount = u16(source, offset + 12);
        var vertexType = source[offset + 14];
        var uvType = source[offset + 15];
        var indexCount = u16(source, offset + 0x20);
        totalVertices = checked(totalVertices + vertexCount);
        totalIndices = checked(totalIndices + indexCount);
        enforceCount(totalVertices, limits.MaxVertices, nameof(ParserLimits.MaxVertices), offset + 12);
        enforceCount(totalIndices, limits.MaxIndices, nameof(ParserLimits.MaxIndices), offset + 0x20);

        var skinType = vertexType >> 4;
        var normalType = vertexType & 0x0F;
        var uvCount = uvType >> 4;
        var colorType = uvType & 0x0F;
        if (skinType is not (0 or 1) || normalType is not (1 or 6) || colorType is not (0 or 2))
            throw malformed($"Unsupported NDP3 vertex layout {vertexType:X2}/{uvType:X2}", offset + 14);
        if (uvCount < 1)
            throw malformed("NDP3 polygon has no texture coordinates", offset + 15);

        var normalSize = normalType == 1 ? 16 : 8;
        var textureStride = checked((colorType == 2 ? 4 : 0) + 4 * uvCount);
        var vertexStride = skinType == 1 ? textureStride : checked(12 + normalSize + textureStride);
        require(source, vertexOffset, checked((long)vertexStride * vertexCount), "NDP3 vertices");
        if (skinType == 1)
            require(source, additionalOffset, checked((long)64 * vertexCount), "NDP3 skinned vertices");
        var vertices = ImmutableArray.CreateBuilder<NudVertex>(vertexCount);
        for (var index = 0; index < vertexCount; index++)
        {
            var cursor = vertexOffset + index * vertexStride;
            Vector3 position;
            Vector3 normal;
            Vector4 indices;
            Vector4 weights;
            if (skinType == 0)
            {
                position = vector3(source, cursor);
                normal = normalType == 1
                    ? vector3(source, cursor + 12)
                    : halfVector3(source, cursor + 12);
                cursor += 12 + normalSize;
                indices = new Vector4(Math.Max(singleBind, (short)0), 0, 0, 0);
                weights = new Vector4(1, 0, 0, 0);
            }
            else
            {
                var additional = additionalOffset + index * 64;
                position = vector3(source, additional);
                normal = vector3(source, additional + 16);
                indices = new Vector4(
                    remapBone(u32(source, additional + 32), boneCount, additional + 32),
                    remapBone(u32(source, additional + 36), boneCount, additional + 36),
                    remapBone(u32(source, additional + 40), boneCount, additional + 40),
                    remapBone(u32(source, additional + 44), boneCount, additional + 44));
                weights = new Vector4(
                    f32(source, additional + 48),
                    f32(source, additional + 52),
                    f32(source, additional + 56),
                    f32(source, additional + 60));
            }

            var color = Vector4.One;
            if (colorType == 2)
            {
                color = new Vector4(source[cursor], source[cursor + 1], source[cursor + 2], source[cursor + 3]) / 127f;
                cursor += 4;
            }
            var textureCoordinate = new Vector2(f16(source, cursor), f16(source, cursor + 2));
            vertices.Add(new NudVertex(position, normal, textureCoordinate, color, weights, indices));
        }

        require(source, indexOffset, checked((long)indexCount * sizeof(ushort)), "NDP3 triangle strip");
        var triangles = expandTriangleStrip(source, indexOffset, indexCount, vertexCount, limits);
        var materials = ImmutableArray.CreateBuilder<NudMaterial>(4);
        for (var pass = 0; pass < 4; pass++)
        {
            var materialOffset = checked((int)u32(source, offset + 16 + pass * 4));
            if (materialOffset != 0)
                materials.Add(parseMaterial(source, materialOffset, limits));
        }
        return new NudPolygon(vertices.MoveToImmutable(), triangles, materials.ToImmutable());
    }

    private static NudMaterial parseMaterial(ReadOnlySpan<byte> source, int offset, ParserLimits limits)
    {
        require(source, offset, MaterialHeaderLength, "NDP3 material");
        var textureCount = u16(source, offset + 10);
        enforceCount(textureCount, limits.MaxRecordCount, nameof(ParserLimits.MaxRecordCount), offset + 10);
        require(source, offset + MaterialHeaderLength, checked((long)textureCount * MaterialTextureLength), "NDP3 material textures");
        var textures = ImmutableArray.CreateBuilder<uint>(textureCount);
        for (var index = 0; index < textureCount; index++)
            textures.Add(u32(source, offset + MaterialHeaderLength + index * MaterialTextureLength));
        return new NudMaterial(
            u32(source, offset),
            u16(source, offset + 8),
            u16(source, offset + 12),
            source[offset + 14],
            source[offset + 15],
            u16(source, offset + 18),
            textures.MoveToImmutable());
    }

    private static ImmutableArray<ushort> expandTriangleStrip(
        ReadOnlySpan<byte> source,
        int offset,
        int count,
        int vertexCount,
        ParserLimits limits)
    {
        var triangles = ImmutableArray.CreateBuilder<ushort>();
        ushort first = 0;
        ushort second = 0;
        var runLength = 0;
        for (var index = 0; index < count; index++)
        {
            var current = u16(source, offset + index * 2);
            if (current == ushort.MaxValue)
            {
                runLength = 0;
                continue;
            }
            if (current >= vertexCount)
                throw malformed("NDP3 index exceeds the polygon vertex count", offset + index * 2);
            runLength++;
            if (runLength >= 3 && first != second && second != current && first != current)
            {
                if (triangles.Count > limits.MaxIndices - 3)
                    throw new FormatLimitException(nameof(ParserLimits.MaxIndices), triangles.Count + 3L, limits.MaxIndices, offset);
                if ((runLength & 1) == 1)
                {
                    triangles.Add(first);
                    triangles.Add(second);
                }
                else
                {
                    triangles.Add(second);
                    triangles.Add(first);
                }
                triangles.Add(current);
            }
            first = second;
            second = current;
        }
        return triangles.ToImmutable();
    }

    private static string readName(ReadOnlySpan<byte> source, int offset, ParserLimits limits)
    {
        require(source, offset, 1, "NDP3 object name");
        var remaining = source[offset..];
        var length = remaining.IndexOf((byte)0);
        if (length < 0)
            throw malformed("NDP3 object name is not terminated", offset);
        if (length > limits.MaxStringBytes)
            throw new FormatLimitException(nameof(ParserLimits.MaxStringBytes), length, limits.MaxStringBytes, offset);
        return Encoding.UTF8.GetString(remaining[..length]);
    }

    private static float remapBone(uint value, int boneCount, int offset)
    {
        if (value > int.MaxValue)
            throw malformed("NDP3 bone index is too large", offset);
        return DonSkeleton.ExpandCompactPaletteIndex((int)value, boneCount);
    }

    private static Vector3 vector3(ReadOnlySpan<byte> source, int offset) =>
        new(f32(source, offset), f32(source, offset + 4), f32(source, offset + 8));

    private static Vector3 halfVector3(ReadOnlySpan<byte> source, int offset) =>
        new(f16(source, offset), f16(source, offset + 2), f16(source, offset + 4));

    private static ushort u16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset, 2));

    private static short i16(ReadOnlySpan<byte> source, int offset) => unchecked((short)u16(source, offset));

    private static uint u32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(source.Slice(offset, 4));

    private static float f32(ReadOnlySpan<byte> source, int offset) =>
        BitConverter.Int32BitsToSingle(unchecked((int)u32(source, offset)));

    private static float f16(ReadOnlySpan<byte> source, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(u16(source, offset));

    private static int checkedOffset(int left, uint right, int sourceOffset, string label)
    {
        try
        {
            return checked(left + (int)right);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException($"NDP3 {label} offset overflows", sourceOffset, innerException: exception);
        }
    }

    private static void require(ReadOnlySpan<byte> source, long offset, long length, string label)
    {
        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException($"{label} range overflows", Math.Max(0, offset), innerException: exception);
        }
        if (offset < 0 || length < 0 || end > source.Length)
        {
            var available = offset >= 0 && offset <= source.Length ? source.Length - offset : 0;
            throw new FormatReadException($"{label} is truncated", Math.Max(0, offset), length, available);
        }
    }

    private static void enforceCount(long value, long limit, string limitName, long offset)
    {
        if (value > limit)
            throw new FormatLimitException(limitName, value, limit, offset);
    }

    private static FormatReadException malformed(string message, long offset) => new(message, offset);
}
