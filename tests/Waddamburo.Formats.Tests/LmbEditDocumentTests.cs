using System.Buffers.Binary;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Tests;

public sealed class LmbEditDocumentTests
{
    [Fact]
    public void NewShapeFramesMatchSequentialEditsAcrossInheritedMasksAndSharedPools()
    {
        var batch = new LmbEditDocument(Movie(shared: true));
        var sprite = batch.AddSprite("layout", 3);
        var original = batch.Export(false);
        var sequential = new LmbEditDocument(original);
        var edits = new LmbPlacementEdit[]
        {
            new(sprite, 0, 1, 1, new(1, 0, 0, 1, 10, 20), .5f, "mask", ClipDepth: 2),
            new(sprite, 0, 2, 1, new(1, 0, 0, 1, 10, 20), .5f, "body"),
            new(sprite, 1, 1, 1, new(1, 0, 0, 1, 10, 20), 1, ""),
            new(sprite, 1, 2, 1, new(2, 0, 0, 2, 30, 40), 0, "changed"),
        };
        foreach (var p in edits)
            sequential.SetPlacement(p.Sprite, p.Frame, p.Depth, p.Character, p.Matrix,
                p.Opacity, p.Name, p.BlendMode, p.ClipDepth);
        batch.SetPlacements(edits);
        var reopened = new LmbEditDocument(batch.Export(false));
        for (var frame = 0; frame < 3; frame++)
        {
            var expected = sequential.Placements(sprite, frame);
            var actual = reopened.Placements(sprite, frame);
            Assert.Equal(expected.Select(p => (p.Depth, p.CharacterId, p.PlacementId, p.FirstFrame, p.ClipDepth, p.BlendMode)),
                actual.Select(p => (p.Depth, p.CharacterId, p.PlacementId, p.FirstFrame, p.ClipDepth, p.BlendMode)));
            Assert.Equal(expected.Select(sequential.Matrix), actual.Select(reopened.Matrix));
            Assert.Equal(expected.Select(sequential.Opacity), actual.Select(reopened.Opacity));
            Assert.Equal(expected.Select(p => sequential.Definition.Strings[(int)p.NameStringIndex].Value),
                actual.Select(p => reopened.Definition.Strings[(int)p.NameStringIndex].Value));
        }
        Assert.Empty(errors(reopened));
        var before = batch.Export(false);
        Assert.Throws<ArgumentException>(() => batch.SetPlacements([edits[0] with { Frame = 2 },
            edits[1] with { Frame = 2 }, edits[0] with { Character = 99999 }]));
        Assert.Equal(before, batch.Export(false));
        batch.Undo();
        Assert.Equal(original, batch.Export(false));
    }

    [Fact]
    public void BatchedPlacementsMatchSequentialSeeksAndRollbackTogether()
    {
        var edits = new LmbPlacementEdit[]
        {
            new(2, 0, 3, 1, new(1, 0, 0, 1, 11, 13), Name: "first"),
            new(2, 1, 4, 1, new(1, 0, 0, 1, 23, 29), .5f, "second"),
        };
        var batch = new LmbEditDocument(Movie(shared: true));
        var sequential = new LmbEditDocument(Movie(shared: true));
        foreach (var p in edits)
            sequential.SetPlacement(p.Sprite, p.Frame, p.Depth, p.Character, p.Matrix, p.Opacity, p.Name);
        batch.SetPlacements(edits);
        var reopened = new LmbEditDocument(batch.Export(false));
        for (var frame = 0; frame <= 2; frame++)
        {
            var expected = sequential.Placements(2, frame);
            var actual = reopened.Placements(2, frame);
            Assert.Equal(expected.Select(p => (p.Depth, p.CharacterId)), actual.Select(p => (p.Depth, p.CharacterId)));
            Assert.Equal(expected.Select(p => sequential.Matrix(p)), actual.Select(p => reopened.Matrix(p)));
            Assert.Equal(expected.Select(p => sequential.Opacity(p)), actual.Select(p => reopened.Opacity(p)));
        }
        var before = batch.Export(false);
        Assert.Throws<ArgumentException>(() => batch.SetPlacements([edits[0], edits[1] with { Character = 99999 }]));
        Assert.Equal(before, batch.Export(false));
        batch.Undo();
        Assert.Equal(Movie(shared: true), batch.Export(false));
    }

    [Fact]
    public void CharacterRemappingKeepsAnimationAndDoesNotAlterItsSourceShape()
    {
        var document = new LmbEditDocument(Movie(shared: true));
        var before = document.Definition.Sprites.Single().Timeline.OfType<LmbPlaceObjectCommand>().ToArray();
        var copy = document.CopyShape(1, v => v with { U = v.U * .5f });
        document.RemapCharacters(2, new Dictionary<uint, uint> { [1] = copy });
        var reopened = new LmbEditDocument(document.Export(false));
        var after = reopened.Definition.Sprites.Single().Timeline.OfType<LmbPlaceObjectCommand>().ToArray();
        Assert.Equal(before.Length, after.Length);
        foreach (var (a, b) in before.Zip(after))
        {
            Assert.Equal(copy, b.CharacterId);
            Assert.Equal((a.PlacementId, a.Depth, a.PositionIndex), (b.PlacementId, b.Depth, b.PositionIndex));
        }
        Assert.Equal(1f, reopened.Definition.Shapes.Single(s => s.CharacterId == 1).Geometry[0].Vertices.Max(v => v.U));
    }

    [Fact]
    public void NamingAnimatedChildrenRetainsTheirKeysAndPlacementIdentities()
    {
        var document = new LmbEditDocument(Movie(shared: true));
        var before = document.Definition.Sprites.Single().Timeline.OfType<LmbPlaceObjectCommand>().ToArray();
        document.NamePlacements(2, p => p.Depth == 1 ? "edge" : null);
        var reopened = new LmbEditDocument(document.Export(false));
        var after = reopened.Definition.Sprites.Single().Timeline.OfType<LmbPlaceObjectCommand>().ToArray();
        Assert.Equal(before.Length, after.Length);
        foreach (var (original, named) in before.Zip(after))
        {
            Assert.Equal(original.CharacterId, named.CharacterId);
            Assert.Equal(original.PlacementId, named.PlacementId);
            Assert.Equal(original.Depth, named.Depth);
            Assert.Equal(original.PositionIndex, named.PositionIndex);
            Assert.Equal(original.FirstFrame, named.FirstFrame);
            if (named.Depth == 1) Assert.Equal("edge", reopened.Definition.Strings[(int)named.NameStringIndex].Value);
        }
    }

    [Fact]
    public void RemappedDepthsRetainMasksRemovalsAndPlacementIdentitiesAfterExport()
    {
        var document = new LmbEditDocument(Movie(shared: true));
        document.SetPlacement(2, 0, 1, 1, new(1, 0, 0, 1, 0, 0), clipDepth: 2);
        document.RemovePlacement(2, 1, 2);
        var identity = document.Placements(2, 0).Single(p => p.Depth == 1).PlacementId;
        document.RemapDepths(2, static depth => checked((ushort)(depth + 1)));
        var reopened = new LmbEditDocument(document.Export(false));
        var mask = reopened.Placements(2, 0).Single(p => p.Depth == 2);
        Assert.Equal(3, mask.ClipDepth);
        Assert.Equal(identity, mask.PlacementId);
        Assert.Equal(3, reopened.Definition.Sprites.Single().Timeline.OfType<LmbRemoveObjectCommand>().First().Depth);
        Assert.Equal(2, Assert.Single(reopened.Placements(2, 2)).Depth);
        var before = document.Export(false);
        Assert.Throws<ArgumentException>(() => document.RemapDepths(2, static _ => 0));
        Assert.Equal(before, document.Export(false));
    }

    [Fact]
    public void CopiedShapeCanCropUvsWithoutChangingTheOriginalAndInvalidEditsAreAtomic()
    {
        var document = new LmbEditDocument(Movie());
        var original = document.Definition.Shapes[0];
        var copy = document.CopyShape(1, v => v.V == 0 ? v with { Y = 2, V = .25f } : v);
        var exported = document.Export(false);
        var reopened = LmbSemanticReader.Read(LmbFile.Parse(exported)).Value;
        var unchanged = reopened.Shapes.Single(s => s.CharacterId == 1);
        Assert.Equal(original.Geometry[0].RawRecord.Payload.ToArray(), unchanged.Geometry[0].RawRecord.Payload.ToArray());
        var geometry = Assert.Single(reopened.Shapes.Single(s => s.CharacterId == copy).Geometry);
        Assert.Equal(2, geometry.Vertices.Min(v => v.Y));
        Assert.Equal(.25f, geometry.Vertices.Min(v => v.V));
        Assert.Equal(original.Geometry[0].TextureIndex, geometry.TextureIndex);
        Assert.Equal(original.Geometry[0].Flags, geometry.Flags);
        Assert.Throws<ArgumentException>(() => document.CopyShape(1, v => v with { U = float.NaN }));
        Assert.Equal(exported, document.Export(false));
        document.Undo();
        Assert.Equal(Movie(), document.Export(true));
    }

    [Fact]
    public void NativeSurfaceShapeKeepsItsCenteredViewportAfterExport()
    {
        var document = new LmbEditDocument(Movie());
        var shape = document.AddShape(0, 96, 400, nativeSurface: true, centered: true);
        var reopened = LmbSemanticReader.Read(LmbFile.Parse(document.Export(false))).Value;
        var geometry = Assert.Single(reopened.Shapes.Single(s => s.CharacterId == shape).Geometry);
        Assert.Equal(0u, geometry.Flags);
        Assert.Equal(-48, geometry.Vertices.Min(v => v.X));
        Assert.Equal(200, geometry.Vertices.Max(v => v.Y));
    }

    [Fact]
    public void UneditedExportIsByteIdenticalAndDoesNotAliasCallerMemory()
    {
        var source = Movie();
        var document = new LmbEditDocument(source);
        source[30] ^= 0xFF;
        Assert.Equal(Movie(), document.Export(true));
        Assert.False(document.IsChanged);
    }

    [Fact]
    public void EditingAUniqueMatrixKeepsLayoutAndSynchronizesSeekSnapshots()
    {
        var source = Movie();
        var document = new LmbEditDocument(source);
        document.SetPlacement(2, 0, 1, 1, new LmbMatrix(2, 0, 0, 3, 42, 55));
        Assert.True(document.PreservesRecordLayout);
        var matrix = document.Matrix(Assert.Single(document.Placements(2, 2)));
        Assert.Equal(42, matrix.X);
        Assert.Equal(3, matrix.M22);
        var key = document.Definition.Sprites[0].Timeline.OfType<LmbFrameKeyCommand>().Single();
        var snapshot = document.Definition.Sprites[0].Timeline.OfType<LmbPlaceObjectCommand>().Single(p => p.Record.Index > key.Record.Index);
        Assert.Equal(42, document.Matrix(snapshot).X);
        Assert.Equal(0xCAFEu, BinaryPrimitives.ReadUInt32BigEndian(snapshot.Record.Payload.Span[40..]));
        Assert.Equal(source.AsSpan(0, 64).ToArray(), document.Export(true).AsSpan(0, 64).ToArray());
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, document.Definition.RawFile.GetSingleRecord(0xDEAD).Payload.ToArray());
        Assert.Empty(errors(document));
    }

    [Fact]
    public void SharedMatricesAreCopiedSoUnselectedElementsDoNotMove()
    {
        var document = new LmbEditDocument(Movie(shared: true));
        document.SetPlacement(2, 0, 1, 1, new LmbMatrix(1, 0, 0, 1, 77, 0));
        Assert.Equal(77, document.Matrix(document.Placements(2, 0).Single(p => p.Depth == 1)).X);
        Assert.Equal(10, document.Matrix(document.Placements(2, 0).Single(p => p.Depth == 2)).X);
        Assert.False(document.PreservesRecordLayout);
        Assert.Throws<InvalidOperationException>(() => document.Export(true));
    }

    [Fact]
    public void BatchMatrixMappingKeepsSharedPoolsAndSeekSnapshotsConsistentAndUndoesAtomically()
    {
        var source = Movie(shared: true);
        var document = new LmbEditDocument(source);
        var originalMatrix = document.Definition.Matrices[0];
        document.MapPlacementMatrices(2, (_, p, m) => p.Depth == 1 ? m with { X = 1280 - m.X, M11 = -m.M11 } : m);
        Assert.Equal(originalMatrix, document.Definition.Matrices[0]);
        Assert.Equal(1270, document.Matrix(document.Placements(2, 2).Single(p => p.Depth == 1)).X);
        Assert.Equal(10, document.Matrix(document.Placements(2, 2).Single(p => p.Depth == 2)).X);
        var key = document.Definition.Sprites[0].Timeline.OfType<LmbFrameKeyCommand>().Single();
        var snapshot = document.Definition.Sprites[0].Timeline.OfType<LmbPlaceObjectCommand>().Single(p => p.Depth == 1 && p.RawRecord.Index > key.RawRecord.Index);
        Assert.Equal(-1, document.Matrix(snapshot).M11);
        Assert.Equal(1270, document.Matrix(snapshot).X);
        var changed = document.Data.ToArray();
        document.Undo(); Assert.Equal(source, document.Data.ToArray());
        document.Redo(); Assert.Equal(changed, document.Data.ToArray());
        Assert.Throws<ArgumentException>(() => document.MapPlacementMatrices(2, (_, _, m) => m with { X = float.NaN }));
        Assert.Equal(changed, document.Data.ToArray());
    }

    [Fact]
    public void CurrentFrameEditsDoNotRewriteEarlierFramesAndUndoRedoRetainBaseline()
    {
        var source = Movie();
        var document = new LmbEditDocument(source);
        document.SetPlacement(2, 1, 1, 1, new LmbMatrix(1, 0, 0, 1, 99, 7), 0);
        Assert.Equal(10, document.Matrix(Assert.Single(document.Placements(2, 0))).X);
        Assert.Equal(99, document.Matrix(Assert.Single(document.Placements(2, 1))).X);
        Assert.Equal(0, document.Opacity(Assert.Single(document.Placements(2, 2))));
        document.Undo(); Assert.Equal(source, document.Export(true));
        document.Redo(); Assert.Equal(99, document.Matrix(Assert.Single(document.Placements(2, 2))).X);
        Assert.False(new LmbEditDocument(document.Data.Span, source).PreservesRecordLayout);
        Assert.Empty(errors(document));
    }

    [Fact]
    public void AddRemoveLabelAndDurationRoundTripWithValidCounts()
    {
        var document = new LmbEditDocument(Movie());
        document.SetPlacement(2, 1, 7, 1, new LmbMatrix(1, 0, 0, 1, 5, 6), name: "extra");
        Assert.Single(document.Placements(2, 0));
        Assert.Equal(2, document.Placements(2, 1).Length);
        var added = document.Placements(2, 1).Single(p => p.Depth == 7);
        Assert.Equal("extra", document.Definition.Strings[(int)added.NameStringIndex].Value);
        Assert.NotEqual(0u, added.NameStringIndex);
        document.AddLabel(2, 1, "toggle_on");
        document.ExtendTimeline(2, 5);
        document.RemovePlacement(2, 2, 7);
        Assert.Single(document.Placements(2, 4));
        Assert.Empty(errors(document));
        var parsed = LmbSemanticReader.Read(LmbFile.Parse(document.Export(false)));
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Code.EndsWith("COUNT_MISMATCH", StringComparison.Ordinal));
    }

    [Fact]
    public void TweenBakesIntermediateFramesAndKeepsSeekingConsistent()
    {
        var document = new LmbEditDocument(Movie());
        document.Tween(2, 1, 0, 2, new LmbMatrix(3, 0, 0, 3, 30, 40), 0, smooth: false);
        var middle = Assert.Single(document.Placements(2, 1));
        Assert.Equal(20, document.Matrix(middle).X);
        Assert.Equal(0.5f, document.Opacity(middle));
        Assert.Equal(30, document.Matrix(Assert.Single(document.Placements(2, 2))).X);
        Assert.Empty(errors(document));
    }

    [Fact]
    public void InvalidEditsAreAtomicAndDoNotPolluteHistory()
    {
        var document = new LmbEditDocument(Movie());
        Assert.Throws<InvalidOperationException>(() => document.SetPlacement(2, 0, 7, 2, new LmbMatrix(1, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentException>(() => document.SetPlacement(2, 0, 1, 1, new LmbMatrix(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => document.RemovePlacement(2, 1, 99));
        Assert.False(document.IsChanged); Assert.False(document.CanUndo);
    }

    [Fact]
    public void RotationTweenKeepsTheShapeSizeAndAnExactlyAuthoredEndpoint()
    {
        var document = new LmbEditDocument(Movie());
        var target = new LmbMatrix(0, 1, -1, 0, 10, 20);
        document.Tween(2, 1, 0, 2, target, 1, smooth: false);
        var middle = document.Matrix(Assert.Single(document.Placements(2, 1)));
        Assert.InRange(middle.M11, 0.7070f, 0.7072f);
        Assert.InRange(middle.M12, 0.7070f, 0.7072f);
        Assert.Equal(target, document.Matrix(Assert.Single(document.Placements(2, 2))));
    }

    [Fact]
    public void NewContainerAndKeyDeletionLeaveReferencesAndSnapshotsConsistent()
    {
        var document = new LmbEditDocument(Movie());
        var id = document.AddSprite("editor_container", 3);
        document.SetPlacement(id, 0, 1, 1, new LmbMatrix(1, 0, 0, 1, 20, 30));
        document.SetPlacement(2, 1, 8, id, new LmbMatrix(1, 0, 0, 1, 40, 50));
        document.RemoveKeyframe(2, 1, 8);
        Assert.Single(document.Placements(2, 2));
        Assert.Empty(errors(document));
    }

    [Fact]
    public void SentinelsAndVeryLargePlacementIdsDoNotPreventAddingAnElement()
    {
        var bytes = Movie();
        var parsed = LmbSemanticReader.Read(LmbFile.Parse(bytes)).Value;
        foreach (var place in parsed.Sprites.SelectMany(s => s.Timeline).OfType<LmbPlaceObjectCommand>())
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)place.Record.PayloadOffset + 4, 4), uint.MaxValue);
        var document = new LmbEditDocument(bytes);
        document.SetPlacement(2, 1, 7, 1, new LmbMatrix(1, 0, 0, 1, 4, 5));
        Assert.Equal(1u, document.Placements(2, 1).Single(p => p.Depth == 7).PlacementId);
        Assert.Empty(errors(document));
    }

    [Fact]
    public void NewShapeRetainsExistingDefinitionAndCanBePlaced()
    {
        var document = new LmbEditDocument(Movie());
        var id = document.AddShape(0, 64, 32);
        Assert.Equal(2, document.Definition.Shapes.Length);
        document.SetPlacement(2, 1, 9, id, new LmbMatrix(1, 0, 0, 1, 100, 100));
        Assert.Equal(id, document.Placements(2, 2).Single(p => p.Depth == 9).CharacterId);
        Assert.Empty(errors(document));
    }

    private static IEnumerable<ParseDiagnostic> errors(LmbEditDocument document) =>
        LmbSemanticReader.Read(LmbFile.Parse(document.Data), validationContext: new LmbSemanticValidationContext(1))
            .Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    internal static byte[] Movie(bool shared = false)
    {
        using var stream = new MemoryStream(); stream.Write("LMB\0"u8); stream.Write(new byte[60]);
        Record(stream, 0xF00C, 0, 0, 0, 2, 0, 0, 0, F(60));
        Record(stream, 0xF001, 1, 0, 0);
        Record(stream, 0xF003, 1, F(1), 0, 0, F(1), F(10), F(20));
        Record(stream, 0xF002, 1, 0x01000100, 0x01000100);
        Record(stream, 0xDEAD, 0x11223344);
        Record(stream, 0xF022, 1, 0, 0, 1);
        Record(stream, 0xF023, 0, 0, 0, 0, F(8), 0, F(1), 0, F(8), F(8), F(1), F(1), 0, F(8), 0, F(1), 0, 0x10000);
        Record(stream, 0x27, 2, 0, 0, 0, 3, 0, 0);
        Record(stream, 1, 0, shared ? 2u : 1u);
        Record(stream, 4, 1, 1, 0xABCD, 0, 0x10000, 0x10000, 0, 0, 0, uint.MaxValue, 0xAAAA, 0);
        if (shared) Record(stream, 4, 1, 2, 0, 0, 0x10000, 0x20000, 0, 0, 0, uint.MaxValue, 0, 0);
        Record(stream, 1, 1, 0);
        Record(stream, 1, 2, 0);
        Record(stream, 0xF105, 2, shared ? 2u : 1u);
        Record(stream, 4, 1, 1, 0xDCBA, 0, 0x10000, 0x10000, 0, 0, 0, uint.MaxValue, 0xCAFE, 0);
        if (shared) Record(stream, 4, 1, 2, 0, 0, 0x10000, 0x20000, 0, 0, 0, uint.MaxValue, 0, 0);
        return stream.ToArray();
    }

    internal static uint F(float value) => BitConverter.SingleToUInt32Bits(value);
    internal static void Record(Stream stream, uint tag, params uint[] words)
    {
        Word(stream, tag); Word(stream, (uint)words.Length); foreach (var word in words) Word(stream, word);
    }
    internal static void Word(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes);
    }
}
