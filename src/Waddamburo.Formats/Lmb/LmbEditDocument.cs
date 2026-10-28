using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Waddamburo.Formats.Diagnostics;

namespace Waddamburo.Formats.Lmb;

/// <summary>Transactional editing of the observed Green LMB profile, retaining opaque bytes.</summary>
public sealed class LmbEditDocument
{
    private readonly byte[] _original;
    private readonly List<byte[]> _undo = [];
    private readonly Stack<byte[]> _redo = [];
    private byte[] _bytes;

    public LmbEditDocument(ReadOnlySpan<byte> data, ReadOnlySpan<byte> baseline = default)
    {
        _original = baseline.IsEmpty ? data.ToArray() : baseline.ToArray();
        _bytes = data.ToArray();
        Definition = read(_bytes);
    }

    public LmbMovieDefinition Definition { get; private set; }
    public ReadOnlyMemory<byte> Data => _bytes;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool IsChanged => !_bytes.AsSpan().SequenceEqual(_original);
    private byte[]? _layoutBytes;
    private bool _preservesLayout;
    public bool PreservesRecordLayout
    {
        get
        {
            if (!ReferenceEquals(_layoutBytes, _bytes))
            {
                _preservesLayout = sameLayout(_original, _bytes);
                _layoutBytes = _bytes;
            }
            return _preservesLayout;
        }
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(_bytes);
        _bytes = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Definition = read(_bytes);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(_bytes);
        _bytes = _redo.Pop();
        Definition = read(_bytes);
    }

    /// <summary>Timeline-only state. ActionScript may change the runtime display independently.</summary>
    private LmbMovieDefinition? _cachedDefinition;
    private readonly Dictionary<(uint Sprite, int Frame), ImmutableArray<LmbPlaceObjectCommand>> _placements = [];
    public ImmutableArray<LmbPlaceObjectCommand> Placements(uint spriteId, int frame)
    {
        if (!ReferenceEquals(_cachedDefinition, Definition) || _placements.Count > 4096)
        {
            _placements.Clear(); _cachedDefinition = Definition;
        }
        var key = (spriteId, frame);
        if (!_placements.TryGetValue(key, out var result))
            _placements[key] = result = state(Definition, spriteId, frame).Values.OrderBy(static p => p.Depth).ToImmutableArray();
        return result;
    }

    public LmbMatrix Matrix(LmbPlaceObjectCommand place) => place.PositionKind switch
    {
        0 when place.PositionIndex < Definition.Matrices.Length => Definition.Matrices[place.PositionIndex],
        0x8000 when place.PositionIndex < Definition.Translations.Length => new LmbMatrix(1, 0, 0, 1,
            Definition.Translations[place.PositionIndex].X, Definition.Translations[place.PositionIndex].Y),
        _ => new LmbMatrix(1, 0, 0, 1, 0, 0),
    };

    public float Opacity(LmbPlaceObjectCommand place) => place.ColorMultiplyIndex < Definition.ColorTransforms.Length
        ? Definition.ColorTransforms[(int)place.ColorMultiplyIndex].Alpha / 256f : 1;

    /// <summary>Returns an existing F001 string index or adds a UTF-8 string for script operands.</summary>
    public uint InternString(string value)
    {
        uint index = 0;
        edit(records => index = addString(records, value), null);
        return index;
    }

    /// <summary>Replaces one F005 action, preserving unrelated entries and padding.</summary>
    public void SetActionBytecode(int actionIndex, ReadOnlySpan<byte> bytecode)
    {
        if ((uint)actionIndex >= (uint)Definition.Actions.Length) throw new ArgumentOutOfRangeException(nameof(actionIndex));
        var bytes = bytecode.ToArray();
        _ = Avm1BytecodeReader.Read([.. bytes], 0, ParserLimits.Default);
        edit(records =>
        {
            var baseIndex = 0;
            foreach (var pool in records.Where(r => r.Tag == LmbTags.ActionPool))
            {
                var count = checked((int)get(pool.Payload, 0));
                if (actionIndex >= baseIndex + count) { baseIndex += count; continue; }
                var offset = 4;
                for (var i = 0; i < actionIndex - baseIndex; i++) offset += checked(4 + (((int)get(pool.Payload, offset / 4) + 3) & ~3));
                var oldSize = checked((int)get(pool.Payload, offset / 4));
                var oldStorage = (oldSize + 3) & ~3; var newStorage = (bytes.Length + 3) & ~3;
                var payload = new byte[checked(pool.Payload.Length - oldStorage + newStorage)];
                pool.Payload.AsSpan(0, offset).CopyTo(payload);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)bytes.Length);
                bytes.CopyTo(payload, offset + 4);
                if (oldSize == bytes.Length) pool.Payload.AsSpan(offset + 4 + oldSize, oldStorage - oldSize).CopyTo(payload.AsSpan(offset + 4 + bytes.Length));
                pool.Payload.AsSpan(offset + 4 + oldStorage).CopyTo(payload.AsSpan(offset + 4 + newStorage));
                pool.Payload = payload;
                return;
            }
            throw new InvalidDataException("The action pool entry was not found.");
        }, null);
    }

    /// <summary>Adds an action and a timeline invocation at the selected frame.</summary>
    public int AddAction(uint spriteId, int frame, ReadOnlySpan<byte> bytecode)
    {
        var bytes = bytecode.ToArray();
        _ = Avm1BytecodeReader.Read([.. bytes], 0, ParserLimits.Default);
        var index = Definition.Actions.Length;
        edit(records =>
        {
            var pool = records.LastOrDefault(r => r.Tag == LmbTags.ActionPool);
            if (pool is null) { pool = new EditRecord(LmbTags.ActionPool, words(0)); records.Insert(0, pool); }
            var count = get(pool.Payload, 0);
            var payload = new byte[checked(pool.Payload.Length + 4 + ((bytes.Length + 3) & ~3))];
            pool.Payload.CopyTo(payload, 0);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(pool.Payload.Length), (uint)bytes.Length);
            bytes.CopyTo(payload, pool.Payload.Length + 4);
            put(payload, 0, count + 1); pool.Payload = payload;
            var group = frameGroup(definition(records), spriteId, frame);
            records.Insert(group.End, new EditRecord(LmbTags.DoAction, words((uint)index, 0)));
        }, spriteId);
        return index;
    }

    public void SetPlacement(uint spriteId, int frame, ushort depth, uint characterId, LmbMatrix matrix,
        float opacity = 1, string? name = null, ushort blend = 0, ushort clipDepth = 0)
    {
        validateMatrix(matrix);
        if (!float.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        edit(records => setPlacement(records, spriteId, frame, depth, characterId, matrix, opacity, name, blend, clipDepth), spriteId);
    }

    /// <summary>Maps authored position keys in one transaction, retaining inherited transforms and rebuilding seek snapshots once.</summary>
    public void MapPlacementMatrices(uint spriteId, Func<int, LmbPlaceObjectCommand, LmbMatrix, LmbMatrix> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var movie = Definition;
        var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
        edit(records =>
        {
            var originalRecords = records.ToArray();
            int? frame = null;
            foreach (var command in sprite.Timeline)
            {
                if (command is LmbShowFrameCommand show) { frame = checked((int)show.Frame); continue; }
                if (command is LmbFrameKeyCommand) { frame = null; continue; }
                if (frame is null || command is not LmbPlaceObjectCommand place || place.PositionKind == ushort.MaxValue) continue;
                if (place.PositionKind is not (0 or 0x8000)) throw new InvalidOperationException("Cannot map an unknown placement position kind.");
                var original = Matrix(place);
                var mapped = map(frame.Value, place, original);
                validateMatrix(mapped);
                if (mapped == original) continue;
                var matrixIndex = storePoolEntry(records, LmbTags.MatrixPool,
                    words(f(mapped.M11), f(mapped.M12), f(mapped.M21), f(mapped.M22), f(mapped.X), f(mapped.Y)), null, movie, p => p.PositionIndex);
                put(originalRecords[place.RawRecord.Index].Payload, 7, checked((ushort)matrixIndex));
            }
        }, spriteId);
    }

    /// <summary>Names existing timeline children without replacing their animation keys or identities.</summary>
    public void NamePlacements(uint spriteId, Func<LmbPlaceObjectCommand, string?> name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var placements = Definition.Sprites.Single(s => s.CharacterId == spriteId).Timeline
            .OfType<LmbPlaceObjectCommand>().ToArray();
        edit(records =>
        {
            var original = records.ToArray();
            foreach (var place in placements)
                if (name(place) is { } value)
                    put(original[place.RawRecord.Index].Payload, 3, addString(records, value));
        }, spriteId);
    }

    /// <summary>Remaps sibling depths, including removals and mask endpoints, while retaining placement identities.</summary>
    public void RemapDepths(uint spriteId, Func<ushort, ushort> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var timeline = Definition.Sprites.Single(s => s.CharacterId == spriteId).Timeline;
        var depths = timeline.SelectMany(command => command switch
        {
            LmbPlaceObjectCommand p => p.ClipDepth == 0 ? new[] { p.Depth } : new[] { p.Depth, p.ClipDepth },
            LmbRemoveObjectCommand r => [r.Depth],
            _ => Array.Empty<ushort>(),
        }).Distinct().ToDictionary(depth => depth, map);
        if (depths.Values.Distinct().Count() != depths.Count)
            throw new ArgumentException("Depth remapping must preserve distinct sibling depths.", nameof(map));
        edit(records =>
        {
            foreach (var command in timeline)
            {
                if (command is LmbPlaceObjectCommand p)
                    put(records[p.RawRecord.Index].Payload, 5,
                        (uint)depths[p.Depth] << 16 | (p.ClipDepth == 0 ? 0U : depths[p.ClipDepth]));
                else if (command is LmbRemoveObjectCommand r)
                    put(records[r.RawRecord.Index].Payload, 1, (uint)depths[r.Depth] << 16 | (r.PackedDepth & 0xffff));
            }
        }, spriteId);
    }

    /// <summary>Replaces character references together, retaining placement transforms and identity.</summary>
    public void RemapCharacters(uint spriteId, IReadOnlyDictionary<uint, uint> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var known = Definition.Shapes.Select(s => s.CharacterId).Concat(Definition.Sprites.Select(s => s.CharacterId))
            .Concat(Definition.Texts.Select(t => t.CharacterId)).ToHashSet();
        if (replacements.Values.Any(id => !known.Contains(id)))
            throw new ArgumentException("Unknown replacement character.", nameof(replacements));
        var timeline = Definition.Sprites.Single(s => s.CharacterId == spriteId).Timeline;
        edit(records =>
        {
            foreach (var place in timeline.OfType<LmbPlaceObjectCommand>())
                if (replacements.TryGetValue(place.CharacterId, out var replacement))
                    put(records[place.RawRecord.Index].Payload, 0, replacement);
        }, spriteId);
    }

    /// <summary>Applies a generated group transactionally, validating the result once.</summary>
    public void SetPlacements(IReadOnlyList<LmbPlacementEdit> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        foreach (var p in placements)
        {
            validateMatrix(p.Matrix);
            if (!float.IsFinite(p.Opacity) || p.Opacity is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(placements), "Opacity must be between zero and one.");
        }
        edit(records =>
        {
            for (var index = 0; index < placements.Count;)
            {
                var end = index + 1;
                while (end < placements.Count && placements[end].Sprite == placements[index].Sprite
                    && placements[end].Frame == placements[index].Frame) end++;
                var group = placements.Skip(index).Take(end - index).ToArray();
                if (group.Length < 2 || !placeShapeFrame(records, group))
                    foreach (var p in group)
                        setPlacement(records, p.Sprite, p.Frame, p.Depth, p.Character, p.Matrix,
                            p.Opacity, p.Name, p.BlendMode, p.ClipDepth);
                index = end;
            }
            foreach (var sprite in placements.Select(p => p.Sprite).Distinct())
                rebuildSnapshots(records, sprite);
        }, null);
    }

    // New layout frames contain many independent shapes. Parse their enclosing movie once,
    // rather than serializing and reparsing the entire movie twice for every border slice.
    // Existing commands, repeated depths and sprite references use the general editor below.
    private bool placeShapeFrame(List<EditRecord> records, LmbPlacementEdit[] edits)
    {
        if (edits.Select(p => p.Depth).Distinct().Count() != edits.Length
            || new[] { LmbTags.MatrixPool, LmbTags.ColorTransformPool, LmbTags.StringPool }
                .Any(tag => records.Count(r => r.Tag == tag) != 1)) return false;
        var movie = definition(records);
        var shapes = movie.Shapes.Select(s => s.CharacterId).Concat(movie.Texts.Select(t => t.CharacterId)).ToHashSet();
        if (edits.Any(p => !shapes.Contains(p.Character))) return false;
        var first = edits[0];
        var group = frameGroup(movie, first.Sprite, first.Frame);
        if (records.Skip(group.Start + 1).Take(group.End - group.Start - 1)
            .Any(r => r.Tag is LmbTags.PlaceObject or LmbTags.RemoveObject)) return false;
        var previous = state(movie, first.Sprite, first.Frame);
        var used = movie.Sprites.SelectMany(s => s.Timeline).OfType<LmbPlaceObjectCommand>()
            .Select(p => p.PlacementId).ToHashSet();
        uint nextId = 1;
        var insertion = group.End;
        foreach (var p in edits)
        {
            var basis = previous.GetValueOrDefault(p.Depth);
            // Append or reuse pool values, never mutate a shared entry: earlier edits in this
            // frame can have introduced a new reference not present in the parsed snapshot.
            var matrix = p.Matrix;
            var matrixIndex = storePoolEntry(records, LmbTags.MatrixPool,
                words(f(matrix.M11), f(matrix.M12), f(matrix.M21), f(matrix.M22), f(matrix.X), f(matrix.Y)),
                null, movie, static q => q.PositionKind == 0 ? q.PositionIndex : uint.MaxValue);
            var color = basis is not null && basis.ColorMultiplyIndex < movie.ColorTransforms.Length
                ? movie.ColorTransforms[(int)basis.ColorMultiplyIndex] : new LmbColorTransform(256, 256, 256, 256);
            var alpha = checked((short)Math.Round(p.Opacity * 256));
            var colorIndex = storePoolEntry(records, LmbTags.ColorTransformPool,
                words(((uint)(ushort)color.Red << 16) | (ushort)color.Green,
                    ((uint)(ushort)color.Blue << 16) | (ushort)alpha), null, movie, static q => q.ColorMultiplyIndex);
            var addIndex = basis?.ColorAddIndex ?? uint.MaxValue;
            if (p.Opacity == 0 && addIndex < movie.ColorTransforms.Length && movie.ColorTransforms[(int)addIndex] is { Alpha: not 0 } add)
                addIndex = storePoolEntry(records, LmbTags.ColorTransformPool,
                    words(((uint)(ushort)add.Red << 16) | (ushort)add.Green, (uint)(ushort)add.Blue << 16),
                    null, movie, static q => q.ColorAddIndex);
            var name = p.Name is null ? basis?.NameStringIndex ?? 0 : addString(records, p.Name);
            var payload = basis?.RawRecord.Payload.ToArray() ?? words(p.Character, 0, 0, 0, 0, 0, 0, 0, uint.MaxValue, uint.MaxValue, 0, 0);
            put(payload, 0, p.Character);
            if (basis is null)
            {
                while (used.Contains(nextId)) nextId = checked(nextId + 1);
                if (nextId == uint.MaxValue) throw new InvalidOperationException("No free placement identity.");
                put(payload, 1, nextId);
                used.Add(nextId);
            }
            var reset = basis is not null && (basis.ClipDepth != 0 && p.ClipDepth == 0
                || basis.BlendMode != 0 && p.BlendMode == 0 || basis.NameStringIndex != 0 && name == 0);
            var mode = reset || basis is null ? 1u : basis.CharacterId == p.Character ? 2u : 3u;
            var firstFrame = reset || basis is null || basis.CharacterId != p.Character
                ? checked((ushort)p.Frame) : basis.FirstFrame;
            put(payload, 3, name);
            put(payload, 4, (mode << 16) | p.BlendMode);
            put(payload, 5, ((uint)p.Depth << 16) | p.ClipDepth);
            put(payload, 6, ((uint)firstFrame << 16) | (get(payload, 6) & 0xFFFF));
            put(payload, 7, checked((ushort)matrixIndex));
            put(payload, 8, colorIndex);
            put(payload, 9, addIndex);
            records.Insert(insertion++, new EditRecord(LmbTags.PlaceObject, payload));
        }
        return true;
    }

    public void RemovePlacement(uint spriteId, int frame, ushort depth) => edit(records =>
    {
        var movie = definition(records);
        var current = state(movie, spriteId, frame).GetValueOrDefault(depth)
            ?? throw new InvalidOperationException("No element at this depth in the selected frame.");
        var group = frameGroup(movie, spriteId, frame);
        records.Insert(group.End, new EditRecord(LmbTags.RemoveObject, words(current.CharacterId, (uint)depth << 16)));
    }, spriteId);

    /// <summary>Bakes ordinary move commands, so the animation also exists outside Waddamburo.</summary>
    public void Tween(uint spriteId, ushort depth, int from, int to, LmbMatrix target, float targetOpacity, bool smooth)
    {
        validateMatrix(target);
        if (to <= from || to - from > 2000) throw new ArgumentOutOfRangeException(nameof(to), "Choose 1–2000 animation frames.");
        if (!float.IsFinite(targetOpacity) || targetOpacity < 0 || targetOpacity > 1)
            throw new ArgumentOutOfRangeException(nameof(targetOpacity));
        var placement = state(Definition, spriteId, from).GetValueOrDefault(depth)
            ?? throw new InvalidOperationException("Select an element at the first frame.");
        var start = Matrix(placement);
        var alpha = Opacity(placement);
        var name = placement.NameStringIndex < Definition.Strings.Length ? Definition.Strings[(int)placement.NameStringIndex].Value : null;
        edit(records =>
        {
            for (var frame = from + 1; frame <= to; frame++)
            {
                var t = (float)(frame - from) / (to - from);
                if (smooth) t = t * t * (3 - 2 * t);
                float mix(float a, float b) => a + (b - a) * t;
                var matrix = interpolate(start, target, t);
                setPlacement(records, spriteId, frame, depth, placement.CharacterId, matrix,
                    mix(alpha, targetOpacity), name, placement.BlendMode, placement.ClipDepth);
            }
        }, spriteId);
    }

    public void AddLabel(uint spriteId, int frame, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        edit(records =>
        {
            var movie = definition(records);
            _ = frameGroup(movie, spriteId, frame);
            var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
            if (sprite.Timeline.OfType<LmbFrameLabelCommand>().Any(l => movie.Strings[(int)l.StringIndex].Value == label))
                throw new InvalidOperationException("This label already exists.");
            var index = addString(records, label);
            movie = definition(records);
            sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
            records.Insert(sprite.RawRecord.Index + 1, new EditRecord(LmbTags.FrameLabel, words(index, (uint)frame, 0)));
        }, spriteId);
    }

    public void RemoveKeyframe(uint spriteId, int frame, ushort depth) => edit(records =>
    {
        var movie = definition(records);
        var group = frameGroup(movie, spriteId, frame);
        var commands = movie.Sprites.Single(s => s.CharacterId == spriteId).Timeline
            .Where(c => c.RawRecord.Index > group.Start && c.RawRecord.Index < group.End
                && (c is LmbPlaceObjectCommand place && place.Depth == depth || c is LmbRemoveObjectCommand remove && remove.Depth == depth))
            .OrderByDescending(c => c.RawRecord.Index).ToArray();
        if (commands.Length == 0) throw new InvalidOperationException("No key for this element at the current frame.");
        foreach (var command in commands) records.RemoveAt(command.RawRecord.Index);
    }, spriteId);

    public uint AddSprite(string name, int frameCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (frameCount < 1 || frameCount > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(frameCount));
        var id = nextCharacter();
        edit(records =>
        {
            var nameIndex = addString(records, name);
            var offset = records.FindIndex(r => r.Tag == LmbTags.DefineSprite);
            if (offset < 0) offset = records.Count;
            records.Insert(offset++, new EditRecord(LmbTags.DefineSprite, words(id, 0, nameIndex, 0, (uint)frameCount, 0, 0)));
            for (uint frame = 0; frame < frameCount; frame++) records.Insert(offset++, new EditRecord(LmbTags.ShowFrame, words(frame, 0)));
        }, null);
        return id;
    }

    public void ExtendTimeline(uint spriteId, int frameCount)
    {
        edit(records =>
        {
            var movie = definition(records);
            var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
            if (frameCount <= sprite.DeclaredFrameCount || frameCount > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            var end = spriteEnd(records, sprite.RawRecord.Index);
            for (var frame = sprite.DeclaredFrameCount; frame < frameCount; frame++)
                records.Insert(end++, new EditRecord(LmbTags.ShowFrame, words(frame, 0)));
            put(records[sprite.RawRecord.Index].Payload, 4, (uint)frameCount);
        }, spriteId);
    }

    /// <summary>Adds a textured quad using an existing texture; does not change the NUT allocation.</summary>
    public uint AddShape(uint textureIndex, float width, float height, bool nativeSurface = false, bool centered = false)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var id = nextCharacter();
        var x = centered ? -width / 2 : 0;
        var y = centered ? -height / 2 : 0;
        edit(records =>
        {
            var offset = records.FindIndex(r => r.Tag == LmbTags.DefineSprite);
            if (offset < 0) offset = records.Count;
            records.Insert(offset++, new EditRecord(LmbTags.DefineShape, words(id, 0, 0, 1)));
            records.Insert(offset, new EditRecord(LmbTags.ShapeGeometry, words(
                f(x), f(y), f(0), f(0), f(x + width), f(y), f(1), f(0),
                f(x + width), f(y + height), f(1), f(1), f(x), f(y + height), f(0), f(1), textureIndex, nativeSurface ? 0U : 0x10000U)));
        }, null);
        return id;
    }

    /// <summary>Copies a shape and maps its vertices, retaining texture references, flags and opaque geometry words.</summary>
    public uint CopyShape(uint shapeId, Func<LmbVertex, LmbVertex> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var source = Definition.Shapes.Single(s => s.CharacterId == shapeId);
        var id = nextCharacter();
        edit(records =>
        {
            var header = source.RawRecord.Payload.ToArray();
            put(header, 0, id);
            var copied = new List<EditRecord> { new(LmbTags.DefineShape, header) };
            foreach (var geometry in source.Geometry)
            {
                var payload = geometry.RawRecord.Payload.ToArray();
                for (var index = 0; index < geometry.Vertices.Length; index++)
                {
                    var vertex = map(geometry.Vertices[index]);
                    if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V))
                        throw new ArgumentException("Shape vertices must be finite.", nameof(map));
                    put(payload, index * 4, f(vertex.X)); put(payload, index * 4 + 1, f(vertex.Y));
                    put(payload, index * 4 + 2, f(vertex.U)); put(payload, index * 4 + 3, f(vertex.V));
                }
                copied.Add(new EditRecord(LmbTags.ShapeGeometry, payload));
            }
            var offset = records.FindIndex(r => r.Tag == LmbTags.DefineSprite);
            records.InsertRange(offset < 0 ? records.Count : offset, copied);
        }, null);
        return id;
    }

    public byte[] Export(bool preserveLayout)
    {
        if (preserveLayout && !PreservesRecordLayout)
            throw new InvalidOperationException("The edit changes record sizes or offsets. Use rebuilt export; hardware compatibility needs a device test.");
        validate(_bytes);
        return _bytes.ToArray();
    }

    private void edit(Action<List<EditRecord>> action, uint? spriteId)
    {
        var records = Definition.RawFile.Records.Select(r => new EditRecord(r.Tag, r.Payload.ToArray())).ToList();
        action(records);
        repairCounts(records);
        if (spriteId is uint sprite) rebuildSnapshots(records, sprite);
        repairCounts(records);
        var bytes = serialize(records);
        var validated = validate(bytes);
        if (bytes.AsSpan().SequenceEqual(_bytes)) return;
        _undo.Add(_bytes);
        // Bound history by both count and bytes; never keep multiple gigabytes of source data.
        while (_undo.Count > 64 || (_undo.Count > 1 && _undo.Sum(static b => (long)b.Length) > 128 * 1024 * 1024))
            _undo.RemoveAt(0);
        _redo.Clear();
        _bytes = bytes;
        Definition = validated;
    }

    private void setPlacement(List<EditRecord> records, uint spriteId, int frame, ushort depth, uint characterId,
        LmbMatrix matrix, float opacity, string? name, ushort blend, ushort clipDepth)
    {
        var movie = definition(records);
        if (!movie.Shapes.Any(s => s.CharacterId == characterId) && !movie.Sprites.Any(s => s.CharacterId == characterId)
            && !movie.Texts.Any(s => s.CharacterId == characterId))
            throw new ArgumentException("Unknown character.", nameof(characterId));
        if (wouldCycle(movie, characterId, spriteId, []))
            throw new InvalidOperationException("This placement would create a recursive sprite hierarchy.");
        var group = frameGroup(movie, spriteId, frame);
        var previous = state(movie, spriteId, frame).GetValueOrDefault(depth);
        var existing = movie.Sprites.Single(s => s.CharacterId == spriteId).Timeline.OfType<LmbPlaceObjectCommand>()
            .LastOrDefault(p => p.Depth == depth && p.RawRecord.Index > group.Start && p.RawRecord.Index < group.End);
        if (previous is null) existing = null;
        var basis = previous ?? existing;
        var matrixWords = words(f(matrix.M11), f(matrix.M12), f(matrix.M21), f(matrix.M22), f(matrix.X), f(matrix.Y));
        var matrixIndex = storePoolEntry(records, LmbTags.MatrixPool, matrixWords,
            existing is { PositionKind: 0 } ? existing.PositionIndex : null, movie,
            p => p.PositionKind == 0 ? p.PositionIndex : uint.MaxValue);
        var color = basis is not null && basis.ColorMultiplyIndex < movie.ColorTransforms.Length
            ? movie.ColorTransforms[(int)basis.ColorMultiplyIndex] : new LmbColorTransform(256, 256, 256, 256);
        var alpha = checked((short)Math.Round(opacity * 256));
        var colorWords = words(((uint)(ushort)color.Red << 16) | (ushort)color.Green,
            ((uint)(ushort)color.Blue << 16) | (ushort)alpha);
        var colorIndex = storePoolEntry(records, LmbTags.ColorTransformPool, colorWords,
            existing?.ColorMultiplyIndex, movie, p => p.ColorMultiplyIndex);
        var addIndex = basis?.ColorAddIndex ?? uint.MaxValue;
        if (opacity == 0 && addIndex < movie.ColorTransforms.Length && movie.ColorTransforms[(int)addIndex] is { Alpha: not 0 } add)
            addIndex = storePoolEntry(records, LmbTags.ColorTransformPool,
                words(((uint)(ushort)add.Red << 16) | (ushort)add.Green, (uint)(ushort)add.Blue << 16), addIndex, movie, p => p.ColorAddIndex);
        var stringIndex = name is null ? basis?.NameStringIndex ?? 0 : addString(records, name);
        movie = definition(records);
        group = frameGroup(movie, spriteId, frame);
        existing = movie.Sprites.Single(s => s.CharacterId == spriteId).Timeline.OfType<LmbPlaceObjectCommand>()
            .LastOrDefault(p => p.Depth == depth && p.RawRecord.Index > group.Start && p.RawRecord.Index < group.End);
        if (previous is null) existing = null;
        var payload = basis?.RawRecord.Payload.ToArray() ?? words(characterId, 0, 0, 0, 0, 0, 0, 0, uint.MaxValue, uint.MaxValue, 0, 0);
        put(payload, 0, characterId);
        // Placement identity is not a depth. Allocate a movie-wide unused id for new instances.
        if (basis is null)
        {
            var used = movie.Sprites.SelectMany(s => s.Timeline).OfType<LmbPlaceObjectCommand>().Select(p => p.PlacementId).ToHashSet();
            uint id = 1;
            while (used.Contains(id)) id = checked(id + 1);
            if (id == uint.MaxValue) throw new InvalidOperationException("No free placement identity.");
            put(payload, 1, id);
        }
        put(payload, 3, stringIndex);
        var mode = existing?.Mode ?? (previous is null ? (ushort)1 : previous.CharacterId == characterId ? (ushort)2 : (ushort)3);
        if (existing is not null && existing.Mode == 2 && previous?.CharacterId != characterId) mode = 3;
        var reset = previous is not null && ((previous.ClipDepth != 0 && clipDepth == 0)
            || (previous.BlendMode != 0 && blend == 0) || (previous.NameStringIndex != 0 && stringIndex == 0));
        if (reset) mode = 1;
        put(payload, 4, ((uint)mode << 16) | blend);
        put(payload, 5, ((uint)depth << 16) | clipDepth);
        var firstFrame = reset || (previous is not null && previous.CharacterId != characterId) ? checked((ushort)frame) : basis?.FirstFrame ?? checked((ushort)frame);
        put(payload, 6, ((uint)firstFrame << 16) | (get(payload, 6) & 0xFFFF));
        put(payload, 7, checked((ushort)matrixIndex));
        put(payload, 8, colorIndex);
        put(payload, 9, addIndex);
        if (existing is null) records.Insert(group.End, new EditRecord(LmbTags.PlaceObject, payload));
        else records[existing.RawRecord.Index] = new EditRecord(LmbTags.PlaceObject, payload);
    }

    private static uint storePoolEntry(List<EditRecord> records, uint tag, byte[] entry, uint? preferred,
        LmbMovieDefinition movie, Func<LmbPlaceObjectCommand, uint> reference)
    {
        var pools = records.Where(r => r.Tag == tag).ToArray();
        if (pools.Length > 1) throw new InvalidOperationException("Editing duplicate pools is ambiguous and is not supported.");
        var pool = pools.SingleOrDefault();
        if (pool is null)
        {
            pool = new EditRecord(tag, words(0));
            var firstDefinition = records.FindIndex(r => r.Tag is LmbTags.DefineShape or LmbTags.DefineSprite);
            records.Insert(firstDefinition < 0 ? records.Count : firstDefinition, pool);
        }
        var count = get(pool.Payload, 0);
        for (uint i = 0; i < count; i++)
            if (pool.Payload.AsSpan(checked(4 + (int)i * entry.Length), entry.Length).SequenceEqual(entry)) return i;
        if (preferred is uint slot && slot < count && ordinaryPlacements(movie).Sum(p => tag == LmbTags.ColorTransformPool
            ? (p.ColorMultiplyIndex == slot ? 1 : 0) + (p.ColorAddIndex == slot ? 1 : 0) : reference(p) == slot ? 1 : 0) == 1)
        {
            entry.CopyTo(pool.Payload, checked(4 + (int)slot * entry.Length));
            return slot;
        }
        var expanded = new byte[checked(pool.Payload.Length + entry.Length)];
        pool.Payload.CopyTo(expanded, 0);
        entry.CopyTo(expanded, pool.Payload.Length);
        put(expanded, 0, checked(count + 1));
        pool.Payload = expanded;
        return count;
    }

    private static IEnumerable<LmbPlaceObjectCommand> ordinaryPlacements(LmbMovieDefinition movie)
    {
        foreach (var sprite in movie.Sprites)
        {
            var ordinary = false;
            foreach (var command in sprite.Timeline)
            {
                if (command is LmbShowFrameCommand) ordinary = true;
                else if (command is LmbFrameKeyCommand) ordinary = false;
                else if (ordinary && command is LmbPlaceObjectCommand place) yield return place;
            }
        }
    }

    private static uint addString(List<EditRecord> records, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("A name cannot contain NUL.", nameof(value));
        var pools = records.Where(r => r.Tag == LmbTags.StringPool).ToArray();
        if (pools.Length > 1) throw new InvalidOperationException("Editing duplicate string pools is ambiguous.");
        var pool = pools.SingleOrDefault();
        if (pool is null)
        {
            pool = new EditRecord(LmbTags.StringPool, words(0));
            records.Insert(0, pool);
        }
        var count = get(pool.Payload, 0);
        if (count == 0 && value.Length != 0)
        {
            _ = addString(records, "");
            return addString(records, value);
        }
        var offset = 4;
        for (uint i = 0; i < count; i++)
        {
            var length = checked((int)get(pool.Payload, offset / 4));
            if (Encoding.UTF8.GetString(pool.Payload, offset + 4, length) == value) return i;
            offset += checked(4 + ((length + 4) & ~3));
        }
        var text = Encoding.UTF8.GetBytes(value);
        var expanded = new byte[checked(pool.Payload.Length + 4 + ((text.Length + 4) & ~3))];
        pool.Payload.CopyTo(expanded, 0);
        put(expanded, pool.Payload.Length / 4, (uint)text.Length);
        text.CopyTo(expanded, pool.Payload.Length + 4);
        put(expanded, 0, checked(count + 1));
        pool.Payload = expanded;
        return count;
    }

    private static Dictionary<ushort, LmbPlaceObjectCommand> state(LmbMovieDefinition movie, uint spriteId, int frame)
    {
        var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
        if (frame < 0 || frame >= sprite.DeclaredFrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        var result = new Dictionary<ushort, LmbPlaceObjectCommand>();
        int? current = null;
        foreach (var command in sprite.Timeline)
        {
            if (command is LmbShowFrameCommand show) { current = (int)show.Frame; continue; }
            if (command is LmbFrameKeyCommand) { current = null; continue; }
            if (current is null || current > frame) continue;
            if (command is LmbRemoveObjectCommand remove) result.Remove(remove.Depth);
            if (command is not LmbPlaceObjectCommand place) continue;
            result.TryGetValue(place.Depth, out var previous);
            if (place.Mode == 1) result[place.Depth] = place;
            else if (previous is not null && place.Mode is 2 or 3)
                result[place.Depth] = place with
                {
                    CharacterId = place.Mode == 2 ? previous.CharacterId : place.CharacterId,
                    PlacementId = place.Mode == 2 ? previous.PlacementId : place.PlacementId,
                    FirstFrame = place.Mode == 2 ? previous.FirstFrame : place.FirstFrame,
                    NameStringIndex = place.NameStringIndex == 0 ? previous.NameStringIndex : place.NameStringIndex,
                    BlendMode = place.BlendMode == 0 ? previous.BlendMode : place.BlendMode,
                    ClipDepth = place.ClipDepth == 0 ? previous.ClipDepth : place.ClipDepth,
                    PositionKind = place.PositionKind == ushort.MaxValue ? previous.PositionKind : place.PositionKind,
                    PositionIndex = place.PositionKind == ushort.MaxValue ? previous.PositionIndex : place.PositionIndex,
                    ColorMultiplyIndex = place.ColorMultiplyIndex == uint.MaxValue ? previous.ColorMultiplyIndex : place.ColorMultiplyIndex,
                    ColorAddIndex = place.ColorAddIndex == uint.MaxValue ? previous.ColorAddIndex : place.ColorAddIndex,
                };
        }
        return result;
    }

    private static bool wouldCycle(LmbMovieDefinition movie, uint character, uint target, HashSet<uint> visited)
    {
        if (character == target) return true;
        if (!visited.Add(character)) return false;
        return movie.Sprites.FirstOrDefault(s => s.CharacterId == character)?.Timeline.OfType<LmbPlaceObjectCommand>()
            .Any(p => p.Mode is 1 or 3 && wouldCycle(movie, p.CharacterId, target, visited)) == true;
    }

    private static (int Start, int End) frameGroup(LmbMovieDefinition movie, uint spriteId, int frame)
    {
        var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
        var start = sprite.Timeline.OfType<LmbShowFrameCommand>().Single(s => s.Frame == frame).RawRecord.Index;
        var end = sprite.Timeline.Where(c => c.RawRecord.Index > start && c is LmbShowFrameCommand or LmbFrameKeyCommand)
            .Select(c => c.RawRecord.Index).DefaultIfEmpty(sprite.RawRecord.Index + 1 + sprite.Timeline.Length).Min();
        // Unknown records belong to the raw region too; never insert into the next sprite.
        var nextDefinition = movie.RawFile.Records.Skip(start + 1)
            .FirstOrDefault(r => r.Tag is LmbTags.DefineSprite or LmbTags.DefineShape);
        end = Math.Min(nextDefinition?.Index ?? movie.RawFile.Records.Length,
            sprite.Timeline.Where(c => c.RawRecord.Index > start && c is LmbShowFrameCommand or LmbFrameKeyCommand)
                .Select(c => c.RawRecord.Index).DefaultIfEmpty(nextDefinition?.Index ?? movie.RawFile.Records.Length).Min());
        return (start, end);
    }

    private void rebuildSnapshots(List<EditRecord> records, uint spriteId)
    {
        var movie = definition(records);
        var sprite = movie.Sprites.Single(s => s.CharacterId == spriteId);
        foreach (var key in sprite.Timeline.OfType<LmbFrameKeyCommand>().OrderByDescending(k => k.RawRecord.Index))
        {
            var start = key.RawRecord.Index;
            var end = start + 1;
            while (end < records.Count && records[end].Tag is not (LmbTags.FrameKey or LmbTags.ShowFrame or LmbTags.DefineSprite or LmbTags.DefineShape)) end++;
            var templates = sprite.Timeline.OfType<LmbPlaceObjectCommand>()
                .Where(p => p.RawRecord.Index > start && p.RawRecord.Index < end)
                .GroupBy(p => p.Depth).ToDictionary(g => g.Key, g => g.Last().RawRecord.Payload.ToArray());
            // Preserve actions, labels, unknown tags and their payloads, replacing display state only.
            for (var i = end - 1; i > start; i--)
                if (records[i].Tag is LmbTags.PlaceObject or LmbTags.RemoveObject) records.RemoveAt(i);
            var insertion = start + 1;
            foreach (var place in state(movie, spriteId, checked((int)key.Frame)).Values.OrderBy(p => p.Depth))
            {
                var payload = templates.TryGetValue(place.Depth, out var template) ? template : place.RawRecord.Payload.ToArray();
                put(payload, 0, place.CharacterId);
                put(payload, 1, place.PlacementId);
                put(payload, 3, place.NameStringIndex);
                put(payload, 4, (1u << 16) | place.BlendMode);
                put(payload, 5, ((uint)place.Depth << 16) | place.ClipDepth);
                put(payload, 6, ((uint)place.FirstFrame << 16) | (get(payload, 6) & 0xFFFF));
                put(payload, 7, ((uint)place.PositionKind << 16) | place.PositionIndex);
                put(payload, 8, place.ColorMultiplyIndex);
                put(payload, 9, place.ColorAddIndex);
                records.Insert(insertion++, new EditRecord(LmbTags.PlaceObject, payload));
            }
        }
    }

    private static void repairCounts(List<EditRecord> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Tag == LmbTags.DefineSprite)
            {
                var end = spriteEnd(records, index);
                var labels = (uint)records.GetRange(index + 1, end - index - 1).Count(r => r.Tag == LmbTags.FrameLabel);
                put(record.Payload, 3, labels);
                put(record.Payload, 5, labels);
            }
            if (record.Tag is not (LmbTags.ShowFrame or LmbTags.FrameKey)) continue;
            uint count = 0;
            for (var next = index + 1; next < records.Count; next++)
            {
                var tag = records[next].Tag;
                if (tag is LmbTags.ShowFrame or LmbTags.FrameKey or LmbTags.DefineSprite or LmbTags.DefineShape) break;
                if (tag is LmbTags.PlaceObject or LmbTags.RemoveObject or LmbTags.DoAction) count++;
            }
            put(record.Payload, 1, count);
        }
    }

    private static int spriteEnd(List<EditRecord> records, int start)
    {
        for (var i = start + 1; i < records.Count; i++)
            if (records[i].Tag is LmbTags.DefineSprite or LmbTags.DefineShape) return i;
        return records.Count;
    }

    private LmbMovieDefinition definition(List<EditRecord> records) => read(serialize(records));
    private byte[] serialize(List<EditRecord> records)
    {
        using var output = new MemoryStream();
        output.Write(_original.AsSpan(0, LmbFile.HeaderLength));
        foreach (var record in records)
        {
            output.Write(words(record.Tag, checked((uint)record.Payload.Length / 4)));
            output.Write(record.Payload);
        }
        return output.ToArray();
    }

    private static LmbMovieDefinition read(byte[] bytes) => LmbSemanticReader.Read(LmbFile.Parse(bytes)).Value;
    private static LmbMovieDefinition validate(byte[] bytes)
    {
        var parsed = LmbSemanticReader.Read(LmbFile.Parse(bytes));
        var errors = parsed.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(e => e.Code + ": " + e.Message)));
        return parsed.Value;
    }

    private static bool sameLayout(byte[] original, byte[] edited)
    {
        if (original.Length != edited.Length) return false;
        var a = LmbFile.Parse(original);
        var b = LmbFile.Parse(edited);
        return a.HeaderBytes.SequenceEqual(b.HeaderBytes) && a.Records.Length == b.Records.Length
            && a.Records.Zip(b.Records).All(p => p.First.Tag == p.Second.Tag && p.First.WordCount == p.Second.WordCount);
    }

    private static void validateMatrix(LmbMatrix matrix)
    {
        if (new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.X, matrix.Y }.Any(v => !float.IsFinite(v)))
            throw new ArgumentException("All transform values must be finite.", nameof(matrix));
    }

    private uint nextCharacter()
    {
        var id = checked(Definition.Shapes.Select(s => s.CharacterId).Concat(Definition.Sprites.Select(s => s.CharacterId))
            .Concat(Definition.Texts.Select(t => t.CharacterId)).DefaultIfEmpty(0u).Max() + 1);
        var referenced = Definition.Sprites.SelectMany(s => s.Timeline).OfType<LmbPlaceObjectCommand>()
            .Select(p => p.CharacterId).ToHashSet();
        while (referenced.Contains(id)) id = checked(id + 1);
        if (id == uint.MaxValue) throw new InvalidOperationException("No free character identity.");
        return id;
    }

    private static LmbMatrix interpolate(LmbMatrix a, LmbMatrix b, float t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        float mix(float x, float y) => x + (y - x) * t;
        var ax = MathF.Sqrt(a.M11 * a.M11 + a.M12 * a.M12);
        var bx = MathF.Sqrt(b.M11 * b.M11 + b.M12 * b.M12);
        if (ax < 1e-6f || bx < 1e-6f)
            return new LmbMatrix(mix(a.M11, b.M11), mix(a.M12, b.M12), mix(a.M21, b.M21), mix(a.M22, b.M22), mix(a.X, b.X), mix(a.Y, b.Y));
        var angleA = MathF.Atan2(a.M12, a.M11); var angleB = MathF.Atan2(b.M12, b.M11);
        var delta = MathF.IEEERemainder(angleB - angleA, 2 * MathF.PI);
        var angle = angleA + delta * t;
        var scaleX = mix(ax, bx);
        var scaleY = mix((a.M11 * a.M22 - a.M12 * a.M21) / ax, (b.M11 * b.M22 - b.M12 * b.M21) / bx);
        var shear = mix((a.M11 * a.M21 + a.M12 * a.M22) / ax, (b.M11 * b.M21 + b.M12 * b.M22) / bx);
        var cos = MathF.Cos(angle); var sin = MathF.Sin(angle);
        return new LmbMatrix(cos * scaleX, sin * scaleX, cos * shear - sin * scaleY,
            sin * shear + cos * scaleY, mix(a.X, b.X), mix(a.Y, b.Y));
    }

    private static uint f(float value) => BitConverter.SingleToUInt32Bits(value);
    private static uint get(byte[] payload, int word) => BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(word * 4, 4));
    private static void put(byte[] payload, int word, uint value) => BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(word * 4, 4), value);
    private static byte[] words(params uint[] values)
    {
        var result = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) put(result, i, values[i]);
        return result;
    }

    private sealed class EditRecord(uint tag, byte[] payload)
    {
        public uint Tag { get; } = tag;
        public byte[] Payload { get; set; } = payload;
    }
}

/// <summary>One generated timeline placement for a transactional batch.</summary>
public sealed record LmbPlacementEdit(uint Sprite, int Frame, ushort Depth, uint Character, LmbMatrix Matrix,
    float Opacity = 1, string? Name = null, ushort BlendMode = 0, ushort ClipDepth = 0);
