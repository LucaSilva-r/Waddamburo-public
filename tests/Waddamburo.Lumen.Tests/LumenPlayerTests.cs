using System.Buffers.Binary;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenPlayerTests
{
    [Fact]
    public void FramePlacementsProduceImmutableTransformedRenderSnapshots()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        var first = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(4U, first.TextureIndex);
        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), first.TopLeft);
        Assert.Equal(new LumenRenderVertex(110, 70, 1, 1), first.BottomRight);
        Assert.Equal(new LumenRenderColor(0.5f, 1, 1, 0.5f), first.MultiplyColor);

        player.Advance();

        var second = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), second.TopLeft);
        Assert.Equal(new LumenRenderVertex(130, 90, 1, 1), second.BottomRight);
        Assert.Equal(first.MultiplyColor, second.MultiplyColor);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");

        player.Advance();
        Assert.Empty(player.CreateRenderSnapshot().Quads);

        player.Advance();
        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void MatrixCompositionAppliesChildBeforeParent()
    {
        var local = new LumenMatrix(2, 0, 0, 3, 5, 7);
        var parent = new LumenMatrix(0, 1, -1, 0, 20, 30);

        var sequential = parent.Transform(local.Transform(4, 6).X, local.Transform(4, 6).Y);
        var composed = local.Then(parent).Transform(4, 6);

        Assert.Equal(sequential, composed);
    }

    [Fact]
    public void RenderSnapshotInterpolatesBetweenPreviousAndCurrentTickState()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        player.Advance();
        var previous = Assert.Single(player.CreateRenderSnapshot(0).Quads);
        var halfway = Assert.Single(player.CreateRenderSnapshot(0.5f).Quads);
        var current = Assert.Single(player.CreateRenderSnapshot(1).Quads);

        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), previous.TopLeft);
        Assert.Equal(new LumenRenderVertex(20, 30, 0, 0), halfway.TopLeft);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), current.TopLeft);
    }

    [Fact]
    public void RenderSnapshotTreatsLargeTranslationAsCut()
    {
        var player = new LumenPlayer(createMovie(secondX: 250), 1280, 720);

        player.Advance();
        var atStartOfPresentationInterval = Assert.Single(player.CreateRenderSnapshot(0).Quads);

        Assert.Equal(new LumenRenderVertex(250, 40, 0, 0), atStartOfPresentationInterval.TopLeft);
    }

    [Fact]
    public void RenderSnapshotTreatsAbruptMultiplyColorChangeAsCut()
    {
        var player = new LumenPlayer(createMovie(secondColorIndex: 1), 1280, 720);

        player.Advance();
        var atStartOfPresentationInterval = Assert.Single(player.CreateRenderSnapshot(0).Quads);

        Assert.Equal(LumenRenderColor.Transparent, atStartOfPresentationInterval.MultiplyColor);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void RenderSnapshotRejectsInvalidInterpolationFraction(float fraction)
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        Assert.Throws<ArgumentOutOfRangeException>(() => player.CreateRenderSnapshot(fraction));
    }

    [Fact]
    public void SceneComposesChildTransformsTextureNamespacesAndLayerOrder()
    {
        var first = new LumenPlayer(createMovie(), 1280, 720);
        var second = new LumenPlayer(createMovie(), 1280, 720);
        var scene = new LumenScenePlayer(
            1280,
            720,
            [
                new LumenSceneLayer(first, LumenMatrix.Identity, 0, 5),
                new LumenSceneLayer(second, new LumenMatrix(2, 0, 0, 2, 200, 100), 5, 5),
            ]);

        var snapshot = scene.CreateRenderSnapshot();

        Assert.Equal([4u, 9u], snapshot.Quads.Select(quad => quad.TextureIndex));
        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), snapshot.Quads[0].TopLeft);
        Assert.Equal(new LumenRenderVertex(220, 140, 0, 0), snapshot.Quads[1].TopLeft);
        Assert.Equal(new LumenRenderVertex(420, 240, 1, 1), snapshot.Quads[1].BottomRight);

        scene.Advance();
        Assert.Equal(1, first.CurrentFrame);
        Assert.Equal(1, second.CurrentFrame);
    }

    [Fact]
    public void EmptySceneStillRejectsInvalidInterpolationFraction()
    {
        var scene = new LumenScenePlayer(1280, 720, []);

        Assert.Throws<ArgumentOutOfRangeException>(() => scene.CreateRenderSnapshot(float.PositiveInfinity));
    }

    private static LmbMovieDefinition createMovie(float secondX = 30, uint secondColorIndex = uint.MaxValue)
    {
        var geometry = new uint[]
        {
            bits(0), bits(0), bits(0), bits(0),
            bits(100), bits(0), bits(1), bits(0),
            bits(100), bits(50), bits(1), bits(1),
            bits(0), bits(50), bits(0), bits(1),
            4, 0x00410000,
        };
        var file = createLmb(
            words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, bits(60)),
            words(LmbTags.ColorTransformPool, 2, 0x00800100, 0x01000080, 0, 0),
            words(LmbTags.MatrixPool, 1, bits(1), bits(0), bits(0), bits(1), bits(secondX), bits(40)),
            words(LmbTags.TranslationPool, 1, bits(10), bits(20)),
            record(LmbTags.ActionPool, actionPool([0])),
            words(LmbTags.DefineShape, 42, 0, 0, 1),
            words(LmbTags.ShapeGeometry, geometry),
            words(LmbTags.DefineSprite, 7, 0, 0, 0, 3, 0, 0),
            words(LmbTags.ShowFrame, 0, 1),
            words(LmbTags.PlaceObject, 42, 1, 0, uint.MaxValue, 0x00010000, 0x00030000, 0, 0x80000000, 0, uint.MaxValue, 0, 0),
            words(LmbTags.ShowFrame, 1, 2),
            words(LmbTags.PlaceObject, 42, 1, 0, uint.MaxValue, 0x00020000, 0x00030000, 0, 0, secondColorIndex, uint.MaxValue, 0, 0),
            words(LmbTags.DoAction, 0, 0),
            words(LmbTags.ShowFrame, 2, 1),
            words(LmbTags.RemoveObject, 42, 0x00020000));
        return LmbSemanticReader.Read(file, validationContext: new LmbSemanticValidationContext(textureCount: 5)).Value;
    }

    private static LmbFile createLmb(params (uint Tag, byte[] Payload)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write("LMB\0"u8);
        stream.Write(new byte[LmbFile.HeaderLength - 4]);
        foreach (var (tag, payload) in records)
        {
            writeUInt32(stream, tag);
            writeUInt32(stream, checked((uint)payload.Length / 4));
            stream.Write(payload);
        }
        return LmbFile.Parse(stream.ToArray());
    }

    private static (uint Tag, byte[] Payload) record(uint tag, byte[] payload) => (tag, payload);

    private static (uint Tag, byte[] Payload) words(uint tag, params uint[] values)
    {
        var payload = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(index * 4, 4), values[index]);
        return (tag, payload);
    }

    private static byte[] actionPool(params byte[][] values)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            writeUInt32(stream, checked((uint)value.Length));
            stream.Write(value);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    private static uint bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
