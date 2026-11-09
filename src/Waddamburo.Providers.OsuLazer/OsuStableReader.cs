using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Reads an osu!stable install (osu!.db, collection.db and the Songs folder) into the same snapshot lazer's realm gives,
/// so both share the grouping, the course layout and the collections. Formats: ppy/osu GitHub wiki, "Legacy database
/// file structure" (osu!.db and collection.db).
/// </summary>
public static class OsuStableReader
{
    /// <summary>True when <paramref name="folder"/> is an osu!stable install (has osu!.db).</summary>
    public static bool IsInstall(string folder) => File.Exists(Path.Combine(folder, "osu!.db"));

    /// <summary>
    /// The snapshot and where each file id lives: a .osu file's id is the SHA-256 of its contents (lazer's own id for
    /// it, so a chart keeps its scores across both), an audio file's the SHA-256 of its path in the Songs folder.
    /// </summary>
    public static (OsuLazerSnapshot Snapshot, IReadOnlyDictionary<string, string> Files) Read(string installFolder)
    {
        var songs = songsFolder(installFolder);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var beatmaps = new List<(string Folder, OsuLazerBeatmap Beatmap, string AudioPath)>();
        var dateAdded = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var setOnlineId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(installFolder, "osu!.db")), Encoding.UTF8))
        {
            var version = reader.ReadInt32();
            reader.ReadInt32(); // folder count
            reader.ReadBoolean(); // account unlocked
            reader.ReadInt64(); // unlock date
            readString(reader); // player name
            var count = reader.ReadInt32();
            for (var index = 0; index < count; index++)
            {
                var entry = readBeatmap(reader, version);
                if (entry.Mode != 1)
                    continue; // native osu!taiko beatmaps only, as from lazer
                var osuPath = Path.Combine(songs, entry.Folder, entry.OsuFile);
                if (!File.Exists(osuPath))
                    continue;
                var osuHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(osuPath)));
                files[osuHash] = osuPath;
                var audioPath = Path.Combine(songs, entry.Folder, entry.Audio);
                beatmaps.Add((entry.Folder, new OsuLazerBeatmap(guid(entry.Folder + "/" + entry.OsuFile), entry.Difficulty, "taiko",
                    entry.Title, entry.TitleUnicode, entry.Artist, entry.ArtistUnicode, entry.Creator, entry.Audio, entry.PreviewTime,
                    osuHash, entry.Md5, entry.TaikoStars, false)
                {
                    Bpm = entry.Bpm,
                    Length = TimeSpan.FromMilliseconds(Math.Max(0, entry.TotalTime)),
                    OnlineId = entry.BeatmapId,
                }, audioPath));
                var modified = entry.Modified is > 0 and < 3155378975999999999 // DateTime.MaxValue.Ticks
                    ? new DateTimeOffset(entry.Modified, TimeSpan.Zero) : (DateTimeOffset?)null;
                if (modified is { } date && (!dateAdded.TryGetValue(entry.Folder, out var seen) || date < seen))
                    dateAdded[entry.Folder] = date;
                if (entry.SetId > 0)
                    setOnlineId[entry.Folder] = entry.SetId;
            }
        }
        var sets = beatmaps.GroupBy(static map => map.Folder, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var setFiles = group.Select(map =>
            {
                var audioHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(map.AudioPath.ToUpperInvariant())));
                files[audioHash] = map.AudioPath;
                return new OsuLazerFile(map.Beatmap.AudioFilename, audioHash);
            }).ToArray();
            return new OsuLazerBeatmapSet(guid(group.Key), false, setFiles, [.. group.Select(static map => map.Beatmap)])
            {
                DateAdded = dateAdded.TryGetValue(group.Key, out var date) ? date : null,
                OnlineId = setOnlineId.GetValueOrDefault(group.Key),
            };
        }).ToArray();
        return (new OsuLazerSnapshot(0, sets) { Collections = readCollections(Path.Combine(installFolder, "collection.db")) }, files);
    }

    private sealed record Entry(string Artist, string ArtistUnicode, string Title, string TitleUnicode, string Creator,
        string Difficulty, string Audio, string Md5, string OsuFile, long Modified, double TaikoStars, int TotalTime,
        int PreviewTime, double Bpm, int BeatmapId, int SetId, byte Mode, string Folder);

    private static Entry readBeatmap(BinaryReader reader, int version)
    {
        if (version < 20191106)
            reader.ReadInt32(); // entry size
        var artist = readString(reader);
        var artistUnicode = readString(reader);
        var title = readString(reader);
        var titleUnicode = readString(reader);
        var creator = readString(reader);
        var difficulty = readString(reader);
        var audio = readString(reader);
        var md5 = readString(reader);
        var osuFile = readString(reader);
        reader.ReadByte(); // ranked status
        reader.ReadBytes(6); // circles, sliders, spinners
        var modified = reader.ReadInt64();
        reader.ReadBytes(version < 20140609 ? 4 : 16); // AR, CS, HP, OD
        reader.ReadDouble(); // slider velocity
        var taikoStars = -1d;
        if (version >= 20140609)
            for (var ruleset = 0; ruleset < 4; ruleset++)
            {
                var pairs = reader.ReadInt32();
                for (var pair = 0; pair < pairs; pair++)
                {
                    reader.ReadByte(); // 0x08
                    var mods = reader.ReadInt32();
                    // 0x0c Single since 20250107, before that 0x0d Double.
                    var stars = reader.ReadByte() == 0x0c ? reader.ReadSingle() : reader.ReadDouble();
                    if (ruleset == 1 && mods == 0)
                        taikoStars = stars;
                }
            }
        reader.ReadInt32(); // drain time
        var totalTime = reader.ReadInt32();
        var previewTime = reader.ReadInt32();
        var bpm = 0d;
        var points = reader.ReadInt32();
        for (var point = 0; point < points; point++)
        {
            var beatLength = reader.ReadDouble(); // stored as milliseconds per beat
            reader.ReadDouble(); // offset
            var uninherited = reader.ReadBoolean();
            if (bpm == 0 && uninherited && beatLength > 0)
                bpm = Math.Round(60000 / beatLength, 2);
        }
        var beatmapId = reader.ReadInt32(); // the difficulty's id
        var setId = reader.ReadInt32();
        reader.ReadInt32(); // thread
        reader.ReadBytes(4); // grades
        reader.ReadInt16(); // local offset
        reader.ReadSingle(); // stack leniency
        var mode = reader.ReadByte();
        readString(reader); // source
        readString(reader); // tags
        reader.ReadInt16(); // online offset
        readString(reader); // title font
        reader.ReadBoolean(); // unplayed
        reader.ReadInt64(); // last played
        reader.ReadBoolean(); // osz2
        var folder = readString(reader);
        reader.ReadInt64(); // last checked
        reader.ReadBytes(5); // ignore sound, ignore skin, disable storyboard, disable video, visual override
        if (version < 20140609)
            reader.ReadInt16();
        reader.ReadInt32(); // last modification (?)
        reader.ReadByte(); // mania scroll speed
        return new Entry(artist, artistUnicode, title, titleUnicode, creator, difficulty, audio, md5, osuFile, modified,
            taikoStars, totalTime, previewTime, bpm, beatmapId, setId, mode, folder);
    }

    private static List<OsuLazerCollectionInfo> readCollections(string path)
    {
        if (!File.Exists(path))
            return [];
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        reader.ReadInt32(); // version
        var collections = new List<OsuLazerCollectionInfo>();
        for (var count = reader.ReadInt32(); count > 0; count--)
        {
            var name = readString(reader);
            var maps = new string[reader.ReadInt32()];
            for (var index = 0; index < maps.Length; index++)
                maps[index] = readString(reader);
            collections.Add(new OsuLazerCollectionInfo(guid("collection:" + name), name, maps));
        }
        return collections;
    }

    // 0x00: absent; 0x0b: a ULEB128 byte length, then UTF-8.
    private static string readString(BinaryReader reader)
    {
        if (reader.ReadByte() != 0x0b)
            return "";
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            var part = reader.ReadByte();
            length |= (part & 0x7f) << shift;
            if (part < 0x80)
                break;
        }
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    // The Songs folder: osu!.<user>.cfg's BeatmapDirectory (absolute or relative to the install) when set.
    private static string songsFolder(string install)
    {
        foreach (var cfg in Directory.EnumerateFiles(install, "osu!.*.cfg"))
            foreach (var line in File.ReadLines(cfg))
                if (line.Split('=', 2) is [var key, var value] && key.Trim() == "BeatmapDirectory" && value.Trim().Length > 0)
                    return Path.Combine(install, value.Trim());
        return Path.Combine(install, "Songs");
    }

    // A stable id from a name (sets and collections have no GUID in stable).
    private static Guid guid(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())).AsSpan(0, 16));
}
