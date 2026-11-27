using System.Text.Json;

namespace Waddamburo.Game.Sound;

/// <summary>
/// A skin's own sounds: a folder with <c>sounds.json</c> and the files it names (any format the engine decodes).
/// A name it does not have is asked of the fallback sound set supplied to <see cref="Load"/> (the next skin down: ours, then the game's).
/// </summary>
/// <remarks>
/// <code>
/// {
///   "tones": ["Taiko", "High taiko"],
///   "sounds": {
///     "drum.don":       { "file": "don.wav" },
///     "drum.don@tone1": { "file": "don.wav", "pitch": 700 },
///     "drum.don@side0": { "file": "don.wav", "pan": -30 },
///     "banner.fullCombo": [ { "file": "fanfare.ogg" }, { "file": "cheer.ogg", "bus": "voice" } ]
///   }
/// }
/// </code>
/// A request looks for, in order: <c>name@mode</c>, <c>name@tone{t}@side{s}</c>, <c>name@tone{t}</c>,
/// <c>name@side{s}</c>, <c>name@{variant}</c>, <c>name</c>. An entry has <c>file</c> (inside the folder), and may set
/// <c>bus</c> (music, menu, voice, drum, coin; by default drum for drum.* and click.*, music for music.*, coin for
/// coin.*, voice for names with "voice", else menu), <c>gain</c>, <c>pan</c> (degrees, negative left), <c>pitch</c>
/// (cents), <c>delay</c> (ms of silence first).
/// </remarks>
public sealed class SoundPack : ISoundSet
{
    private readonly Dictionary<string, IReadOnlyList<SoundCue>> _sounds;
    private readonly IReadOnlyList<string>? _tones;
    private readonly ISoundSet? _fallback;

    private SoundPack(Dictionary<string, IReadOnlyList<SoundCue>> sounds, IReadOnlyList<string>? tones, ISoundSet? fallback) =>
        (_sounds, _tones, _fallback) = (sounds, tones, fallback);

    /// <summary>The pack in <paramref name="directory"/>; null when it has no sounds.json.</summary>
    public static SoundPack? Load(string directory, ISoundSet? fallback = null)
    {
        var root = Path.GetFullPath(directory);
        var file = Path.Combine(root, "sounds.json");
        if (!File.Exists(file))
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var tones = document.RootElement.TryGetProperty("tones", out var list)
            ? list.EnumerateArray().Select(static tone => tone.GetString() ?? "").ToArray() : null;
        var sounds = new Dictionary<string, IReadOnlyList<SoundCue>>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("sounds", out var entries))
            foreach (var entry in entries.EnumerateObject())
            {
                var name = entry.Name.Split('@')[0];
                sounds[entry.Name] = entry.Value.ValueKind == JsonValueKind.Array
                    ? [.. entry.Value.EnumerateArray().Select(cue => read(root, name, cue))]
                    : [read(root, name, entry.Value)];
            }
        return new SoundPack(sounds, tones, fallback);
    }

    public IReadOnlyList<string> Tones => _tones ?? _fallback?.Tones ?? [];

    public IReadOnlyList<SoundCue> Describe(SoundRequest request)
    {
        foreach (var key in Keys(request))
            if (_sounds.TryGetValue(key, out var cues))
                return cues;
        return _fallback?.Describe(request) ?? [];
    }

    /// <summary>The entries a request looks for, most particular first.</summary>
    public static IEnumerable<string> Keys(SoundRequest request)
    {
        var name = request.Name;
        if (request.Mode is { } mode)
            yield return $"{name}@{mode}";
        if (request.Side is { } side)
            yield return $"{name}@tone{request.Tone}@side{side}";
        yield return $"{name}@tone{request.Tone}";
        if (request.Side is { } only)
            yield return $"{name}@side{only}";
        yield return $"{name}@{request.Variant}";
        yield return name;
    }

    private static SoundCue read(string root, string name, JsonElement cue)
    {
        var file = cue.GetProperty("file").GetString() ?? throw new InvalidDataException($"Sound '{name}' has no file.");
        var path = Path.GetFullPath(Path.Combine(root, file));
        // A skin is something people download: its files stay inside its folder.
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Sound '{name}' names a file outside its folder: {file}");
        float number(string property, float otherwise) => cue.TryGetProperty(property, out var value) ? value.GetSingle() : otherwise;
        var settings = new SoundSettings(number("gain", 1), number("pan", 0), (int)number("pitch", 0),
            TimeSpan.FromMilliseconds(number("delay", 0)));
        var bus = cue.TryGetProperty("bus", out var named) ? Enum.Parse<SoundBus>(named.GetString() ?? "", ignoreCase: true)
            : name.StartsWith("drum.", StringComparison.Ordinal) || name.StartsWith("click.", StringComparison.Ordinal) ? SoundBus.Drum
            : name.StartsWith("music.", StringComparison.Ordinal) ? SoundBus.Music
            : name.StartsWith("coin.", StringComparison.Ordinal) ? SoundBus.Coin
            : name.Contains("voice", StringComparison.Ordinal) ? SoundBus.Voice
            : SoundBus.Menu;
        return new(new SoundSource(path, 0, file) { Settings = () => settings, Variant = settings.ToString() }, bus);
    }
}
