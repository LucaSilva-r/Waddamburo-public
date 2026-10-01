using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Game.Patching;

/// <summary>
/// Edits a decoded movie in memory before it is uploaded and played (a Lumen patch): the user's files
/// stay untouched. Edits work on the movie's own model: strings, textures, shapes, sprites and their
/// timelines; <see cref="Commit"/> replaces the content's definition and textures.
/// </summary>
public sealed class LumenMovieEditor
{
    private readonly LumenMovieContent _content;
    private readonly List<LmbString> _strings;
    private readonly List<LmbColorTransform> _colors;
    private readonly List<LumenTextureContent> _textures;
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, LmbSpriteDefinition> _sprites;
    private readonly List<uint> _spriteOrder;
    private uint _nextCharacter;

    public LumenMovieEditor(LumenMovieContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        var movie = content.Definition;
        _strings = [.. movie.Strings];
        _colors = [.. movie.ColorTransforms];
        _textures = [.. content.Textures];
        _shapes = movie.Shapes.ToDictionary(static shape => shape.CharacterId);
        _sprites = movie.Sprites.ToDictionary(static sprite => sprite.CharacterId);
        _spriteOrder = [.. movie.Sprites.Select(static sprite => sprite.CharacterId)];
        _nextCharacter = movie.Shapes.Select(static shape => shape.CharacterId)
            .Concat(movie.Sprites.Select(static sprite => sprite.CharacterId))
            .Concat(movie.Texts.Select(static text => text.CharacterId))
            .DefaultIfEmpty(0u).Max() + 1;
    }

    /// <summary>The string's index in the pool, added when missing.</summary>
    public uint AddString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var index = _strings.FindIndex(entry => entry.Value == value);
        if (index >= 0)
            return (uint)index;
        _strings.Add(new LmbString(_strings.Count, value, -1));
        return (uint)(_strings.Count - 1);
    }

    /// <summary>A copy of texture <paramref name="source"/> multiplied by <paramref name="tint"/>; its index.</summary>
    public uint TintTexture(uint source, (byte R, byte G, byte B) tint)
    {
        var texture = _textures[checked((int)source)];
        if (texture.Rgba8.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Texture {source} has no decoded pixels to tint.");
        var pixels = texture.Rgba8.ToArray();
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(pixels[index] * tint.R / 255);
            pixels[index + 1] = (byte)(pixels[index + 1] * tint.G / 255);
            pixels[index + 2] = (byte)(pixels[index + 2] * tint.B / 255);
        }
        _textures.Add(texture with { Index = _textures.Count, Rgba8 = [.. pixels], Bc7 = default });
        return (uint)(_textures.Count - 1);
    }

    private readonly Dictionary<uint, (uint Body, uint Light)> _split = [];
    private LmbRemoveObjectCommand? _removeTemplate;

    /// <summary>
    /// Texture <paramref name="source"/> split for recolouring: each pixel read as shade × (base colour
    /// mixed with white), the base colour being <paramref name="reference"/>'s most saturated one (one
    /// reference for a whole set of art keeps its pieces matching). Body = shade × colour
    /// share (grey, to be multiplied by a new colour), light = shade × white share (grey, added on top):
    /// body × colour + light redraws the art in any colour with its highlights and shading.
    /// </summary>
    public (uint Body, uint Light) SplitForRecolour(uint source, uint reference)
    {
        if (_split.TryGetValue(source, out var known))
            return known;
        var texture = _textures[checked((int)source)];
        if (texture.Rgba8.IsDefaultOrEmpty || _textures[checked((int)reference)].Rgba8.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Texture {source} or {reference} has no decoded pixels to split.");
        var pixels = texture.Rgba8.AsSpan();
        var (baseSaturation, baseValue) = (0f, 1f);
        var best = 0f;
        var referencePixels = _textures[checked((int)reference)].Rgba8.AsSpan();
        for (var index = 0; index < referencePixels.Length; index += 4)
        {
            if (referencePixels[index + 3] < 250)
                continue;
            var (saturation, value) = saturationValue(referencePixels, index);
            if (value > 0.4f && saturation * value > best)
                (best, baseSaturation, baseValue) = (saturation * value, saturation, value);
        }
        var body = new byte[pixels.Length];
        var light = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var (saturation, value) = saturationValue(pixels, index);
            var colourShare = baseSaturation > 0 ? Math.Min(1, saturation / baseSaturation) : 0;
            var whiteShare = 1 - colourShare;
            var shade = Math.Min(1, value / Math.Max(1e-4f, baseValue * colourShare + whiteShare));
            var b = (byte)Math.Round(shade * colourShare * 255);
            var l = (byte)Math.Round(shade * whiteShare * 255);
            body[index] = body[index + 1] = body[index + 2] = b;
            light[index] = light[index + 1] = light[index + 2] = l;
            body[index + 3] = light[index + 3] = pixels[index + 3];
        }
        _textures.Add(texture with { Index = _textures.Count, Rgba8 = [.. body], Bc7 = default });
        _textures.Add(texture with { Index = _textures.Count, Rgba8 = [.. light], Bc7 = default });
        return _split[source] = ((uint)(_textures.Count - 2), (uint)(_textures.Count - 1));
    }

    private static (float Saturation, float Value) saturationValue(ReadOnlySpan<byte> pixels, int index)
    {
        var max = Math.Max(pixels[index], Math.Max(pixels[index + 1], pixels[index + 2])) / 255f;
        var min = Math.Min(pixels[index], Math.Min(pixels[index + 1], pixels[index + 2])) / 255f;
        return (max > 0 ? (max - min) / max : 0, max);
    }

    /// <summary>A colour-pool entry (256 = 1.0 per channel); its index (a placement's colour multiply).</summary>
    public uint AddColorTransform(short red, short green, short blue, short alpha = 256)
    {
        _colors.Add(new LmbColorTransform(red, green, blue, alpha));
        return (uint)(_colors.Count - 1);
    }

    /// <summary>
    /// A copy of character <paramref name="character"/> with its textures handled by <paramref name="rule"/>
    /// (null: unchanged): a sprite is copied with every child it places, as deep as a rule applies.
    /// <see cref="TextureRule.Tint"/> multiplies the colour of the placements of shapes drawing that texture;
    /// <see cref="TextureRule.Fill"/> turns those geometries into the host surface slot named (the
    /// placement takes the slot's name); <see cref="TextureRule.Hide"/> drops them. Returns the character
    /// itself when no rule applies anywhere in it.
    /// </summary>
    public uint Clone(uint character, Func<uint, TextureRule?> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return clone(character, rule, []).Character;
    }

    private (uint Character, TextureRule.Tint? Tint, string? Fill) clone(uint character, Func<uint, TextureRule?> rule,
        Dictionary<uint, (uint, TextureRule.Tint?, string?)> done)
    {
        if (done.TryGetValue(character, out var known))
            return known;
        if (_shapes.TryGetValue(character, out var shape))
        {
            var rules = shape.Geometry.Select(geometry => rule(geometry.TextureIndex)).ToArray();
            if (rules.OfType<TextureRule.Recolour>().FirstOrDefault() is { } recolour)
                return done[character] = (layered(shape, rules, recolour), null, null);
            var tint = rules.OfType<TextureRule.Tint>().FirstOrDefault();
            var fill = rules.OfType<TextureRule.Fill>().Select(static fill => fill.Slot).FirstOrDefault();
            if (!rules.Any(static rule => rule is TextureRule.Fill or TextureRule.Hide))
                return done[character] = (character, tint, null);
            var id = _nextCharacter++;
            _shapes[id] = shape with
            {
                CharacterId = id,
                Geometry = [.. shape.Geometry.Zip(rules)
                    .Where(static pair => pair.Second is not TextureRule.Hide)
                    // A geometry whose flags' high half is zero draws the host surface of its slot.
                    .Select(static pair => pair.Second is TextureRule.Fill ? pair.First with { Flags = pair.First.Flags & 0xFFFF } : pair.First)],
                DeclaredGeometryCount = (uint)shape.Geometry.Zip(rules).Count(static pair => pair.Second is not TextureRule.Hide),
            };
            return done[character] = (id, tint, fill);
        }
        if (!_sprites.TryGetValue(character, out var sprite))
            return done[character] = (character, null, null);
        done[character] = (character, null, null); // cycles keep the original
        var timeline = sprite.Timeline.Select(command =>
            command is LmbPlaceObjectCommand place ? replace(place, rule, done) : command).ToImmutableArray();
        if (timeline.SequenceEqual(sprite.Timeline))
            return (character, null, null);
        var copy = _nextCharacter++;
        // A copy is not the class the original sprite exports (its script would bind twice).
        _sprites[copy] = sprite with
        {
            CharacterId = copy,
            Timeline = timeline,
            UninterpretedHeaderWords = sprite.UninterpretedHeaderWords.Length >= 2
                ? sprite.UninterpretedHeaderWords.SetItem(1, 0) : sprite.UninterpretedHeaderWords,
        };
        _spriteOrder.Add(copy);
        return done[character] = (copy, null, null);
    }

    /// <summary>
    /// Adds label <paramref name="label"/> to sprite <paramref name="sprite"/>: a copy of the frames
    /// <paramref name="from"/> spans (up to the next label), what it places cloned through
    /// <paramref name="rule"/> (see <see cref="Clone"/>). The copy starts at the source's nearest
    /// keyframe, so it replays exactly as the original does.
    /// </summary>
    public void CloneLabel(uint sprite, string from, string label, Func<uint, TextureRule?> rule)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(rule);
        var done = new Dictionary<uint, (uint, TextureRule.Tint?, string?)>();
        var definition = _sprites[sprite];
        var labels = definition.Timeline.OfType<LmbFrameLabelCommand>()
            .Select(command => (Name: _strings[(int)command.StringIndex].Value, Frame: (int)command.Frame)).ToArray();
        var start = labels.Single(entry => entry.Name == from).Frame;
        var end = labels.Select(static entry => entry.Frame).Where(frame => frame > start)
            .DefaultIfEmpty((int)definition.DeclaredFrameCount).Min();
        var keyFrames = definition.Timeline.OfType<LmbFrameKeyCommand>().Select(static key => (int)key.Frame).ToHashSet();
        var first = keyFrames.Where(frame => frame <= start).DefaultIfEmpty(start).Max();
        var offset = (int)definition.DeclaredFrameCount - first;
        var added = new List<LmbTimelineCommand>();
        var copying = false;
        // The movie may reach the copy by playing forward (from whatever frame came last, the last label's
        // leftovers on screen) rather than by seeking to its keyframe: the copy's first frame clears every
        // depth and places the keyframe's display list itself.
        var keyCommands = new List<LmbTimelineCommand>();
        var inKey = false;
        foreach (var command in definition.Timeline)
        {
            if (command is LmbShowFrameCommand or LmbFrameKeyCommand)
                inKey = command is LmbFrameKeyCommand key && key.Frame == first;
            else if (inKey && command is not LmbFrameLabelCommand)
                keyCommands.Add(command is LmbPlaceObjectCommand place ? replace(place, rule, done) : command);
        }
        var depths = definition.Timeline.OfType<LmbPlaceObjectCommand>().Select(static place => place.Depth).Distinct().ToArray();
        var template = _removeTemplate ??= _sprites.Values.SelectMany(static sprite => sprite.Timeline)
            .OfType<LmbRemoveObjectCommand>().First();
        var clearing = false;
        foreach (var command in definition.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show:
                    copying = show.Frame >= first && show.Frame < end;
                    clearing = copying && show.Frame == first && keyCommands.Count > 0;
                    if (copying)
                        added.Add(show with { Frame = (uint)(show.Frame + offset) });
                    if (clearing)
                    {
                        added.AddRange(depths.Select(depth => template with { CharacterId = 0, PackedDepth = (uint)depth << 16 }));
                        added.AddRange(keyCommands);
                    }
                    continue;
                case LmbFrameKeyCommand key:
                    copying = key.Frame >= first && key.Frame < end;
                    if (copying)
                        added.Add(key with { Frame = (uint)(key.Frame + offset) });
                    continue;
                case LmbFrameLabelCommand:
                    continue;
            }
            // The first frame's placements come from its keyframe above; its scripts (stop()) stay.
            if (copying && (!clearing || command is LmbDoActionCommand))
                added.Add(command is LmbPlaceObjectCommand place ? replace(place, rule, done) : command);
        }
        var labelTemplate = definition.Timeline.OfType<LmbFrameLabelCommand>().First();
        added.Add(labelTemplate with { StringIndex = AddString(label), Frame = (uint)(start + offset) });
        _sprites[sprite] = definition with
        {
            Timeline = [.. definition.Timeline, .. added],
            DeclaredFrameCount = (uint)(end + offset),
            DeclaredLabelCount = definition.DeclaredLabelCount + 1,
        };
    }

    /// <summary>The sprites that have a frame labelled <paramref name="label"/>.</summary>
    public IEnumerable<uint> SpritesWithLabel(string label) => _sprites.Values
        .Where(sprite => sprite.Timeline.OfType<LmbFrameLabelCommand>()
            .Any(command => command.StringIndex < _strings.Count && _strings[(int)command.StringIndex].Value == label))
        .Select(static sprite => sprite.CharacterId)
        .ToArray();

    /// <summary>The characters sprite <paramref name="sprite"/> places from its label <paramref name="label"/> to the next.</summary>
    public IReadOnlySet<uint> PlacedAt(uint sprite, string label)
    {
        var definition = _sprites[sprite];
        var labels = definition.Timeline.OfType<LmbFrameLabelCommand>()
            .Select(command => (Name: _strings[(int)command.StringIndex].Value, Frame: (int)command.Frame)).ToArray();
        var start = labels.Single(entry => entry.Name == label).Frame;
        var end = labels.Select(static entry => entry.Frame).Where(frame => frame > start)
            .DefaultIfEmpty((int)definition.DeclaredFrameCount).Min();
        var placed = new HashSet<uint>();
        int? frame = null;
        foreach (var command in definition.Timeline)
        {
            frame = command switch
            {
                LmbShowFrameCommand show => (int)show.Frame,
                LmbFrameKeyCommand key => (int)key.Frame,
                _ => frame,
            };
            if (command is LmbPlaceObjectCommand place && frame >= start && frame < end)
                placed.Add(place.CharacterId);
        }
        return placed;
    }

    // A recoloured shape: a one-frame sprite drawing its body layer multiplied by the colour entry and its
    // light layer added on top (Flash blend 8), in the shape's place.
    private uint layered(LmbShapeDefinition shape, TextureRule?[] rules, TextureRule.Recolour recolour)
    {
        LmbShapeDefinition layer(Func<uint, uint> texture)
        {
            var id = _nextCharacter++;
            return _shapes[id] = shape with
            {
                CharacterId = id,
                Geometry = [.. shape.Geometry.Zip(rules).Select(pair => pair.Second is TextureRule.Recolour
                    ? pair.First with { TextureIndex = texture(pair.First.TextureIndex) } : pair.First)],
            };
        }
        var body = layer(texture => SplitForRecolour(texture, recolour.Reference).Body);
        var light = layer(texture => SplitForRecolour(texture, recolour.Reference).Light);
        var template = _sprites.Values.First(static sprite => sprite.Timeline.OfType<LmbPlaceObjectCommand>().Any()
            && sprite.Timeline.OfType<LmbShowFrameCommand>().Any());
        var show = template.Timeline.OfType<LmbShowFrameCommand>().First();
        var place = template.Timeline.OfType<LmbPlaceObjectCommand>().First() with
        {
            NameStringIndex = 0,
            Mode = 1,
            FirstFrame = 0,
            PositionKind = ushort.MaxValue,
            PositionIndex = 0,
            ColorMultiplyIndex = uint.MaxValue,
            ColorAddIndex = uint.MaxValue,
            ClipDepth = 0,
        };
        var id = _nextCharacter++;
        _sprites[id] = template with
        {
            CharacterId = id,
            DeclaredFrameCount = 1,
            DeclaredLabelCount = 0,
            UninterpretedHeaderWords = template.UninterpretedHeaderWords.Length >= 2
                ? template.UninterpretedHeaderWords.SetItem(1, 0) : template.UninterpretedHeaderWords,
            Timeline =
            [
                show with { Frame = 0, DeclaredCommandCount = 2 },
                place with { CharacterId = body.CharacterId, Depth = 1, PlacementId = 1, BlendMode = 0, ColorMultiplyIndex = recolour.ColorIndex },
                place with { CharacterId = light.CharacterId, Depth = 2, PlacementId = 2, BlendMode = 8 },
            ],
        };
        _spriteOrder.Add(id);
        return id;
    }

    // A placement of the cloned character: its tint (colour multiply) and fill slot (instance name) applied.
    private LmbPlaceObjectCommand replace(LmbPlaceObjectCommand place, Func<uint, TextureRule?> rule,
        Dictionary<uint, (uint, TextureRule.Tint?, string?)> done)
    {
        var (child, tint, fill) = clone(place.CharacterId, rule, done);
        return place with
        {
            CharacterId = child,
            ColorMultiplyIndex = tint?.ColorIndex ?? place.ColorMultiplyIndex,
            ColorAddIndex = tint is { AddIndex: not uint.MaxValue } ? tint.AddIndex : place.ColorAddIndex,
            NameStringIndex = fill is null ? place.NameStringIndex : AddString(fill),
        };
    }

    public void Commit()
    {
        var movie = _content.Definition with
        {
            Strings = [.. _strings],
            ColorTransforms = [.. _colors],
            Shapes = [.. _shapes.Values.OrderBy(static shape => shape.CharacterId)],
            Sprites = [.. _spriteOrder.Select(id => _sprites[id])],
        };
        _content.Replace(movie, [.. _textures]);
    }
}

/// <summary>What a cloned character does with one texture (see <see cref="LumenMovieEditor.Clone"/>).</summary>
public abstract record TextureRule
{
    private TextureRule()
    {
    }

    /// <summary>
    /// Its shapes' placements take colour-pool entries <paramref name="ColorIndex"/> as their multiply and
    /// <paramref name="AddIndex"/> (none: unchanged) as their add.
    /// </summary>
    public sealed record Tint(uint ColorIndex, uint AddIndex = uint.MaxValue) : TextureRule;

    /// <summary>Its geometries draw the host surface of slot <paramref name="Slot"/> (LumenPlayer.SetNativeFill).</summary>
    public sealed record Fill(string Slot) : TextureRule;

    /// <summary>
    /// Its geometries are redrawn in the colour of colour-pool entry <paramref name="ColorIndex"/> (a
    /// multiply), keeping their highlights and shading (see <see cref="LumenMovieEditor.SplitForRecolour"/>,
    /// against texture <paramref name="Reference"/>'s colour).
    /// </summary>
    public sealed record Recolour(uint ColorIndex, uint Reference) : TextureRule;

    /// <summary>Its geometries are not drawn.</summary>
    public sealed record Hide : TextureRule;
}

