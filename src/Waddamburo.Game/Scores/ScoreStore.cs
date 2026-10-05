using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Scores;

public enum TaikoCrown { None, Clear, FullCombo }

/// <summary>A player's best on one chart, as the server keeps it (crown 0 none, 1 clear, 2 full combo).</summary>
public sealed record RemoteBest(string Sha256, long Score, int Crown);

/// <summary>A play as the server takes it (snake_case JSON); the replay is base64.</summary>
public sealed record UploadPlay(Guid Id, long Baid, string ChartSha256, string Mode, int Course, long Score,
    int Great, int Good, int Miss, int MaxCombo, int Rolls, int Gauge, bool Cleared, int ScoringVersion,
    string EngineVersion, DateTimeOffset PlayedAt, string Replay)
{
    /// <summary>The player's audio offset during the play (null: recorded before it was kept).</summary>
    public int? AudioOffsetMs { get; init; }

    /// <summary>The player's input offset during the play; the replay's times already have it applied.</summary>
    public int? InputOffsetMs { get; init; }
}

/// <summary>
/// A chart import: gzipped canonical notes (<see cref="ChartHash.Serialize"/>), base64, plus display metadata.
/// Difficulty is the chart's own name where courses are not named (osu!); the osu ids link it on osu.ppy.sh.
/// SongKey groups the charts into songs on the website: the game's song id (stock, Nijiiro), the beatmap set
/// (osu!); null for TJA, whose paths differ between machines (the website falls back to the titles).
/// </summary>
public sealed record ChartUpload(string Notes, string? Title, string? Subtitle, string? Source, int Course, int? Level)
{
    public string? SongKey { get; init; }

    public string? TitleEn { get; init; }

    public string? SubtitleEn { get; init; }

    public string? Difficulty { get; init; }

    public int? OsuBeatmapId { get; init; }

    public int? OsuBeatmapsetId { get; init; }

    public static ChartUpload From(PlayableChart chart, TaikoCourse course, SongDescriptor? song)
    {
        ArgumentNullException.ThrowIfNull(chart);
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            gzip.Write(ChartHash.Serialize(chart, course));
        var source = chart.Key.Song.Source;
        var descriptor = song?.Charts.FirstOrDefault(candidate => candidate.Key == chart.Key);
        var osu = source == SongSourceKind.OsuLazer;
        return new ChartUpload(Convert.ToBase64String(output.ToArray()), song?.Title.Primary, song?.Subtitle,
            source.ToString(), (int)course, chart.Level)
        {
            SongKey = source switch
            {
                SongSourceKind.Stock or SongSourceKind.Nijiiro => chart.Key.Song.StableId,
                // An unsubmitted set has no online id; its local key is still one song on this machine.
                SongSourceKind.OsuLazer => song?.OnlineSetId is { } set ? $"set:{set}" : $"local:{chart.Key.Song.StableId}",
                _ => null,
            },
            TitleEn = song?.Title.English,
            SubtitleEn = song?.EnglishSubtitle,
            // Other sources name their charts after the course (TJA: the raw COURSE value), nothing to add.
            Difficulty = osu ? descriptor?.DifficultyName : null,
            OsuBeatmapId = osu ? descriptor?.OnlineId : null,
            OsuBeatmapsetId = osu ? song?.OnlineSetId : null,
        };
    }
}

/// <summary>
/// One finished play of one player. Every play is kept (bests are derived), with its replay so it
/// can be rescored when the scoring rules change.
/// </summary>
public sealed record PlayRecord(
    Guid Id,
    long Baid,
    string ChartSha256,
    ChartKey Chart,
    string Mode,
    TaikoPlayResult Result,
    DateTimeOffset PlayedAt,
    byte[] Replay)
{
    /// <summary>
    /// The judgement and scoring rules a play was scored under. Bump when they change; older plays
    /// are rescored from their replays (leaderboards may then show only the current version).
    /// </summary>
    public const int CurrentScoringVersion = 1;

    public int ScoringVersion { get; init; } = CurrentScoringVersion;

    public string EngineVersion { get; init; } =
        typeof(PlayRecord).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>The audio offset setting the play was made with (context for its replay; judgement does not use it).</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>The input offset setting the play was made with; the replay's input times already include it.</summary>
    public int InputOffsetMs { get; init; }
}

/// <summary>The local score database (SQLite, one file beside the cabinet settings).</summary>
public sealed class ScoreStore : IDisposable
{
    public const string FileName = "scores.db";

    // Index = schema version reached after running it (PRAGMA user_version).
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE plays (
            id TEXT PRIMARY KEY,
            baid INTEGER NOT NULL,
            chart_sha256 TEXT NOT NULL,
            chart_key TEXT NOT NULL,
            course INTEGER NOT NULL,
            mode TEXT NOT NULL,
            score INTEGER NOT NULL,
            great INTEGER NOT NULL,
            good INTEGER NOT NULL,
            miss INTEGER NOT NULL,
            max_combo INTEGER NOT NULL,
            rolls INTEGER NOT NULL,
            gauge INTEGER NOT NULL,
            cleared INTEGER NOT NULL,
            scoring_version INTEGER NOT NULL,
            engine_version TEXT NOT NULL,
            played_at TEXT NOT NULL,
            replay BLOB NOT NULL,
            uploaded_at TEXT
        );
        CREATE INDEX plays_player_chart ON plays (baid, chart_key);
        CREATE INDEX plays_chart ON plays (chart_sha256);
        """,
        // The canonical notes of every chart played, for the server to import on request.
        """
        CREATE TABLE charts (
            sha256 TEXT PRIMARY KEY,
            notes BLOB NOT NULL,
            title TEXT,
            subtitle TEXT,
            source TEXT,
            course INTEGER NOT NULL,
            level INTEGER
        );
        """,
        // Chart key -> hash for the whole library (filled in the background), and each player's bests
        // from the server, so crowns match on every machine: a crown is keyed by chart hash.
        """
        CREATE TABLE chart_hashes (
            chart_key TEXT PRIMARY KEY,
            sha256 TEXT NOT NULL
        );
        CREATE INDEX chart_hashes_sha ON chart_hashes (sha256);
        INSERT OR IGNORE INTO chart_hashes (chart_key, sha256) SELECT chart_key, chart_sha256 FROM plays;
        CREATE TABLE remote_bests (
            baid INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            score INTEGER NOT NULL,
            crown INTEGER NOT NULL,
            PRIMARY KEY (baid, sha256)
        );
        """,
        // English titles, osu! difficulty names and online ids, for the website.
        """
        ALTER TABLE charts ADD COLUMN song_key TEXT;
        ALTER TABLE charts ADD COLUMN title_en TEXT;
        ALTER TABLE charts ADD COLUMN subtitle_en TEXT;
        ALTER TABLE charts ADD COLUMN difficulty TEXT;
        ALTER TABLE charts ADD COLUMN osu_beatmap_id INTEGER;
        ALTER TABLE charts ADD COLUMN osu_beatmapset_id INTEGER;
        """,
        // The offsets each play was made with, kept with its replay.
        """
        ALTER TABLE plays ADD COLUMN audio_offset_ms INTEGER;
        ALTER TABLE plays ADD COLUMN input_offset_ms INTEGER;
        """,
    ];

    private readonly SqliteConnection _connection;

    public ScoreStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        migrate();
    }

    // ponytail: one connection behind a lock (game thread saves, uploader reads); fine at a few plays a minute.
    public void Save(PlayRecord play, ChartUpload chart)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(chart);
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            insertPlay(play, transaction);
            using (var hash = _connection.CreateCommand())
            {
                hash.Transaction = transaction;
                hash.CommandText = "INSERT OR REPLACE INTO chart_hashes (chart_key, sha256) VALUES ($key, $sha)";
                hash.Parameters.AddWithValue("$key", play.Chart.ToString());
                hash.Parameters.AddWithValue("$sha", play.ChartSha256);
                hash.ExecuteNonQuery();
            }
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO charts (sha256, notes, title, subtitle, source, course, level,
                    song_key, title_en, subtitle_en, difficulty, osu_beatmap_id, osu_beatmapset_id)
                VALUES ($sha, $notes, $title, $subtitle, $source, $course, $level,
                    $song_key, $title_en, $subtitle_en, $difficulty, $beatmap, $beatmapset)
                """;
            command.Parameters.AddWithValue("$sha", play.ChartSha256);
            command.Parameters.AddWithValue("$notes", Convert.FromBase64String(chart.Notes));
            command.Parameters.AddWithValue("$title", (object?)chart.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$subtitle", (object?)chart.Subtitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)chart.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$course", chart.Course);
            command.Parameters.AddWithValue("$level", (object?)chart.Level ?? DBNull.Value);
            command.Parameters.AddWithValue("$song_key", (object?)chart.SongKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$title_en", (object?)chart.TitleEn ?? DBNull.Value);
            command.Parameters.AddWithValue("$subtitle_en", (object?)chart.SubtitleEn ?? DBNull.Value);
            command.Parameters.AddWithValue("$difficulty", (object?)chart.Difficulty ?? DBNull.Value);
            command.Parameters.AddWithValue("$beatmap", (object?)chart.OsuBeatmapId ?? DBNull.Value);
            command.Parameters.AddWithValue("$beatmapset", (object?)chart.OsuBeatmapsetId ?? DBNull.Value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>
    /// Plays not yet on the server, oldest first: one player's, or everyone's (null: a cabinet). Local
    /// guest plays (baid 0) never leave the PC.
    /// </summary>
    public List<UploadPlay> PendingPlays(long? baid, int limit)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, baid, chart_sha256, mode, course, score, great, good, miss, max_combo, rolls, gauge,
                    cleared, scoring_version, engine_version, played_at, replay, audio_offset_ms, input_offset_ms
                FROM plays WHERE ($baid IS NULL OR baid = $baid) AND baid <> 0 AND uploaded_at IS NULL
                ORDER BY played_at LIMIT $limit
                """;
            command.Parameters.AddWithValue("$baid", (object?)baid ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var plays = new List<UploadPlay>();
            while (reader.Read())
                plays.Add(new UploadPlay(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetBoolean(12),
                    reader.GetInt32(13), reader.GetString(14),
                    DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                    Convert.ToBase64String((byte[])reader[16]))
                {
                    AudioOffsetMs = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    InputOffsetMs = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                });
            return plays;
        }
    }

    public ChartUpload? Chart(string sha256)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT notes, title, subtitle, source, course, level,
                    title_en, subtitle_en, difficulty, osu_beatmap_id, osu_beatmapset_id, song_key
                FROM charts WHERE sha256 = $sha
                """;
            command.Parameters.AddWithValue("$sha", sha256);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            string? text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            return new ChartUpload(Convert.ToBase64String((byte[])reader[0]), text(1), text(2), text(3),
                reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5))
            {
                TitleEn = text(6),
                SubtitleEn = text(7),
                Difficulty = text(8),
                OsuBeatmapId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                OsuBeatmapsetId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                SongKey = text(11),
            };
        }
    }

    public void MarkUploaded(IEnumerable<Guid> ids)
    {
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE plays SET uploaded_at = $at WHERE id = $id";
            var at = command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var value in ids)
            {
                id.Value = value.ToString();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private void insertPlay(PlayRecord play, SqliteTransaction transaction)
    {
        var result = play.Result;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plays (id, baid, chart_sha256, chart_key, course, mode, score, great, good, miss,
                max_combo, rolls, gauge, cleared, scoring_version, engine_version, played_at, replay,
                audio_offset_ms, input_offset_ms)
            VALUES ($id, $baid, $sha, $key, $course, $mode, $score, $great, $good, $miss,
                $combo, $rolls, $gauge, $cleared, $version, $engine, $at, $replay, $audio_offset, $input_offset)
            """;
        command.Parameters.AddWithValue("$id", play.Id.ToString());
        command.Parameters.AddWithValue("$baid", play.Baid);
        command.Parameters.AddWithValue("$sha", play.ChartSha256);
        command.Parameters.AddWithValue("$key", play.Chart.ToString());
        command.Parameters.AddWithValue("$course", (int)result.Course);
        command.Parameters.AddWithValue("$mode", play.Mode);
        command.Parameters.AddWithValue("$score", result.Score);
        command.Parameters.AddWithValue("$great", result.Great);
        command.Parameters.AddWithValue("$good", result.Good);
        command.Parameters.AddWithValue("$miss", result.Miss);
        command.Parameters.AddWithValue("$combo", result.MaxCombo);
        command.Parameters.AddWithValue("$rolls", result.Rolls);
        command.Parameters.AddWithValue("$gauge", result.GaugeSegments);
        command.Parameters.AddWithValue("$cleared", result.Cleared);
        command.Parameters.AddWithValue("$version", play.ScoringVersion);
        command.Parameters.AddWithValue("$engine", play.EngineVersion);
        command.Parameters.AddWithValue("$at", play.PlayedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$replay", play.Replay);
        command.Parameters.AddWithValue("$audio_offset", play.AudioOffsetMs);
        command.Parameters.AddWithValue("$input_offset", play.InputOffsetMs);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The player's best crown per chart key: local plays, and the server's bests for charts whose hash
    /// is known here. ponytail: a chart edited since it was hashed keeps its old hash until played or
    /// re-hashed (the cache has no file stamps).
    /// </summary>
    public Dictionary<string, TaikoCrown> Crowns(long baid)
    {
        lock (_connection)
            return crowns(baid);
    }

    private Dictionary<string, TaikoCrown> crowns(long baid)
    {
        var crowns = new Dictionary<string, TaikoCrown>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT chart_key, MAX(crown) FROM (
                SELECT chart_key, cleared + (cleared AND miss = 0) AS crown FROM plays WHERE baid = $baid
                UNION ALL
                SELECT hashes.chart_key, bests.crown FROM remote_bests bests
                JOIN chart_hashes hashes ON hashes.sha256 = bests.sha256 WHERE bests.baid = $baid
            ) GROUP BY chart_key
            """;
        command.Parameters.AddWithValue("$baid", baid);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (reader.GetInt32(1) > 0)
                crowns[reader.GetString(0)] = (TaikoCrown)reader.GetInt32(1);
        return crowns;
    }

    /// <summary>The chart keys already hashed (as strings).</summary>
    public HashSet<string> HashedCharts()
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT chart_key FROM chart_hashes";
            using var reader = command.ExecuteReader();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                keys.Add(reader.GetString(0));
            return keys;
        }
    }

    public string? ChartHashOf(string chartKey)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT sha256 FROM chart_hashes WHERE chart_key = $key";
            command.Parameters.AddWithValue("$key", chartKey);
            return command.ExecuteScalar() as string;
        }
    }

    public void SaveChartHashes(IEnumerable<(string ChartKey, string Sha256)> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO chart_hashes (chart_key, sha256) VALUES ($key, $sha)";
            var key = command.Parameters.Add("$key", SqliteType.Text);
            var sha = command.Parameters.Add("$sha", SqliteType.Text);
            foreach (var (chartKey, sha256) in hashes)
            {
                key.Value = chartKey;
                sha.Value = sha256;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>Replaces a player's server bests with a fresh download.</summary>
    public void ReplaceRemoteBests(long baid, IEnumerable<RemoteBest> bests)
    {
        ArgumentNullException.ThrowIfNull(bests);
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM remote_bests WHERE baid = $baid";
                clear.Parameters.AddWithValue("$baid", baid);
                clear.ExecuteNonQuery();
            }
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO remote_bests (baid, sha256, score, crown) VALUES ($baid, $sha, $score, $crown)";
            command.Parameters.AddWithValue("$baid", baid);
            var sha = command.Parameters.Add("$sha", SqliteType.Text);
            var score = command.Parameters.Add("$score", SqliteType.Integer);
            var crown = command.Parameters.Add("$crown", SqliteType.Integer);
            foreach (var best in bests)
            {
                sha.Value = best.Sha256;
                score.Value = best.Score;
                crown.Value = best.Crown;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// The player's best score on a chart before <paramref name="excluding"/> (the play just saved):
    /// local plays and the server's best. Null when there is none.
    /// </summary>
    public long? PreviousBest(long baid, string sha256, Guid excluding)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT MAX(score) FROM (
                    SELECT score FROM plays WHERE baid = $baid AND chart_sha256 = $sha AND id != $id
                    UNION ALL
                    SELECT score FROM remote_bests WHERE baid = $baid AND sha256 = $sha
                )
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$sha", sha256);
            command.Parameters.AddWithValue("$id", excluding.ToString());
            return command.ExecuteScalar() is long best ? best : null;
        }
    }

    public void Dispose() => _connection.Dispose();

    private void migrate()
    {
        using var version = _connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current > Migrations.Length)
            throw new InvalidDataException($"{FileName} is from a newer Waddamburo (schema {current}).");
        for (var step = current; step < Migrations.Length; step++)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"{Migrations[step]}\nPRAGMA user_version = {step + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
