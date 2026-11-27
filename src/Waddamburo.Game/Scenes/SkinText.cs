using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Formats.Text;

namespace Waddamburo.Game.Scenes;

/// <summary>
/// A skin's text.json: its fonts (a name for a font file in the skin) and its named text styles, which the text fields
/// of its packages pick ("style": "title") and may change setting by setting (Formats/Text/TextStyleJson).
/// <code>
/// { "format": 1,
///   "fonts": { "pop": "fonts/MochiyPopPOne-Regular.ttf" },
///   "styles": { "title": { "font": "pop", "size": 64, "layers": [ ... ] } } }
/// </code>
/// The font "game" is the engine's own (the player's title font); a style naming no font has it.
/// </summary>
public sealed class SkinText
{
    public const string FileName = "text.json";

    /// <summary>The engine's own font's name.</summary>
    public const string GameFont = "game";

    private static SkinText? s_current;

    private SkinText(IReadOnlyDictionary<string, string> fonts, IReadOnlyDictionary<string, JsonObject> styles)
    {
        Fonts = fonts;
        Styles = styles;
    }

    public static SkinText Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, JsonObject>());

    /// <summary>Font name to its file's full path.</summary>
    public IReadOnlyDictionary<string, string> Fonts { get; }

    /// <summary>Style name to its settings, as written.</summary>
    public IReadOnlyDictionary<string, JsonObject> Styles { get; }

    /// <summary>The skin's text the game draws with now (the bundled skin's, the user's, the one being developed).</summary>
    public static SkinText Current
    {
        get => s_current ?? Empty;
        set
        {
            s_current = value;
            Version++;
        }
    }

    /// <summary>Changes whenever <see cref="Current"/> is set: what was drawn with the one before is drawn again.</summary>
    public static int Version { get; private set; }

    /// <summary>The first root's text.json that reads (a mistake: told, and the next root's), else <see cref="Empty"/>.</summary>
    public static SkinText Find(IEnumerable<string?> roots, Action<string>? problem = null)
    {
        foreach (var root in roots)
        {
            if (root is null || !File.Exists(Path.Combine(root, FileName)))
                continue;
            try
            {
                return Parse(File.ReadAllText(Path.Combine(root, FileName)), root);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                problem?.Invoke($"{Path.Combine(root, FileName)}: {error.Message}");
            }
        }
        return Empty;
    }

    /// <summary>Reads and checks a text.json (font paths from <paramref name="root"/>); a mistake throws <see cref="InvalidDataException"/>.</summary>
    public static SkinText Parse(string json, string root)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new InvalidDataException("text.json is not an object.");
        if (node["format"]?.GetValueKind() != JsonValueKind.Number || node["format"]!.GetValue<int>() != 1)
            throw new InvalidDataException("\"format\": 1 expected.");
        var fonts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, file) in node["fonts"] as JsonObject ?? [])
        {
            if (name == GameFont || file?.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException($"Font \"{name}\": a file name, and not called \"{GameFont}\" (the engine's own).");
            fonts[name] = Path.GetFullPath(Path.Combine(root, file.GetValue<string>()));
        }
        var styles = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, style) in node["styles"] as JsonObject ?? [])
        {
            if (style is not JsonObject settings)
                throw new InvalidDataException($"Style \"{name}\" is not an object.");
            try
            {
                var parsed = TextStyleJson.Parse(settings);
                if (parsed.Font != GameFont && !fonts.ContainsKey(parsed.Font))
                    throw new FormatException($"no font \"{parsed.Font}\" in \"fonts\".");
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException)
            {
                throw new InvalidDataException($"Style \"{name}\": {error.Message}");
            }
            styles[name] = settings;
        }
        return new SkinText(fonts, styles);
    }

    /// <summary>
    /// The style a text field draws in: the field's size, alignment and colour from its movie, under its named style,
    /// under its own settings (<paramref name="settings"/>, the JSON its package gave it; null: none). The font's file is
    /// null for the engine's own. A setting that does not read is left out (the problems panel names it).
    /// </summary>
    public (TextStyle Style, string? FontFile) Resolve(string? settings, float size, char align, uint rgb)
    {
        var style = new JsonObject
        {
            ["size"] = size,
            ["align"] = align switch { 'r' => "right", 'c' => "center", _ => "left" },
            ["layers"] = new JsonArray(new JsonObject { ["color"] = $"#{rgb & 0xffffff:x6}" }),
        };
        if (settings is not null && JsonNode.Parse(settings) is JsonObject own)
        {
            if (own["style"]?.GetValueKind() == JsonValueKind.String && Styles.TryGetValue(own["style"]!.GetValue<string>(), out var named))
                style = TextStyleJson.Merge(style, named);
            style = TextStyleJson.Merge(style, own);
        }
        TextStyle parsed;
        try
        {
            parsed = TextStyleJson.Parse(style);
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
            parsed = new TextStyle(GameFont, size, [TextLayer.Solid(rgb << 8 | 0xff)]);
        }
        return (parsed, Fonts.GetValueOrDefault(parsed.Font));
    }
}
