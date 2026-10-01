using Realms;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Reads the song-library portion of an existing osu!lazer Realm without
/// creating, migrating, recovering, or writing the database.
/// </summary>
/// <remarks>
/// The Realm is opened dynamically: the schema comes from the file itself, so any lazer version whose
/// beatmap tables keep the fields read here works, without pinning osu!'s own model assembly.
/// </remarks>
public static class OsuLazerRealmReader
{
    public static OsuLazerSnapshot Read(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The osu!lazer Realm database does not exist.", fullPath);

        // ponytail: read-only Realms skip the lock-file protocol; a scan while osu! is saving a beatmap
        // could read a half-written version. Copy-on-scan if that ever bites.
        var configuration = new RealmConfiguration(fullPath)
        {
            IsReadOnly = true,
            IsDynamic = true,
        };

        try
        {
            using var realm = Realm.GetInstance(configuration);
            var sets = realm.DynamicApi.All("BeatmapSet")
                .AsEnumerable()
                .Select(copySet)
                .ToArray();
            var collections = realm.Schema.TryFindObjectSchema("BeatmapCollection", out _)
                ? realm.DynamicApi.All("BeatmapCollection").AsEnumerable()
                    .Select(static collection => new OsuLazerCollectionInfo(
                        collection.DynamicApi.Get<Guid>("ID"),
                        collection.DynamicApi.Get<string>("Name") ?? "",
                        [.. collection.DynamicApi.GetList<string>("BeatmapMD5Hashes")]))
                    .ToArray()
                : [];
            return new OsuLazerSnapshot(realm.Config.SchemaVersion, sets) { Collections = collections };
        }
        catch (Exception exception) when (exception is not OsuLazerRealmReadException)
        {
            throw new OsuLazerRealmReadException(
                "The osu!lazer database could not be opened read-only.",
                exception);
        }
    }

    private static OsuLazerBeatmapSet copySet(IRealmObjectBase set)
    {
        var files = set.DynamicApi.GetList<IRealmObjectBase>("Files")
            .Where(static file => file.DynamicApi.Get<IRealmObjectBase?>("File") is not null)
            .Select(static file => new OsuLazerFile(
                file.DynamicApi.Get<string>("Filename"),
                file.DynamicApi.Get<IRealmObjectBase>("File").DynamicApi.Get<string>("Hash")))
            .ToArray();

        var beatmaps = set.DynamicApi.GetList<IRealmObjectBase>("Beatmaps")
            .Where(static beatmap => beatmap.DynamicApi.Get<IRealmObjectBase?>("Metadata") is not null
                && beatmap.DynamicApi.Get<IRealmObjectBase?>("Ruleset") is not null)
            .Select(static beatmap =>
            {
                var metadata = beatmap.DynamicApi.Get<IRealmObjectBase>("Metadata");
                var author = metadata.DynamicApi.Get<IRealmObjectBase?>("Author");
                return new OsuLazerBeatmap(
                    beatmap.DynamicApi.Get<Guid>("ID"),
                    beatmap.DynamicApi.Get<string>("DifficultyName") ?? "",
                    beatmap.DynamicApi.Get<IRealmObjectBase>("Ruleset").DynamicApi.Get<string>("ShortName"),
                    metadata.DynamicApi.Get<string>("Title") ?? "",
                    metadata.DynamicApi.Get<string>("TitleUnicode") ?? "",
                    metadata.DynamicApi.Get<string>("Artist") ?? "",
                    metadata.DynamicApi.Get<string>("ArtistUnicode") ?? "",
                    author?.DynamicApi.Get<string>("Username") ?? "",
                    metadata.DynamicApi.Get<string>("AudioFile") ?? "",
                    metadata.DynamicApi.Get<int>("PreviewTime"),
                    beatmap.DynamicApi.Get<string>("Hash") ?? "",
                    beatmap.DynamicApi.Get<string>("MD5Hash") ?? "",
                    beatmap.DynamicApi.Get<double>("StarRating"),
                    beatmap.DynamicApi.Get<bool>("Hidden"))
                {
                    Bpm = beatmap.DynamicApi.Get<double>("BPM"),
                    Length = TimeSpan.FromMilliseconds(beatmap.DynamicApi.Get<double>("Length")),
                };
            })
            .ToArray();

        return new OsuLazerBeatmapSet(
            set.DynamicApi.Get<Guid>("ID"),
            set.DynamicApi.Get<bool>("DeletePending"),
            files,
            beatmaps)
        {
            DateAdded = set.DynamicApi.Get<DateTimeOffset>("DateAdded"),
        };
    }
}

public sealed record OsuLazerSnapshot(
    ulong SchemaVersion,
    IReadOnlyList<OsuLazerBeatmapSet> BeatmapSets)
{
    /// <summary>The player's collections (lazer lists them by beatmap MD5).</summary>
    public IReadOnlyList<OsuLazerCollectionInfo> Collections { get; init; } = [];
}

public sealed record OsuLazerCollectionInfo(Guid Id, string Name, IReadOnlyList<string> BeatmapMd5Hashes);

public sealed record OsuLazerBeatmapSet(
    Guid Id,
    bool DeletePending,
    IReadOnlyList<OsuLazerFile> Files,
    IReadOnlyList<OsuLazerBeatmap> Beatmaps)
{
    public DateTimeOffset? DateAdded { get; init; }
}

public sealed record OsuLazerFile(string Filename, string Hash);

/// <summary>
/// One beatmap. FileHash: SHA-256 of the .osu file, also its name in lazer's file store; PreviewTime:
/// song preview start in milliseconds, negative when unset; StarRating: negative when not computed yet.
/// </summary>
public sealed record OsuLazerBeatmap(
    Guid Id,
    string DifficultyName,
    string Ruleset,
    string Title,
    string TitleUnicode,
    string Artist,
    string ArtistUnicode,
    string Creator,
    string AudioFilename,
    int PreviewTime,
    string FileHash,
    string Md5Hash,
    double StarRating,
    bool Hidden)
{
    public double Bpm { get; init; }

    public TimeSpan Length { get; init; }
}

public sealed class OsuLazerRealmReadException : Exception
{
    public OsuLazerRealmReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
