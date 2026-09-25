using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Scores;

public enum TaikoCrown { None, Clear, FullCombo }

/// <summary>A play as the server takes it (snake_case JSON); the replay is base64.</summary>
public sealed record UploadPlay(Guid Id, long Baid, string ChartSha256, string Mode, int Course, long Score,
    int Great, int Good, int Miss, int MaxCombo, int Rolls, int Gauge, bool Cleared, int ScoringVersion,
    string EngineVersion, DateTimeOffset PlayedAt, string Replay);

/// <summary>A chart import: gzipped canonical notes (<see cref="ChartHash.Serialize"/>), base64, plus display metadata.</summary>
public sealed record ChartUpload(string Notes, string? Title, string? Subtitle, string? Source, int Course, int? Level)
{
    public static ChartUpload From(PlayableChart chart, TaikoCourse course, string? title, string? subtitle)
    {
        ArgumentNullException.ThrowIfNull(chart);
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            gzip.Write(ChartHash.Serialize(chart, course));
        return new ChartUpload(Convert.ToBase64String(output.ToArray()), title, subtitle,
            chart.Key.Song.Source.ToString(), (int)course, chart.Level);
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
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO charts (sha256, notes, title, subtitle, source, course, level)
                VALUES ($sha, $notes, $title, $subtitle, $source, $course, $level)
                """;
            command.Parameters.AddWithValue("$sha", play.ChartSha256);
            command.Parameters.AddWithValue("$notes", Convert.FromBase64String(chart.Notes));
            command.Parameters.AddWithValue("$title", (object?)chart.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$subtitle", (object?)chart.Subtitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)chart.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$course", chart.Course);
            command.Parameters.AddWithValue("$level", (object?)chart.Level ?? DBNull.Value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>The player's plays not yet on the server, oldest first.</summary>
    public List<UploadPlay> PendingPlays(long baid, int limit)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, baid, chart_sha256, mode, course, score, great, good, miss, max_combo, rolls, gauge,
                    cleared, scoring_version, engine_version, played_at, replay
                FROM plays WHERE baid = $baid AND uploaded_at IS NULL ORDER BY played_at LIMIT $limit
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var plays = new List<UploadPlay>();
            while (reader.Read())
                plays.Add(new UploadPlay(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetBoolean(12),
                    reader.GetInt32(13), reader.GetString(14),
                    DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                    Convert.ToBase64String((byte[])reader[16])));
            return plays;
        }
    }

    public ChartUpload? Chart(string sha256)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT notes, title, subtitle, source, course, level FROM charts WHERE sha256 = $sha";
            command.Parameters.AddWithValue("$sha", sha256);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            string? text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            return new ChartUpload(Convert.ToBase64String((byte[])reader[0]), text(1), text(2), text(3),
                reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5));
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
                max_combo, rolls, gauge, cleared, scoring_version, engine_version, played_at, replay)
            VALUES ($id, $baid, $sha, $key, $course, $mode, $score, $great, $good, $miss,
                $combo, $rolls, $gauge, $cleared, $version, $engine, $at, $replay)
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
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The player's best crown per chart key (song select looks charts up by key without hashing
    /// them). ponytail: an edited chart keeps its old crown until played; a key-to-hash cache fixes that.
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
            SELECT chart_key, MAX(cleared + (cleared AND miss = 0)) FROM plays
            WHERE baid = $baid GROUP BY chart_key
            """;
        command.Parameters.AddWithValue("$baid", baid);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (reader.GetInt32(1) > 0)
                crowns[reader.GetString(0)] = (TaikoCrown)reader.GetInt32(1);
        return crowns;
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
