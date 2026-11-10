using Waddamburo.Game.Dan;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Scores;

public enum TaikoCrown { None, Clear, FullCombo }

/// <summary>A player's best on one chart, as the server keeps it (crown 0 none, 1 clear, 2 full combo).</summary>
public sealed record RemoteBest(string Sha256, long Score, int Crown);

/// <summary>One of a player's last plays on the server (any machine): its chart, course and time.</summary>
public sealed record RemotePlay(string Sha256, int Course, DateTimeOffset PlayedAt);

/// <summary>A play as the server takes it (snake_case JSON); the replay is base64.</summary>
public sealed record UploadPlay(Guid Id, long Baid, string ChartSha256, string Mode, int Course, long Score,
    int Great, int Good, int Miss, int MaxCombo, int Rolls, int Gauge, bool Cleared, int ScoringVersion,
    string EngineVersion, DateTimeOffset PlayedAt, string Replay)
{
    /// <summary>The player's audio offset during the play (null: recorded before it was kept).</summary>
    public int? AudioOffsetMs { get; init; }

    /// <summary>The player's input offset during the play; the replay's times already have it applied.</summary>
    public int? InputOffsetMs { get; init; }

    /// <summary>The play options (<see cref="Gameplay.TaikoPlayOptions.Bits"/>; 0 none).</summary>
    public int Options { get; init; }

    /// <summary>The random options' seed (null: none drawn).</summary>
    public int? Seed { get; init; }

    /// <summary>AI battle: won, sections ('W'/'L' each), support level, rank points earned.</summary>
    public bool? AiWon { get; init; }

    public string? AiSections { get; init; }

    public int? AiSupport { get; init; }

    public int? AiPoints { get; init; }

    public Guid? AiSessionId { get; init; }

    /// <summary>A battle played without a server setup (offline): what AI-don started from, for the server to replay it.</summary>
    public string? AiBaseSha256 { get; init; }

    public double? AiPriorMedianMs { get; init; }

    public double? AiPriorVariance { get; init; }

    /// <summary>The player's hit timing in the play: median offset (ms, late positive) and variance (ms²).</summary>
    public double? HitMedianMs { get; init; }

    public double? HitVariance { get; init; }
}

/// <summary>
/// An AI battle play's battle (mode "ai_battle"): won by most sections, each section's outcome ('W'/'L'), the support level
/// (0 = いつもの) and the rank points it earned. Rank, wins and points are derived from these, never kept apart.
/// </summary>
public sealed record AiBattlePlay(bool Won, string Sections, int SupportLevel, int Points)
{
    public Guid? SessionId { get; init; }
    public bool Ranked { get; init; }

    /// <summary>The unsimplified chart's hash (a support level's base; the played chart's own at いつもの).</summary>
    public string? BaseSha256 { get; init; }

    /// <summary>AI-don's starting timing (median ms, variance ms²); null: the model's own.</summary>
    public (double Median, double Variance)? Prior { get; init; }
}

/// <summary>One of the player's AI battles on the server (any machine).</summary>
public sealed record RemoteAiPlay(Guid Id, string Sha256, bool Won, int Points, double? HitMedianMs, double? HitVariance, DateTimeOffset PlayedAt);

/// <summary>A player's AI battle standing: rank points, wins, and their latest hit timing (median ms, variance ms²).</summary>
public sealed record AiProgress(int Points, int Wins, (double Median, double Variance)? Timing)
{
    public static AiProgress None { get; } = new(0, 0, null);
}

/// <summary>A stored play to rescore: its chart's gzipped canonical notes and source, options, seed and replay.</summary>
public sealed record OutdatedPlay(Guid Id, byte[] Notes, string? Source, int Options, int? Seed, byte[] Replay);

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
        // osu!stable uploads as OsuLazer (see ServerSource): same chart id, same rules, one leaderboard.
        var source = chart.Key.Song.Source.ServerSource();
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
    public const int CurrentScoringVersion = 9; // 5: a big note hit is always a big hit; 6: the other hand's press right after is part of it; 7: ka popped balloons (reverted); 8: don only again; 9: Go-Go by the note's window, branch and osu! gauge scaling

    public int ScoringVersion { get; init; } = CurrentScoringVersion;

    public static string CurrentEngineVersion { get; } =
        typeof(PlayRecord).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public string EngineVersion { get; init; } = CurrentEngineVersion;

    /// <summary>The audio offset setting the play was made with (context for its replay; judgement does not use it).</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>The input offset setting the play was made with; the replay's input times already include it.</summary>
    public int InputOffsetMs { get; init; }

    /// <summary>The seed the random options were drawn with (its replay needs the same notes).</summary>
    public int? Seed { get; init; }

    /// <summary>AI battle plays only.</summary>
    public AiBattlePlay? Battle { get; init; }

    /// <summary>The player's hit timing in the play (median ms, variance ms²); null without hits.</summary>
    public (double Median, double Variance)? Timing { get; init; }
}

/// <summary>A course play as the server takes it: each song's chart hash and replay (base64).</summary>
public sealed record UploadDanPlay(Guid Id, long Baid, string CourseHash, int ResultLevel, long Score, int Great, int Good, int Miss,
    int MaxCombo, int Rolls, int GaugePercent, int SongsPlayed, int ScoringVersion, string EngineVersion, int AudioOffsetMs,
    int InputOffsetMs, DateTimeOffset PlayedAt, List<UploadDanSong> Songs);

/// <summary>One song of an uploaded course play.</summary>
public sealed record UploadDanSong(string Sha256, string Replay);

/// <summary>A finished dan course: each song's chart hash (null: not known) and replay, on that song's own time.</summary>
public sealed record DanPlayRecord(Guid Id, long Baid, DanCourseResult Result, IReadOnlyList<string?> ChartHashes,
    DateTimeOffset PlayedAt, IReadOnlyList<byte[]> Replays)
{
    public int AudioOffsetMs { get; init; }

    public int InputOffsetMs { get; init; }

    public int ScoringVersion { get; init; } = PlayRecord.CurrentScoringVersion;

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
        // The play options (TaikoPlayOptions.Bits) and the random options' seed.
        """
        ALTER TABLE plays ADD COLUMN options INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE plays ADD COLUMN seed INTEGER;
        """,
        // A dan course's plays: three charts, one replay on the course's timeline. Apart from plays, so they
        // never count as a song's best or reach the song upload. uploaded_at: for when they are sent.
        """
        CREATE TABLE dan_plays (
            id TEXT PRIMARY KEY,
            baid INTEGER NOT NULL,
            course_hash TEXT NOT NULL,
            level INTEGER NOT NULL,
            result_level INTEGER NOT NULL,
            score INTEGER NOT NULL,
            great INTEGER NOT NULL,
            good INTEGER NOT NULL,
            miss INTEGER NOT NULL,
            max_combo INTEGER NOT NULL,
            rolls INTEGER NOT NULL,
            gauge_percent INTEGER NOT NULL,
            songs_played INTEGER NOT NULL,
            songs TEXT NOT NULL,
            replay BLOB NOT NULL,
            scoring_version INTEGER NOT NULL,
            engine_version TEXT NOT NULL,
            audio_offset_ms INTEGER NOT NULL,
            input_offset_ms INTEGER NOT NULL,
            played_at TEXT NOT NULL,
            uploaded_at TEXT
        );
        CREATE INDEX dan_plays_player ON dan_plays (baid, course_hash);
        """,
        // Each song's replay on its own, in songs: nothing about the course's timeline is kept or sent.
        """
        ALTER TABLE dan_plays DROP COLUMN replay;
        """,
        // Failed plays are no longer kept.
        """
        DELETE FROM plays WHERE cleared = 0;
        DELETE FROM dan_plays WHERE result_level = 0;
        """,
        // The library's hashes again, from the chart reader as it is now: branched charts read differently since they
        // were cached (a dan course's song showed as another chart, locking the course). Add another such step whenever
        // a reader change moves charts' hashes.
        """
        DELETE FROM chart_hashes;
        """,
        // The player's last plays on the server (any machine), for the play history.
        """
        CREATE TABLE IF NOT EXISTS remote_plays (baid INTEGER NOT NULL, sha256 TEXT NOT NULL, played_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS remote_plays_player ON remote_plays (baid);
        """,
        // AI battle plays' battles (rank and wins derive from them) and every play's hit timing (AI-don learns it),
        // plus the player's AI battles from the server (any machine).
        """
        ALTER TABLE plays ADD COLUMN ai_won INTEGER;
        ALTER TABLE plays ADD COLUMN ai_sections TEXT;
        ALTER TABLE plays ADD COLUMN ai_support INTEGER;
        ALTER TABLE plays ADD COLUMN ai_points INTEGER;
        ALTER TABLE plays ADD COLUMN hit_median REAL;
        ALTER TABLE plays ADD COLUMN hit_variance REAL;
        CREATE TABLE remote_ai_plays (
            id TEXT PRIMARY KEY,
            baid INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            won INTEGER NOT NULL,
            points INTEGER NOT NULL,
            hit_median REAL,
            hit_variance REAL,
            played_at TEXT NOT NULL
        );
        CREATE INDEX remote_ai_plays_player ON remote_ai_plays (baid);
        """,
        """
        ALTER TABLE plays ADD COLUMN ai_session_id TEXT;
        ALTER TABLE plays ADD COLUMN ai_ranked INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE plays ADD COLUMN ai_base_sha256 TEXT;
        ALTER TABLE plays ADD COLUMN ai_prior_median REAL;
        ALTER TABLE plays ADD COLUMN ai_prior_variance REAL;
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
            insertChart(play.ChartSha256, chart, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Plays not yet on the server, oldest first: one player's, or everyone's (null: a cabinet). Local
    /// guest plays (baid 0) and failed plays never leave the PC, except AI battles (a failed one can still be a win).
    /// </summary>
    public List<UploadPlay> PendingPlays(long? baid, int limit)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, baid, chart_sha256, mode, course, score, great, good, miss, max_combo, rolls, gauge,
                    cleared, scoring_version, engine_version, played_at, replay, audio_offset_ms, input_offset_ms,
                    options, seed, ai_won, ai_sections, ai_support, ai_points, hit_median, hit_variance, ai_session_id,
                    ai_base_sha256, ai_prior_median, ai_prior_variance
                FROM plays WHERE ($baid IS NULL OR baid = $baid) AND baid <> 0 AND (cleared = 1 OR mode = 'ai_battle')
                    AND uploaded_at IS NULL
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
                    Options = reader.GetInt32(19),
                    Seed = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                    AiWon = reader.IsDBNull(21) ? null : reader.GetBoolean(21),
                    AiSections = reader.IsDBNull(22) ? null : reader.GetString(22),
                    AiSupport = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                    AiPoints = reader.IsDBNull(24) ? null : reader.GetInt32(24),
                    HitMedianMs = reader.IsDBNull(25) ? null : reader.GetDouble(25),
                    HitVariance = reader.IsDBNull(26) ? null : reader.GetDouble(26),
                    AiSessionId = reader.IsDBNull(27) ? null : Guid.Parse(reader.GetString(27)),
                    AiBaseSha256 = reader.IsDBNull(28) ? null : reader.GetString(28),
                    AiPriorMedianMs = reader.IsDBNull(29) ? null : reader.GetDouble(29),
                    AiPriorVariance = reader.IsDBNull(30) ? null : reader.GetDouble(30),
                });
            return plays;
        }
    }

    /// <summary>
    /// A player's failed plays on a chart, newest first: kept on this PC only (see <see cref="PendingPlays"/>),
    /// with their replays, for the score list's own plays.
    /// </summary>
    public List<(ScoreRow Row, TaikoReplay Replay)> LocalOnlyPlays(long baid, string sha256)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, course, score, great, good, miss, max_combo, rolls, gauge, played_at, replay,
                    audio_offset_ms, input_offset_ms, options, seed
                FROM plays WHERE baid = $baid AND chart_sha256 = $sha AND cleared = 0 ORDER BY played_at DESC
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$sha", sha256);
            using var reader = command.ExecuteReader();
            var plays = new List<(ScoreRow, TaikoReplay)>();
            while (reader.Read())
                plays.Add((new ScoreRow
                {
                    PlayId = Guid.Parse(reader.GetString(0)),
                    Baid = baid,
                    Course = reader.GetInt32(1),
                    Score = reader.GetInt64(2),
                    Great = reader.GetInt32(3),
                    Good = reader.GetInt32(4),
                    Miss = reader.GetInt32(5),
                    MaxCombo = reader.GetInt32(6),
                    Rolls = reader.GetInt32(7),
                    Gauge = reader.GetInt32(8),
                    PlayedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                    AudioOffsetMs = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    InputOffsetMs = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    Options = reader.GetInt32(13),
                    Seed = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                    LocalOnly = true,
                }, TaikoReplay.Decode((byte[])reader[10])));
            return plays;
        }
    }

    /// <summary>
    /// Plays scored under older rules than <paramref name="version"/>, with what rescoring them needs: the
    /// chart's stored notes (gzipped canonical form) and its source.
    /// </summary>
    public List<OutdatedPlay> OutdatedPlays(int version)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT plays.id, charts.notes, charts.source, plays.options, plays.seed, plays.replay
                FROM plays JOIN charts ON charts.sha256 = plays.chart_sha256
                WHERE plays.scoring_version < $version ORDER BY plays.played_at
                """;
            command.Parameters.AddWithValue("$version", version);
            using var reader = command.ExecuteReader();
            var plays = new List<OutdatedPlay>();
            while (reader.Read())
                plays.Add(new OutdatedPlay(Guid.Parse(reader.GetString(0)), (byte[])reader[1],
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), (byte[])reader[5]));
            return plays;
        }
    }

    /// <summary>Keeps a play's numbers as rescored under <paramref name="version"/>.</summary>
    public void UpdateScore(Guid id, TaikoPlayResult result, int version)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE plays SET course = $course, score = $score, great = $great, good = $good, miss = $miss,
                    max_combo = $max_combo, rolls = $rolls, gauge = $gauge, cleared = $cleared, scoring_version = $version
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$course", (int)result.Course);
            command.Parameters.AddWithValue("$score", result.Score);
            command.Parameters.AddWithValue("$great", result.Great);
            command.Parameters.AddWithValue("$good", result.Good);
            command.Parameters.AddWithValue("$miss", result.Miss);
            command.Parameters.AddWithValue("$max_combo", result.MaxCombo);
            command.Parameters.AddWithValue("$rolls", result.Rolls);
            command.Parameters.AddWithValue("$gauge", result.GaugeSegments);
            command.Parameters.AddWithValue("$cleared", result.Cleared);
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
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

    private void insertChart(string sha256, ChartUpload chart, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO charts (sha256, notes, title, subtitle, source, course, level,
                song_key, title_en, subtitle_en, difficulty, osu_beatmap_id, osu_beatmapset_id)
            VALUES ($sha, $notes, $title, $subtitle, $source, $course, $level,
                $song_key, $title_en, $subtitle_en, $difficulty, $beatmap, $beatmapset)
            """;
        command.Parameters.AddWithValue("$sha", sha256);
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
    }

    private void insertPlay(PlayRecord play, SqliteTransaction transaction)
    {
        var result = play.Result;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plays (id, baid, chart_sha256, chart_key, course, mode, score, great, good, miss,
                max_combo, rolls, gauge, cleared, scoring_version, engine_version, played_at, replay,
                audio_offset_ms, input_offset_ms, options, seed, ai_won, ai_sections, ai_support, ai_points,
                hit_median, hit_variance, ai_session_id, ai_ranked, ai_base_sha256, ai_prior_median, ai_prior_variance)
            VALUES ($id, $baid, $sha, $key, $course, $mode, $score, $great, $good, $miss,
                $combo, $rolls, $gauge, $cleared, $version, $engine, $at, $replay, $audio_offset, $input_offset,
                $options, $seed, $ai_won, $ai_sections, $ai_support, $ai_points, $hit_median, $hit_variance, $ai_session_id, $ai_ranked, $ai_base, $ai_prior_median, $ai_prior_variance)
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
        command.Parameters.AddWithValue("$options", result.Options.Bits);
        command.Parameters.AddWithValue("$seed", (object?)play.Seed ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_won", (object?)play.Battle?.Won ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_sections", (object?)play.Battle?.Sections ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_support", (object?)play.Battle?.SupportLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_points", (object?)play.Battle?.Points ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_session_id", (object?)play.Battle?.SessionId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_ranked", play.Battle?.Ranked == true);
        command.Parameters.AddWithValue("$ai_base", (object?)play.Battle?.BaseSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_prior_median", (object?)play.Battle?.Prior?.Median ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_prior_variance", (object?)play.Battle?.Prior?.Variance ?? DBNull.Value);
        command.Parameters.AddWithValue("$hit_median", (object?)play.Timing?.Median ?? DBNull.Value);
        command.Parameters.AddWithValue("$hit_variance", (object?)play.Timing?.Variance ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A player's AI battle standing, from their AI battles on this PC and on the server (each counted once, by id): rank
    /// points, wins, and the hit timing of the latest that has one.
    /// </summary>
    public AiProgress AiProgress(long baid)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT won, points, median, variance FROM (
                    SELECT CASE WHEN ai_ranked = 1 AND uploaded_at IS NULL THEN ai_won ELSE 0 END AS won,
                        CASE WHEN ai_ranked = 1 AND uploaded_at IS NULL THEN ai_points ELSE 0 END AS points,
                        hit_median AS median, hit_variance AS variance, played_at
                    FROM plays WHERE baid = $baid AND mode = 'ai_battle'
                        AND id NOT IN (SELECT id FROM remote_ai_plays WHERE baid = $baid)
                    UNION ALL
                    SELECT won, points, hit_median, hit_variance, played_at FROM remote_ai_plays
                    WHERE baid = $baid)
                ORDER BY played_at DESC
                """;
            command.Parameters.AddWithValue("$baid", baid);
            using var reader = command.ExecuteReader();
            int points = 0, wins = 0;
            (double, double)? timing = null;
            while (reader.Read())
            {
                wins += !reader.IsDBNull(0) && reader.GetBoolean(0) ? 1 : 0;
                points += reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                if (timing is null && !reader.IsDBNull(2) && !reader.IsDBNull(3))
                    timing = (reader.GetDouble(2), reader.GetDouble(3));
            }
            return new(points, wins, timing);
        }
    }

    /// <summary>The player has won an AI battle on this chart before (here or on the server).</summary>
    public bool HasAiWin(long baid, string sha256)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM plays WHERE baid = $baid AND chart_sha256 = $sha AND mode = 'ai_battle' AND ai_won = 1
                    AND ai_ranked = 1 AND uploaded_at IS NULL
                    AND id NOT IN (SELECT id FROM remote_ai_plays WHERE baid = $baid))
                    OR EXISTS (SELECT 1 FROM remote_ai_plays WHERE baid = $baid AND sha256 = $sha AND won = 1)
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$sha", sha256);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
    }

    /// <summary>Replaces a player's AI battles from the server with a fresh download.</summary>
    public void ReplaceRemoteAiPlays(long baid, IEnumerable<RemoteAiPlay> plays)
    {
        ArgumentNullException.ThrowIfNull(plays);
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM remote_ai_plays WHERE baid = $baid";
                clear.Parameters.AddWithValue("$baid", baid);
                clear.ExecuteNonQuery();
            }
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO remote_ai_plays (id, baid, sha256, won, points, hit_median, hit_variance, played_at)
                VALUES ($id, $baid, $sha, $won, $points, $median, $variance, $at)
                """;
            command.Parameters.AddWithValue("$baid", baid);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var sha = command.Parameters.Add("$sha", SqliteType.Text);
            var won = command.Parameters.Add("$won", SqliteType.Integer);
            var points = command.Parameters.Add("$points", SqliteType.Integer);
            var median = command.Parameters.Add("$median", SqliteType.Real);
            var variance = command.Parameters.Add("$variance", SqliteType.Real);
            var at = command.Parameters.Add("$at", SqliteType.Text);
            foreach (var play in plays)
            {
                id.Value = play.Id.ToString();
                sha.Value = play.Sha256;
                won.Value = play.Won;
                points.Value = play.Points;
                median.Value = (object?)play.HitMedianMs ?? DBNull.Value;
                variance.Value = (object?)play.HitVariance ?? DBNull.Value;
                at.Value = play.PlayedAt.ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
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
    /// <summary>Forgets every stored chart hash: the library is hashed again (Settings > Song Libraries).</summary>
    public void ForgetChartHashes()
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM chart_hashes";
            command.ExecuteNonQuery();
        }
    }

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
    private void replaceRemotePlays(long baid, IEnumerable<RemotePlay> recent, SqliteTransaction transaction)
    {
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM remote_plays WHERE baid = $baid";
            clear.Parameters.AddWithValue("$baid", baid);
            clear.ExecuteNonQuery();
        }
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO remote_plays (baid, sha256, played_at) VALUES ($baid, $sha, $at)";
        command.Parameters.AddWithValue("$baid", baid);
        var sha = command.Parameters.Add("$sha", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        foreach (var play in recent)
        {
            sha.Value = play.Sha256;
            at.Value = play.PlayedAt.ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A player's play history, newest first, as chart keys: this PC's plays (failed ones too) merged with
    /// the server's last plays from any machine (charts this PC has hashed). A play on both counts once per
    /// chart and time; the caller keeps one entry per song.
    /// </summary>
    public List<string> RecentCharts(long baid, int limit = 200)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT chart_key, played_at FROM (SELECT chart_key, played_at FROM plays WHERE baid = $baid
                    ORDER BY played_at DESC LIMIT $limit)
                UNION ALL
                SELECT hashes.chart_key, remote.played_at FROM remote_plays remote
                JOIN chart_hashes hashes ON hashes.sha256 = remote.sha256 WHERE remote.baid = $baid
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var plays = new List<(string Chart, DateTimeOffset At)>();
            while (reader.Read())
                plays.Add((reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
            return [.. plays.Distinct().OrderByDescending(static play => play.At).Select(static play => play.Chart)];
        }
    }

    public void ReplaceRemoteBests(long baid, IEnumerable<RemoteBest> bests, IEnumerable<RemotePlay>? recent = null)
    {
        ArgumentNullException.ThrowIfNull(bests);
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            if (recent is not null)
                replaceRemotePlays(baid, recent, transaction);
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

    /// <summary>Saves a course play. Its charts stay out: the server gave their hashes, so it has them.</summary>
    public void SaveDan(DanPlayRecord play)
    {
        ArgumentNullException.ThrowIfNull(play);
        var result = play.Result;
        var total = result.Total;
        var songs = songsJson(play);
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dan_plays (id, baid, course_hash, level, result_level, score, great, good, miss, max_combo,
                    rolls, gauge_percent, songs_played, songs, scoring_version, engine_version,
                    audio_offset_ms, input_offset_ms, played_at)
                VALUES ($id, $baid, $hash, $level, $result, $score, $great, $good, $miss, $combo,
                    $rolls, $gauge, $played, $songs, $scoring, $engine, $audio, $input, $at)
                """;
            command.Parameters.AddWithValue("$id", play.Id.ToString());
            command.Parameters.AddWithValue("$baid", play.Baid);
            command.Parameters.AddWithValue("$hash", result.Course.Hash);
            command.Parameters.AddWithValue("$level", result.Course.Level);
            command.Parameters.AddWithValue("$result", result.Level);
            command.Parameters.AddWithValue("$score", total.Score);
            command.Parameters.AddWithValue("$great", total.Goods);
            command.Parameters.AddWithValue("$good", total.Oks);
            command.Parameters.AddWithValue("$miss", total.Bads);
            command.Parameters.AddWithValue("$combo", total.MaxCombo);
            command.Parameters.AddWithValue("$rolls", total.Rolls);
            command.Parameters.AddWithValue("$gauge", total.GaugePercent);
            command.Parameters.AddWithValue("$played", result.SongsPlayed);
            command.Parameters.AddWithValue("$songs", songs);
            command.Parameters.AddWithValue("$scoring", play.ScoringVersion);
            command.Parameters.AddWithValue("$engine", play.EngineVersion);
            command.Parameters.AddWithValue("$audio", play.AudioOffsetMs);
            command.Parameters.AddWithValue("$input", play.InputOffsetMs);
            command.Parameters.AddWithValue("$at", play.PlayedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private static string songsJson(DanPlayRecord play)
    {
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            var songs = play.Result.Songs;
            for (var index = 0; index < songs.Count; index++)
            {
                json.WriteStartObject();
                json.WriteString("sha256", index < play.ChartHashes.Count ? play.ChartHashes[index] : null);
                json.WriteNumber("course", songs[index].Course);
                json.WriteBase64String("replay", index < play.Replays.Count ? play.Replays[index] : []);
                json.WriteNumber("great", songs[index].Counts.Goods);
                json.WriteNumber("good", songs[index].Counts.Oks);
                json.WriteNumber("miss", songs[index].Counts.Bads);
                json.WriteNumber("rolls", songs[index].Counts.Rolls);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Course plays not yet on the server; a guest's stay on this PC.</summary>
    public List<UploadDanPlay> PendingDanPlays(long? baid, int limit)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, baid, course_hash, result_level, score, great, good, miss, max_combo, rolls, gauge_percent,
                    songs_played, scoring_version, engine_version, audio_offset_ms, input_offset_ms, played_at, songs
                FROM dan_plays WHERE ($baid IS NULL OR baid = $baid) AND baid <> 0 AND uploaded_at IS NULL
                ORDER BY played_at LIMIT $limit
                """;
            command.Parameters.AddWithValue("$baid", (object?)baid ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var plays = new List<UploadDanPlay>();
            while (reader.Read())
            {
                using var songs = System.Text.Json.JsonDocument.Parse(reader.GetString(17));
                plays.Add(new UploadDanPlay(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9),
                    reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetString(13), reader.GetInt32(14), reader.GetInt32(15),
                    DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                    [.. songs.RootElement.EnumerateArray().Select(static song => new UploadDanSong(
                        song.GetProperty("sha256").GetString() ?? "", song.GetProperty("replay").GetString() ?? ""))]));
            }
            return plays;
        }
    }

    public void MarkDanUploaded(IEnumerable<Guid> ids)
    {
        lock (_connection)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE dan_plays SET uploaded_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var value in ids)
            {
                id.Value = value.ToString();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>The player's best result on each course played, by course hash: 1 red, 2 gold.</summary>
    public Dictionary<string, int> DanBests(long baid)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT course_hash, MAX(result_level) FROM dan_plays WHERE baid = $baid GROUP BY course_hash";
            command.Parameters.AddWithValue("$baid", baid);
            using var reader = command.ExecuteReader();
            var bests = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read())
                bests[reader.GetString(0)] = reader.GetInt32(1);
            return bests;
        }
    }

    /// <summary>
    /// The player's best score on a chart before <paramref name="excluding"/> (the play just saved):
    /// local plays and the server's best. Null when there is none. 真打 plays have their own bests
    /// (another scale; the server's best here is the normal one).
    /// </summary>
    public long? PreviousBest(long baid, string sha256, Guid excluding, bool shinuchi = false)
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT MAX(score) FROM (
                    SELECT score FROM plays WHERE baid = $baid AND chart_sha256 = $sha AND id != $id
                        AND ((options & 2) <> 0) = $shinuchi
                    UNION ALL
                    SELECT score FROM remote_bests WHERE baid = $baid AND sha256 = $sha AND NOT $shinuchi
                )
                """;
            command.Parameters.AddWithValue("$baid", baid);
            command.Parameters.AddWithValue("$sha", sha256);
            command.Parameters.AddWithValue("$id", excluding.ToString());
            command.Parameters.AddWithValue("$shinuchi", shinuchi);
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
