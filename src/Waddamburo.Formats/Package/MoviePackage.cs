using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Package;

/// <summary>
/// A movie package's <c>movie.json</c> (docs/development/free-movies-plan.md): a movie written by hand or by
/// the editor, as names, layers and keyframes. <see cref="Assemble"/> turns it into the record stream the
/// Lumen reader takes, so a package movie goes through the same parser, validation and player as an
/// archive movie.
/// </summary>
public static class MoviePackage
{
    public const string FileName = "movie.json";

    /// <summary>The assembled movie and its texture files (package-relative), in texture-index order.</summary>
    public sealed record Assembled(byte[] Lmb, ImmutableArray<string> TextureFiles);

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <exception cref="FormatException">The text is not a valid movie; the message says where.</exception>
    public static Assembled Assemble(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, Options);
            return new Assembler().Run(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new FormatException($"{FileName}: {exception.Message}", exception);
        }
    }

    private sealed class Assembler
    {
        private readonly List<string> _strings = [""]; // Index 0 is "no name".
        private readonly List<(float A, float B, float C, float D, float X, float Y)> _matrices = [];
        private readonly List<(short R, short G, short B, short A)> _colours = [];
        private readonly Dictionary<string, uint> _characters = new(StringComparer.Ordinal);
        private readonly List<(uint Tag, uint[] Words)> _records = [];

        public Assembled Run(JsonElement root)
        {
            if (number(root, "format", 0) != 1)
                throw new FormatException($"{FileName}: \"format\" must be 1.");
            var textures = root.TryGetProperty("textures", out var textureMap)
                ? textureMap.EnumerateObject().Select(static entry => (entry.Name, File: entry.Value.GetString() ?? "")).ToArray()
                : [];
            var shapes = root.TryGetProperty("shapes", out var shapeMap) ? shapeMap.EnumerateObject().ToArray() : [];
            var sprites = root.GetProperty("sprites").EnumerateObject().ToArray();
            // Every character gets its id first: a sprite may place one defined after it.
            foreach (var name in shapes.Select(static shape => shape.Name).Concat(sprites.Select(static sprite => sprite.Name)))
                if (!_characters.TryAdd(name, (uint)_characters.Count + 1))
                    throw new FormatException($"{FileName}: two characters are named \"{name}\".");

            var body = new List<(uint Tag, uint[] Words)>();
            foreach (var shape in shapes)
                addShape(body, shape, textures);
            foreach (var sprite in sprites)
                addSprite(body, sprite);

            var rootName = root.GetProperty("root").GetString() ?? "";
            if (!sprites.Any(sprite => sprite.Name == rootName))
                throw new FormatException($"{FileName}: \"root\" names no sprite: \"{rootName}\".");
            _records.Add((LmbTags.MovieProperties,
                [0, 0, 0, _characters[rootName], 0, 0, 0, bits((float)number(root, "frameRate", 60))]));
            _records.Add((LmbTags.ColorTransformPool, [(uint)_colours.Count, .. _colours.SelectMany(static colour => new[]
            {
                (uint)(ushort)colour.R << 16 | (ushort)colour.G, (uint)(ushort)colour.B << 16 | (ushort)colour.A,
            })]));
            _records.Add((LmbTags.MatrixPool, [(uint)_matrices.Count, .. _matrices.SelectMany(static matrix => new[]
            {
                bits(matrix.A), bits(matrix.B), bits(matrix.C), bits(matrix.D), bits(matrix.X), bits(matrix.Y),
            })]));
            _records.AddRange(body);

            using var stream = new MemoryStream();
            stream.Write("LMB\0"u8);
            stream.Write(new byte[LmbFile.HeaderLength - 4]);
            writeRecord(stream, LmbTags.StringPool, stringPool());
            foreach (var (tag, words) in _records)
            {
                var payload = new byte[words.Length * 4];
                for (var index = 0; index < words.Length; index++)
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(index * 4), words[index]);
                writeRecord(stream, tag, payload);
            }
            return new Assembled(stream.ToArray(), [.. textures.Select(static texture => texture.File)]);
        }

        // One textured quad: "rect" [x, y, width, height] in the shape's own space, "uv" [left, top, right, bottom]
        // (the whole texture when left out). Without a texture it is a slot the host draws into (a native fill).
        private void addShape(List<(uint, uint[])> body, JsonProperty shape, (string Name, string File)[] textures)
        {
            var rect = floats(shape.Value.GetProperty("rect"), 4, $"shape \"{shape.Name}\" rect");
            var uv = shape.Value.TryGetProperty("uv", out var uvValue) ? floats(uvValue, 4, $"shape \"{shape.Name}\" uv") : [0, 0, 1, 1];
            uint texture = 0, flags = 0;
            if (shape.Value.TryGetProperty("texture", out var textureName))
            {
                var index = Array.FindIndex(textures, entry => entry.Name == textureName.GetString());
                if (index < 0)
                    throw new FormatException($"{FileName}: shape \"{shape.Name}\" uses a texture that is not listed: \"{textureName}\".");
                (texture, flags) = ((uint)index, 0x00410000);
            }
            float left = rect[0], top = rect[1], right = rect[0] + rect[2], bottom = rect[1] + rect[3];
            body.Add((LmbTags.DefineShape, [_characters[shape.Name], 0, 0, 1]));
            body.Add((LmbTags.ShapeGeometry,
            [
                bits(left), bits(top), bits(uv[0]), bits(uv[1]),
                bits(right), bits(top), bits(uv[2]), bits(uv[1]),
                bits(right), bits(bottom), bits(uv[2]), bits(uv[3]),
                bits(left), bits(bottom), bits(uv[0]), bits(uv[3]),
                texture, flags,
            ]));
        }

        // A sprite is layers: each one character at one depth, placed at its first key, moved on every frame
        // its interpolated values change, removed at "until" (never, when left out).
        private void addSprite(List<(uint, uint[])> body, JsonProperty sprite)
        {
            var frameCount = (int)number(sprite.Value, "frames", 1);
            if (frameCount is < 1 or > ushort.MaxValue)
                throw new FormatException($"{FileName}: sprite \"{sprite.Name}\" needs 1 to 65535 frames.");
            var frames = Enumerable.Range(0, frameCount).Select(static _ => new List<(uint, uint[])>()).ToArray();
            var layerIndex = 0u;
            foreach (var layer in sprite.Value.TryGetProperty("layers", out var layers) ? layers.EnumerateArray() : default)
            {
                var where = $"sprite \"{sprite.Name}\" layer {layerIndex}";
                var characterName = layer.GetProperty("character").GetString() ?? "";
                if (!_characters.TryGetValue(characterName, out var character))
                    throw new FormatException($"{FileName}: {where} places an unknown character: \"{characterName}\".");
                var depth = (uint)number(layer, "depth", layerIndex + 1);
                var name = layer.TryGetProperty("name", out var nameValue) ? text(nameValue.GetString() ?? "") : 0;
                var blend = (layer.TryGetProperty("blend", out var blendValue) ? blendValue.GetString() : "normal") switch
                {
                    "normal" => 0u,
                    "add" => 8u,
                    var other => throw new FormatException($"{FileName}: {where} has an unknown blend: \"{other}\"."),
                };
                var keys = layer.GetProperty("keys").EnumerateArray().Select(Key.Read).OrderBy(static key => key.Frame).ToArray();
                if (keys.Length == 0 || keys[^1].Frame >= frameCount)
                    throw new FormatException($"{FileName}: {where} needs keys inside the sprite's {frameCount} frames.");
                var until = (int)number(layer, "until", frameCount);
                (uint Matrix, uint Colour)? last = null;
                for (var frame = keys[0].Frame; frame < Math.Min(until, frameCount); frame++)
                {
                    var value = Key.At(keys, frame);
                    var now = (matrix(value), colour(value));
                    if (now == last)
                        continue;
                    // Mode 1 places, 2 moves what is there; position kind 0 is a matrix-pool index.
                    frames[frame].Add((LmbTags.PlaceObject,
                    [
                        character, layerIndex + 1, 0, last is null ? name : 0, (last is null ? 1u : 2u) << 16 | blend, depth << 16,
                        (uint)keys[0].Frame << 16, now.Item1, now.Item2, uint.MaxValue, 0, 0,
                    ]));
                    last = now;
                }
                if (until < frameCount)
                    frames[until].Add((LmbTags.RemoveObject, [character, depth << 16]));
                layerIndex++;
            }

            var labels = sprite.Value.TryGetProperty("labels", out var labelMap) ? labelMap.EnumerateObject().ToArray() : [];
            body.Add((LmbTags.DefineSprite, [_characters[sprite.Name], 0, 0, (uint)labels.Length, (uint)frameCount, (uint)labels.Length, 0]));
            foreach (var label in labels)
                body.Add((LmbTags.FrameLabel, [text(label.Name), (uint)label.Value.GetInt32(), 0]));
            for (var frame = 0; frame < frameCount; frame++)
            {
                body.Add((LmbTags.ShowFrame, [(uint)frame, (uint)frames[frame].Count]));
                body.AddRange(frames[frame]);
            }
        }

        private uint matrix(Key key)
        {
            var (sin, cos) = MathF.SinCos(key.Rotation * MathF.PI / 180);
            var value = (cos * key.ScaleX, sin * key.ScaleX, -sin * key.ScaleY, cos * key.ScaleY, key.X, key.Y);
            var index = _matrices.IndexOf(value);
            if (index < 0)
            {
                index = _matrices.Count;
                _matrices.Add(value);
            }
            // ponytail: a placement's matrix index is 16 bits and the search is linear; per-sprite pools or a
            // dictionary when a movie's tweens outgrow either.
            return index <= ushort.MaxValue ? (uint)index
                : throw new FormatException($"{FileName}: more than 65536 different transforms (shorten or split the tweens).");
        }

        private uint colour(Key key)
        {
            // Always an entry, plain white included: a move without one keeps the colour it had.
            var value = (fixed8(key.Red), fixed8(key.Green), fixed8(key.Blue), fixed8(key.Alpha));
            var index = _colours.IndexOf(value);
            if (index < 0)
            {
                index = _colours.Count;
                _colours.Add(value);
            }
            return (uint)index;

            static short fixed8(float value) => (short)MathF.Round(Math.Clamp(value, -127, 127) * 256);
        }

        private uint text(string value)
        {
            var index = _strings.IndexOf(value);
            if (index < 0)
            {
                index = _strings.Count;
                _strings.Add(value);
            }
            return (uint)index;
        }

        private byte[] stringPool()
        {
            using var stream = new MemoryStream();
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)_strings.Count);
            stream.Write(word);
            foreach (var value in _strings)
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteUInt32BigEndian(word, (uint)encoded.Length);
                stream.Write(word);
                stream.Write(encoded);
                stream.WriteByte(0);
                while (stream.Position % 4 != 0)
                    stream.WriteByte(0);
            }
            return stream.ToArray();
        }

        private static void writeRecord(Stream stream, uint tag, byte[] payload)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, tag);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)payload.Length / 4);
            stream.Write(header);
            stream.Write(payload);
        }

        private static uint bits(float value) => BitConverter.SingleToUInt32Bits(value);

        private static double number(JsonElement element, string name, double fallback) =>
            element.TryGetProperty(name, out var value) ? value.GetDouble() : fallback;

        private static float[] floats(JsonElement element, int count, string what)
        {
            var values = element.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
            return values.Length == count ? values : throw new FormatException($"{FileName}: {what} needs {count} numbers.");
        }
    }

    /// <summary>One keyframe of a layer; values run straight to the next key unless <see cref="Hold"/>.</summary>
    private readonly record struct Key(int Frame, float X, float Y, float ScaleX, float ScaleY, float Rotation,
        float Alpha, float Red, float Green, float Blue, bool Hold)
    {
        public static Key Read(JsonElement key)
        {
            float get(string name, float fallback) => key.TryGetProperty(name, out var value) ? value.GetSingle() : fallback;
            var scale = get("scale", 1);
            var tint = key.TryGetProperty("tint", out var tintValue) ? tintValue.EnumerateArray().Select(static value => value.GetSingle()).ToArray() : [1, 1, 1];
            if (tint.Length != 3)
                throw new FormatException($"{FileName}: a key's tint needs 3 numbers.");
            return new Key(key.GetProperty("frame").GetInt32(), get("x", 0), get("y", 0), get("scaleX", scale), get("scaleY", scale),
                get("rotation", 0), get("alpha", 1), tint[0], tint[1], tint[2],
                key.TryGetProperty("hold", out var hold) && hold.GetBoolean());
        }

        public static Key At(Key[] keys, int frame)
        {
            var next = Array.FindIndex(keys, key => key.Frame > frame);
            if (next < 0)
                return keys[^1];
            var start = keys[next - 1];
            var end = keys[next];
            if (start.Hold)
                return start;
            var t = (frame - start.Frame) / (float)(end.Frame - start.Frame);
            float mix(float a, float b) => a + (b - a) * t;
            return start with
            {
                X = mix(start.X, end.X), Y = mix(start.Y, end.Y), ScaleX = mix(start.ScaleX, end.ScaleX), ScaleY = mix(start.ScaleY, end.ScaleY),
                Rotation = mix(start.Rotation, end.Rotation), Alpha = mix(start.Alpha, end.Alpha),
                Red = mix(start.Red, end.Red), Green = mix(start.Green, end.Green), Blue = mix(start.Blue, end.Blue),
            };
        }
    }
}
