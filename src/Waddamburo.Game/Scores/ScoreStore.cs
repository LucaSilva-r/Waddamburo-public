using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Scores;

public enum TaikoCrown { None, Clear, FullCombo }

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
    ];

    private readonly SqliteConnection _connection;

    public ScoreStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        migrate();
    }

    public void Save(PlayRecord play)
    {
        ArgumentNullException.ThrowIfNull(play);
        var result = play.Result;
        using var command = _connection.CreateCommand();
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
