using System.Text;
using System.Xml.Linq;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Formats.Ddp;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// Chooses a song's themed gameplay skin. The game names it per song (musicinfo.xml
/// <c>partsset</c>, "taiko" = the random original mix). Custom charts take the skin of the stock
/// song with the same title; otherwise a genre default (project choice: Vocaloid → miku). A chart's
/// own <see cref="SongDescriptor.ScenePresets"/> come first: a preset names a skin archive, with or
/// without its enso_ prefix, case and punctuation ignored (IMAS_SIDEM, enso_imasSideM). "original"
/// and unknown names give the regular gameplay; archives without the standard parts (dojo, tokkun,
/// waiwai, result, system) are never used. The list: docs/development/gameplay-skins.md.
/// Missing archives and skins without the standard parts fall back to the original mix.
/// </summary>
internal sealed class GameplaySkinResolver
{
    private static readonly Dictionary<string, string> ArchiveNames = new(StringComparer.Ordinal)
    {
        ["GMT"] = "enso_gmt",
        ["butto"] = "enso_buttoburst",
    };
    private static readonly string[] NotStandardSkins = ["taiko", "dojo", "original"];
    // The enso_ archives that are other modes' scenes, not themes (gameplay-skins.md).
    private static readonly string[] NotSkins = ["dojo", "tokkun", "waiwai", "waiwai_effect", "result", "system", "gudetama", "original"];

    private readonly string _lumenRoot;
    private readonly Dictionary<string, string> _partsByTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _partsByPreset = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GameplaySceneComposition.ThemedSkin?> _skins = new(StringComparer.Ordinal);

    /// <param name="lumenRoot">The lumendata/packed directory.</param>
    /// <param name="musicInfoPath">Green's config musicinfo.xml, when present.</param>
    public GameplaySkinResolver(string lumenRoot, string? musicInfoPath)
    {
        _lumenRoot = lumenRoot;
        if (Directory.Exists(lumenRoot))
            foreach (var directory in Directory.EnumerateDirectories(lumenRoot, "enso_*"))
                _partsByPreset.TryAdd(normalize(Path.GetFileName(directory)["enso_".Length..]), Path.GetFileName(directory)["enso_".Length..]);
        // (Before the aliases below: one name per archive.)
        Names = [.. _partsByPreset.Values.Where(static parts => !NotSkins.Contains(parts, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
        foreach (var parts in ArchiveNames.Keys)
            _partsByPreset[normalize(parts)] = parts;
        if (musicInfoPath is null || !File.Exists(musicInfoPath)) return;
        foreach (var song in XDocument.Load(musicInfoPath).Descendants("Data"))
            if ((string?)song.Element("musicname") is { } name && (string?)song.Element("partsset") is { } parts)
                _partsByTitle.TryAdd(normalize(name), parts);
    }

    /// <summary>The skins the player can choose for every song (the user's enso_ archives that are themes), by name.</summary>
    /// <remarks>ponytail: names only, not checked to load (enso_mh3G is listed, and plays as the regular gameplay).</remarks>
    public IReadOnlyList<string> Names { get; }

    /// <param name="choice">The player's setting (ArcadeSettings.GameplaySkin): "auto", "original" or a skin's
    /// name for every song; a name the data does not have counts as auto.</param>
    public GameplaySceneComposition.ThemedSkin? Resolve(SongDescriptor song, string category, string choice = "auto")
    {
        if (normalize(choice) == "original")
            return null;
        var parts = lookup(choice)
            ?? song.ScenePresets.Select(lookup).FirstOrDefault(value => value is not null)
            ?? new[] { song.Title.Japanese, song.Title.Primary, song.Title.English }
            .OfType<string>()
            .Select(title => _partsByTitle.GetValueOrDefault(normalize(title)))
            .FirstOrDefault(value => value is not null)
            ?? (normalize(category) is "vocaloid" or "ボーカロイド" ? "miku" : null);
        if (parts is null || NotStandardSkins.Contains(parts)) return null;
        return _skins.GetOrAdd($"{ArchiveNames.GetValueOrDefault(parts, "enso_" + parts)}/packeddata.ddp", load);
    }

    // A skin's parts set by any of its names (with or without enso_, case and punctuation ignored).
    private string? lookup(string name)
    {
        var key = normalize(name);
        return _partsByPreset.GetValueOrDefault(key)
            ?? (key.StartsWith("enso", StringComparison.Ordinal) ? _partsByPreset.GetValueOrDefault(key[4..]) : null);
    }

    // A skin is usable when it has the standard parts and every movie loads (enso_mh3G's Don-chan
    // backdrops reference a texture the archive lacks). Checked once per archive, textures not decoded.
    private GameplaySceneComposition.ThemedSkin? load(string archive)
    {
        var path = Path.Combine(_lumenRoot, archive);
        if (!File.Exists(path)) return null;
        var ddp = DdpArchive.Open(File.ReadAllBytes(path));
        var movies = ddp.Index.Movies.Select(movie => movie.Name).ToArray();
        // ponytail: only skins built from the standard parts are wired; others keep the original mix.
        if (!movies.Any(movie => GameplaySceneComposition.Role(Path.GetFileNameWithoutExtension(movie)) == "donbg"))
            return null;
        try
        {
            if (ddp.Index.Movies.Any(movie => Waddamburo.Game.LumenMovieContent.HasErrors(ddp.OpenMovie(movie))))
            {
                Console.Error.WriteLine($"Gameplay skin {archive} has broken movies; using the regular gameplay.");
                return null;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            Console.Error.WriteLine($"Gameplay skin {archive} is unreadable ({exception.Message}); using the regular gameplay.");
            return null;
        }
        return new(archive, movies);
    }

    private static string normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        return builder.ToString();
    }
}
