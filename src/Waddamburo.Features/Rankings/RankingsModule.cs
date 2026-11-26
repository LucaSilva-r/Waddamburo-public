using System.Collections.Concurrent;
using Waddamburo.Catalog;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Rankings;

/// <summary>
/// Rankings: the online top players per course of the song under Song Select's cursor, fetched from TaikOnline as the
/// cursor moves and kept for a minute; a play that ranks in shows at once. Chart hashes come from <see cref="ChartHashes"/>.
/// </summary>
/// <remarks><paramref name="client"/>: the server to ask (null: offline).</remarks>
public sealed class RankingsModule(Func<ScoreClient?> client, ChartHashes hashes) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<SongKey, (DateTime At, IReadOnlyList<RankingEntry>?[] Courses)> _songs = new();
    private CancellationTokenSource? _current;

    /// <summary>The cursor is on a song: fetch its windows unless they are fresh. A newer song cancels the fetch.</summary>
    public void Want(SongSelectSong song)
    {
        var key = song.Descriptor.Key;
        if (_songs.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < Fresh)
            return;
        if (client() is not { } server)
            return;
        _current?.Cancel();
        var work = _current = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var courses = new string?[5];
                foreach (var chart in song.Descriptor.Charts)
                    if (chart.Course is { } course && (int)course < courses.Length)
                        courses[(int)course] = await hashes.HashAsync(chart, work.Token).ConfigureAwait(false);
                var wanted = courses.OfType<string>().Distinct().ToList();
                if (wanted.Count == 0)
                    return;
                var rankings = await server.RankingsAsync(wanted, work.Token).ConfigureAwait(false);
                _songs[key] = (DateTime.UtcNow,
                    [.. courses.Select(hash => hash is not null && rankings.TryGetValue(hash, out var lines) ? lines : null)]);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                or NotSupportedException or System.Text.Json.JsonException)
            {
                // ponytail: an unreachable server or unreadable chart just leaves the windows empty.
            }
        }, work.Token);
    }

    public void Dispose() => _current?.Cancel(); // ponytail: sources are left to the GC; their fetches may still run

    /// <summary>
    /// A play ranked in: its course's board becomes <paramref name="top"/> and stays fresh, so song
    /// select shows it at once (the upload may not have reached the server yet).
    /// </summary>
    public void Record(SongSelectSong song, int course, IReadOnlyList<RankingEntry> top)
    {
        if (!_songs.TryGetValue(song.Descriptor.Key, out var known) || (uint)course >= (uint)known.Courses.Length)
            return;
        var courses = (IReadOnlyList<RankingEntry>?[])known.Courses.Clone();
        courses[course] = top;
        _songs[song.Descriptor.Key] = (DateTime.UtcNow, courses);
    }

    /// <summary>What is known for the song now (null: nothing yet).</summary>
    public IReadOnlyList<RankingEntry>?[]? For(SongSelectSong song) =>
        _songs.TryGetValue(song.Descriptor.Key, out var known) ? known.Courses : null;
}
