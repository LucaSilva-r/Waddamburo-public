using System.Text;
using System.Xml.Linq;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Formats.Ddp;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// Chooses a song's themed gameplay skin. The game names it per song (musicinfo.xml
/// <c>partsset</c>, "taiko" = the random original mix). Custom charts take the skin of the stock
/// song with the same title; otherwise a genre default (project choice: Vocaloid → miku).
/// Missing archives and skins without the standard parts fall back to the original mix.
/// </summary>
internal sealed class GameplaySkinResolver
{
    private static readonly Dictionary<string, string> ArchiveNames = new(StringComparer.Ordinal)
    {
        ["GMT"] = "enso_gmt",
        ["butto"] = "enso_buttoburst",
    };
    private static readonly string[] NotStandardSkins = ["taiko", "dojo"];

    private readonly string _lumenRoot;
    private readonly Dictionary<string, string> _partsByTitle = new(StringComparer.Ordinal);

    /// <param name="lumenRoot">The lumendata/packed directory.</param>
    /// <param name="musicInfoPath">Green's config musicinfo.xml, when present.</param>
    public GameplaySkinResolver(string lumenRoot, string? musicInfoPath)
    {
        _lumenRoot = lumenRoot;
        if (musicInfoPath is null || !File.Exists(musicInfoPath)) return;
        foreach (var song in XDocument.Load(musicInfoPath).Descendants("Data"))
            if ((string?)song.Element("musicname") is { } name && (string?)song.Element("partsset") is { } parts)
                _partsByTitle.TryAdd(normalize(name), parts);
    }

    public GameplaySceneComposition.ThemedSkin? Resolve(SongDescriptor song, string category)
    {
        var parts = new[] { song.Title.Japanese, song.Title.Primary, song.Title.English }
            .OfType<string>()
            .Select(title => _partsByTitle.GetValueOrDefault(normalize(title)))
            .FirstOrDefault(value => value is not null)
            ?? (normalize(category) is "vocaloid" or "ボーカロイド" ? "miku" : null);
        if (parts is null || NotStandardSkins.Contains(parts)) return null;
        var archive = $"{ArchiveNames.GetValueOrDefault(parts, "enso_" + parts)}/packeddata.ddp";
        var path = Path.Combine(_lumenRoot, archive);
        if (!File.Exists(path)) return null;
        var movies = DdpArchive.Open(File.ReadAllBytes(path)).Index.Movies.Select(movie => movie.Name).ToArray();
        // ponytail: only skins built from the standard parts are wired; others keep the original mix.
        return movies.Any(movie => GameplaySceneComposition.Role(Path.GetFileNameWithoutExtension(movie)) == "donbg")
            ? new(archive, movies)
            : null;
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
