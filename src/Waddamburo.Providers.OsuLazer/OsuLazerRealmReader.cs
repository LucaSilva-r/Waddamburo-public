using osu.Game.Beatmaps;
using Realms;
using System.Runtime.CompilerServices;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Reads the song-library portion of an existing osu!lazer Realm without
/// creating, migrating, recovering, or writing the database.
/// </summary>
public static class OsuLazerRealmReader
{
    // ppy.osu.Game 2026.916.0 uses schema 52. Keep this value paired with the
    // package version so an incompatible database fails instead of migrating.
    public const ulong SupportedSchemaVersion = 52;

    public static OsuLazerSnapshot Read(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The osu!lazer Realm database does not exist.", fullPath);

        // Realm models register through osu.Game's generated module initializer.
        // It must run before the process opens its first default-schema Realm.
        RuntimeHelpers.RunModuleConstructor(typeof(BeatmapSetInfo).Module.ModuleHandle);

        var configuration = new RealmConfiguration(fullPath)
        {
            IsReadOnly = true,
            SchemaVersion = SupportedSchemaVersion,
        };

        try
        {
            using var realm = Realm.GetInstance(configuration);

            var sets = realm.All<BeatmapSetInfo>()
                .AsEnumerable()
                .Select(copySet)
                .ToArray();

            return new OsuLazerSnapshot(SupportedSchemaVersion, sets);
        }
        catch (Exception exception) when (exception is not OsuLazerRealmReadException)
        {
            throw new OsuLazerRealmReadException(
                "The osu!lazer database could not be opened read-only with the supported official schema.",
                exception);
        }
    }

    private static OsuLazerBeatmapSet copySet(BeatmapSetInfo set)
    {
        var files = set.Files
            .Select(file => new OsuLazerFile(file.Filename, file.File.Hash))
            .ToArray();

        var beatmaps = set.Beatmaps
            .Select(beatmap => new OsuLazerBeatmap(
                beatmap.ID,
                beatmap.OnlineID,
                beatmap.DifficultyName,
                beatmap.Ruleset.ShortName,
                beatmap.Metadata.Title,
                beatmap.Metadata.TitleUnicode,
                beatmap.Metadata.Artist,
                beatmap.Metadata.ArtistUnicode,
                beatmap.Metadata.Author.Username,
                beatmap.Metadata.AudioFile,
                beatmap.Metadata.BackgroundFile,
                beatmap.Hash,
                beatmap.MD5Hash,
                beatmap.Length,
                beatmap.BPM))
            .ToArray();

        return new OsuLazerBeatmapSet(
            set.ID,
            set.OnlineID,
            set.DateAdded,
            set.Hash,
            set.DeletePending,
            files,
            beatmaps);
    }
}

public sealed record OsuLazerSnapshot(
    ulong SchemaVersion,
    IReadOnlyList<OsuLazerBeatmapSet> BeatmapSets);

public sealed record OsuLazerBeatmapSet(
    Guid Id,
    int OnlineId,
    DateTimeOffset DateAdded,
    string Hash,
    bool DeletePending,
    IReadOnlyList<OsuLazerFile> Files,
    IReadOnlyList<OsuLazerBeatmap> Beatmaps);

public sealed record OsuLazerFile(string Filename, string Hash);

public sealed record OsuLazerBeatmap(
    Guid Id,
    int OnlineId,
    string DifficultyName,
    string Ruleset,
    string Title,
    string TitleUnicode,
    string Artist,
    string ArtistUnicode,
    string Creator,
    string AudioFilename,
    string BackgroundFilename,
    string FileHash,
    string Md5Hash,
    double Length,
    double Bpm);

public sealed class OsuLazerRealmReadException : Exception
{
    public OsuLazerRealmReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
