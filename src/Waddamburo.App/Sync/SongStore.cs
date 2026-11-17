using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Waddamburo.Catalog;

namespace Waddamburo.App.Sync;

/// <summary>
/// A course of a stored song: its chart and drawing data (blob hashes), its duet charts (pairs of the same), how Song
/// Select shows it, what the canonical bytes leave out (length, shin-uchi base), and whether the sender kept its scores
/// off TaikOnline (a donotupload marker there: it stays off here too).
/// </summary>
public sealed record StoredChart(string Id, TaikoCourse Course, int? Level, string Name, string Chart, string Drawing,
    long DurationMs, string[] Duets, string[] DuetDrawings, int? ShinuchiBase = null, bool HasBranches = false, int? OnlineId = null,
    bool NoUpload = false);

/// <summary>The category a stored song sits in on the sender (its key, so it joins the receiver's same category).</summary>
public sealed record StoredCategory(string Id, string Name, int Order, string? Genre = null, uint? Colour = null,
    uint? OutlineColour = null, string[]? Description = null);

/// <summary>
/// A song as one source lists it on the sender (see song-sync-plan.md): its key (so scores and favourites follow), what
/// Song Select shows, its category, its music and each course's chart, as blob hashes. A row binds its own charts to its
/// own music: blobs are shared between rows, timing never is.
/// </summary>
public sealed record StoredSong(SongSourceKind Source, string SongId, string Title, StoredCategory Category, string Audio,
    StoredChart[] Charts, string? TitleJa = null, string? TitleEn = null, string? Artist = null, string? Subtitle = null,
    string? EnglishSubtitle = null, string? Creator = null, long? PreviewMs = null, double? Bpm = null, long? LengthMs = null,
    string? Composition = null, string[]? ScenePresets = null, bool DemoPlay = false, int? OnlineSetId = null)
{
    /// <summary>Every blob the row needs.</summary>
    public IEnumerable<string> Blobs() =>
        Charts.SelectMany(static chart => chart.Duets.Concat(chart.DuetDrawings).Append(chart.Chart).Append(chart.Drawing))
            .Append(Audio).Concat(Composition is null ? [] : [Composition]);
}

/// <summary>
/// The songs this device received (<see cref="PhoneReceiver"/>'s offer/blobs/commit): every file once, named by its
/// SHA-256 (blobs/ab/abcd...), and the rows in songs.json.
/// </summary>
/// <remarks>ponytail: one JSON file rewritten at each commit; SQLite if libraries of tens of thousands make it slow.</remarks>
public sealed class SongStore(string root)
{

    private readonly Lock _commit = new();

    public string Root { get; } = root;

    private string RowsPath => Path.Combine(Root, "songs.json");

    /// <summary>Where a blob is kept (whether or not it is there).</summary>
    public string BlobPath(string hash) =>
        IsHash(hash) ? Path.Combine(Root, "blobs", hash[..2], hash) : throw new ArgumentException($"Not a blob hash: '{hash}'.");

    public bool Has(string hash) => IsHash(hash) && File.Exists(BlobPath(hash));

    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The stored rows (none when nothing was received).</summary>
    public List<StoredSong> Load() =>
        File.Exists(RowsPath) ? JsonSerializer.Deserialize(File.ReadAllText(RowsPath), SyncJson.Default.ListStoredSong) ?? [] : [];

    /// <summary>
    /// Keeps a received blob: written under a temporary name, kept only when its contents hash to its name.
    /// </summary>
    public async Task KeepAsync(string hash, Stream contents, long size)
    {
        var path = BlobPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        await using (var file = File.Create(partial))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 20];
            while (size > 0)
            {
                var count = await contents.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size))).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException();
                sha.AppendData(buffer, 0, count);
                await file.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                size -= count;
            }
            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != hash)
            {
                file.Close();
                File.Delete(partial);
                throw new InvalidDataException($"Blob {hash[..12]} arrived damaged.");
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>
    /// Adds the rows whose blobs are all here (a row of the same source and song is replaced); returns how many.
    /// Sync only adds: rows not offered stay.
    /// </summary>
    public int Commit(IEnumerable<StoredSong> offered)
    {
        lock (_commit)
        {
            var rows = Load().ToDictionary(static row => (row.Source, row.SongId));
            var added = 0;
            foreach (var row in offered.Where(row => row.Blobs().All(Has)))
            {
                rows[(row.Source, row.SongId)] = row;
                added++;
            }
            Directory.CreateDirectory(Root);
            var partial = RowsPath + ".part";
            File.WriteAllText(partial, JsonSerializer.Serialize(rows.Values.ToList(), SyncJson.Default.ListStoredSong));
            File.Move(partial, RowsPath, overwrite: true);
            return added;
        }
    }
}

/// <summary>The store's and the offer's JSON, generated (trimmed builds such as Android's have no reflection).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<StoredSong>))]
[JsonSerializable(typeof(SongOffer))]
internal sealed partial class SyncJson : JsonSerializerContext;
