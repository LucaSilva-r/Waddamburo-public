using Waddamburo.Catalog;

namespace Waddamburo.Game.SongSelect;

/// <summary>
/// The songs marked favourite (P in Song Select), in the order marked; kept in a text file of
/// "Source&lt;tab&gt;StableId" lines (none: memory only).
/// </summary>
// ponytail: one list for the machine; per-account favourites (by baid) when profiles need them.
public sealed class SongFavourites
{
    private readonly string? _path;
    private readonly List<SongKey> _songs = [];

    public SongFavourites(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        foreach (var line in File.ReadAllLines(path))
            if (line.Split('\t', 2) is [var source, var id] && Enum.TryParse<SongSourceKind>(source, out var kind)
                && !string.IsNullOrWhiteSpace(id))
                _songs.Add(new SongKey(kind, id));
    }

    public IReadOnlyList<SongKey> Songs => _songs;

    public bool Contains(SongKey song) => _songs.Contains(song);

    /// <summary>Marks or unmarks a song; true when it is now a favourite.</summary>
    public bool Toggle(SongKey song)
    {
        var added = !_songs.Remove(song);
        if (added)
            _songs.Add(song);
        if (_path is not null)
        {
            try
            {
                File.WriteAllLines(_path, _songs.Select(static key => $"{key.Source}\t{key.StableId}"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error FAVOURITES_SAVE: {exception.Message}");
            }
        }
        return added;
    }
}
