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

    /// <param name="json">The text of movie.json.</param>
    /// <param name="readScript">Reads a script file of the package by its relative path (movies without scripts need none).</param>
    /// <exception cref="FormatException">The text is not a valid movie, or a script has an error; the message says where.</exception>
    /// <param name="scriptErrors">
    /// Given: a script that does not compile (or cannot be read) is left out of the movie and reported here, so the
    /// rest of the movie still plays (the editor). Null: the first such error is thrown.
    /// </param>
    public static Assembled Assemble(string json, Func<string, string>? readScript = null,
        ICollection<ActionScriptException>? scriptErrors = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, Options);
            return new Assembler(readScript, scriptErrors).Run(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new FormatException($"{FileName}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// The movie's characters in the order of their ids (the first is character 1): shapes, then text fields, then
    /// sprites, each in file order. The editor uses it to tell which entry a clip on stage comes from.
    /// </summary>
    public static IReadOnlyList<string> CharacterNames(JsonElement root) =>
    [
        .. CharacterGroups.SelectMany(group => root.TryGetProperty(group, out var map)
            ? map.EnumerateObject().Select(static entry => entry.Name) : []),
    ];

    private static readonly string[] CharacterGroups = ["shapes", "texts", "sprites"];

    /// <summary>The ways a key's values can run to the next key ("ease"; "linear" when left out).</summary>
    public static readonly string[] Eases = ["linear", "in", "out", "inOut"];

    /// <summary>
    /// How far along the values are at fraction <paramref name="t"/> (0..1) of the way from a key to the next:
    /// "in" starts slowly, "out" ends slowly, "inOut" both.
    /// </summary>
    public static double Ease(string? ease, double t) => ease switch
    {
        null or "linear" => t,
        "in" => t * t,
        "out" => 1 - (1 - t) * (1 - t),
        "inOut" => t < .5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t),
        _ => throw new FormatException($"{FileName}: unknown ease \"{ease}\" (linear, in, out, inOut)."),
    };

    /// <summary>The parsing options of movie.json (comments and trailing commas allowed).</summary>
    public static JsonDocumentOptions ReadOptions => Options;

    private sealed class Assembler(Func<string, string>? readScript, ICollection<ActionScriptException>? scriptErrors)
    {
        private readonly List<byte[]> _actions = [];
        private readonly List<float[]> _bounds = [];
        private readonly List<string> _strings = [""]; // Index 0 is "no name".
        private readonly List<(float A, float B, float C, float D, float X, float Y)> _matrices = [];
        private readonly List<(short R, short G, short B, short A)> _colours = [];
        private readonly Dictionary<string, uint> _characters = new(StringComparer.Ordinal);
        private readonly List<(uint Tag, uint[] Words)> _records = [];

        private int _format;

        public Assembled Run(JsonElement root)
        {
            _format = (int)number(root, "format", 0);
            if (_format is not (1 or 2))
                throw new FormatException($"{FileName}: \"format\" must be 1 or 2.");
            var textures = root.TryGetProperty("textures", out var textureMap)
                ? textureMap.EnumerateObject().Select(static entry => (entry.Name, File: entry.Value.GetString() ?? "")).ToArray()
                : [];
            var shapes = root.TryGetProperty("shapes", out var shapeMap) ? shapeMap.EnumerateObject().ToArray() : [];
            var texts = root.TryGetProperty("texts", out var textMap) ? textMap.EnumerateObject().ToArray() : [];
            var sprites = root.GetProperty("sprites").EnumerateObject().ToArray();
            // Every character gets its id first: a sprite may place one defined after it.
            foreach (var name in CharacterNames(root))
                if (!_characters.TryAdd(name, (uint)_characters.Count + 1))
                    throw new FormatException($"{FileName}: two characters are named \"{name}\".");

            var body = new List<(uint Tag, uint[] Words)>();
            var nextCharacter = (uint)_characters.Count + 1;
            // "classes": scripts that run once before anything is placed (class definitions, registerClass), each as
            // a one-frame "__Packages." sprite, which is how the player finds such code in the game's movies.
            foreach (var file in root.TryGetProperty("classes", out var classes) ? classes.EnumerateArray() : default)
            {
                var path = file.GetString() ?? "";
                body.Add((LmbTags.DefineSprite, [nextCharacter++, 0, text($"__Packages.{path}"), 0, 1, 0, 0]));
                body.Add((LmbTags.ShowFrame, [0, 1]));
                body.Add((LmbTags.DoAction, [script(path), 0]));
            }
            foreach (var shape in shapes)
                addShape(body, shape, textures);
            // A text field: a box a script fills through its "text" member (clip.field.text = "12"), drawn by the
            // engine's own text renderer in the game's font. "size" in pixels, "align" left / center / right,
            // "color" "#rrggbb".
            foreach (var field in texts)
            {
                var rect = floats(field.Value.GetProperty("rect"), 4, $"text \"{field.Name}\" rect");
                var align = field.Value.TryGetProperty("align", out var alignValue) ? alignValue.GetString() : "left";
                var colour = field.Value.TryGetProperty("color", out var colourValue) ? colourValue.GetString() ?? "" : "#000000";
                if (align is not ("left" or "center" or "right") || colour.Length != 7 || colour[0] != '#' || !colour[1..].All(Uri.IsHexDigit))
                    throw new FormatException($"{FileName}: text \"{field.Name}\" needs align left, center or right and a color like \"#ffcc00\".");
                var words = new uint[16];
                words[0] = _characters[field.Name];
                words[2] = text($"<p align=\"{align}\"><font color=\"{colour}\"></font></p>");
                words[5] = (uint)_bounds.Count;
                words[11] = bits((float)number(field.Value, "size", 24));
                _bounds.Add([rect[0], rect[1], rect[0] + rect[2], rect[1] + rect[3]]);
                body.Add((LmbTags.DefineText, words));
            }
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
            if (_actions.Count > 0)
                _actionPool = actionPool();
            if (_bounds.Count > 0)
                _records.Add((LmbTags.BoundsPool, [(uint)_bounds.Count, .. _bounds.SelectMany(static box => box.Select(bits))]));
            _records.AddRange(body);

            using var stream = new MemoryStream();
            stream.Write("LMB\0"u8);
            stream.Write(new byte[LmbFile.HeaderLength - 4]);
            writeRecord(stream, LmbTags.StringPool, stringPool());
            if (_actionPool is not null)
                writeRecord(stream, LmbTags.ActionPool, _actionPool);
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
                    "multiply" => 3u,
                    "screen" => 4u,
                    "add" => 8u,
                    var other => throw new FormatException($"{FileName}: {where} has an unknown blend: \"{other}\"."),
                };
                var keys = layer.GetProperty("keys").EnumerateArray().Select(key => Key.Read(key, _format == 1)).OrderBy(static key => key.Frame).ToArray();
                if (keys.Length == 0 || keys[^1].Frame >= frameCount)
                    throw new FormatException($"{FileName}: {where} needs keys inside the sprite's {frameCount} frames.");
                var until = (int)number(layer, "until", frameCount);
                // "masks": n: this layer is not drawn; its shape clips the n layers above it (the next n depths).
                var clip = (uint)number(layer, "masks", 0) is > 0 and var masked ? depth + masked : 0;
                if (clip > ushort.MaxValue)
                    throw new FormatException($"{FileName}: {where} masks too many layers.");
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
                        character, layerIndex + 1, 0, last is null ? name : 0, (last is null ? 1u : 2u) << 16 | blend, depth << 16 | clip,
                        (uint)keys[0].Frame << 16, now.Item1, now.Item2, uint.MaxValue, 0, 0,
                    ]));
                    last = now;
                }
                if (until < frameCount)
                    frames[until].Add((LmbTags.RemoveObject, [character, depth << 16]));
                layerIndex++;
            }

            var labels = sprite.Value.TryGetProperty("labels", out var labelMap) ? labelMap.EnumerateObject().ToArray() : [];
            // "scripts": frame (a number or one of the labels) -> script file, run when the frame is entered,
            // after the frame's placements.
            foreach (var entry in sprite.Value.TryGetProperty("scripts", out var scripts) ? scripts.EnumerateObject() : default)
            {
                var frame = int.TryParse(entry.Name, out var number) ? number
                    : labels.FirstOrDefault(label => label.Name == entry.Name) is { Value.ValueKind: JsonValueKind.Number } named ? named.Value.GetInt32()
                    : throw new FormatException($"{FileName}: sprite \"{sprite.Name}\" has a script on an unknown label: \"{entry.Name}\".");
                if (frame < 0 || frame >= frameCount)
                    throw new FormatException($"{FileName}: sprite \"{sprite.Name}\" has a script outside its {frameCount} frames.");
                frames[frame].Add((LmbTags.DoAction, [script(entry.Value.GetString() ?? ""), 0]));
            }
            // "export": the linkage name Object.registerClass binds a class to.
            var export = sprite.Value.TryGetProperty("export", out var exportName) ? text(exportName.GetString() ?? "") : 0;
            body.Add((LmbTags.DefineSprite, [_characters[sprite.Name], 0, export, (uint)labels.Length, (uint)frameCount, (uint)labels.Length, 0]));
            foreach (var label in labels)
                body.Add((LmbTags.FrameLabel, [text(label.Name), (uint)label.Value.GetInt32(), 0]));
            for (var frame = 0; frame < frameCount; frame++)
            {
                body.Add((LmbTags.ShowFrame, [(uint)frame, (uint)frames[frame].Count]));
                body.AddRange(frames[frame]);
            }
        }

        private uint matrix(Pose key)
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

        private uint colour(Pose key)
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

        private byte[]? _actionPool;

        // Compiles a script file (once per use: a file on two frames is two actions) and gives its action index.
        private uint script(string path)
        {
            if (readScript is null)
                throw new FormatException($"{FileName}: the movie has scripts but no script files were given.");
            try
            {
                _actions.Add(ActionScript.Compile(readScript(path), path, value => checked((ushort)text(value))));
            }
            catch (Exception exception) when (scriptErrors is not null && exception is ActionScriptException or IOException)
            {
                scriptErrors.Add(exception as ActionScriptException ?? new ActionScriptException(path, 1, 1, "The file could not be read."));
                _actions.Add([0x00]); // An empty script: End.
            }
            return (uint)_actions.Count - 1;
        }

        private byte[] actionPool()
        {
            using var stream = new MemoryStream();
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)_actions.Count);
            stream.Write(word);
            foreach (var action in _actions)
            {
                BinaryPrimitives.WriteUInt32BigEndian(word, (uint)action.Length);
                stream.Write(word);
                stream.Write(action);
                while (stream.Position % 4 != 0)
                    stream.WriteByte(0);
            }
            return stream.ToArray();
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

    /// <summary>
    /// One keyframe of a layer. Format 2: it holds the channels it names and no others; a channel runs from one key
    /// that names it to the next that does (by the first one's ease, or held), whatever keys lie between. Format 1:
    /// every key holds every channel, the ones left out at their defaults.
    /// </summary>
    private readonly record struct Key(int Frame, float? X, float? Y, float? ScaleX, float? ScaleY, float? Rotation,
        float? Alpha, float? Red, float? Green, float? Blue, bool Hold, string? Ease)
    {
        public static Key Read(JsonElement key, bool whole)
        {
            float? get(string name, float fallback) => key.TryGetProperty(name, out var value) ? value.GetSingle() : whole ? fallback : null;
            float? scale = key.TryGetProperty("scale", out var both) ? both.GetSingle() : null;
            var tint = key.TryGetProperty("tint", out var tintValue) ? tintValue.EnumerateArray().Select(static value => (float?)value.GetSingle()).ToArray()
                : whole ? [1, 1, 1] : [null, null, null];
            if (tint.Length != 3)
                throw new FormatException($"{FileName}: a key's tint needs 3 numbers.");
            return new Key(key.GetProperty("frame").GetInt32(), get("x", 0), get("y", 0), get("scaleX", scale ?? 1) ?? scale, get("scaleY", scale ?? 1) ?? scale,
                get("rotation", 0), get("alpha", 1), tint[0], tint[1], tint[2],
                key.TryGetProperty("hold", out var hold) && hold.GetBoolean(),
                key.TryGetProperty("ease", out var ease) ? ease.GetString() : null);
        }

        /// <summary>The layer's values on a frame: every channel worked out by itself.</summary>
        public static Pose At(Key[] keys, int frame)
        {
            float channel(Func<Key, float?> of, float fallback)
            {
                Key? start = null, end = null;
                foreach (var key in keys)
                {
                    if (of(key) is null) continue;
                    if (key.Frame <= frame) start = key;
                    else { end = key; break; }
                }
                if (start is not { } from)
                    return end is { } first ? of(first)!.Value : fallback; // before its first key: already there
                if (end is not { } to || from.Hold)
                    return of(from)!.Value;
                var t = (float)MoviePackage.Ease(from.Ease, (frame - from.Frame) / (double)(to.Frame - from.Frame));
                return of(from)!.Value + (of(to)!.Value - of(from)!.Value) * t;
            }
            return new(channel(static key => key.X, 0), channel(static key => key.Y, 0), channel(static key => key.ScaleX, 1),
                channel(static key => key.ScaleY, 1), channel(static key => key.Rotation, 0), channel(static key => key.Alpha, 1),
                channel(static key => key.Red, 1), channel(static key => key.Green, 1), channel(static key => key.Blue, 1));
        }
    }

    private readonly record struct Pose(float X, float Y, float ScaleX, float ScaleY, float Rotation, float Alpha, float Red, float Green, float Blue);
}
