using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Dan;

/// <summary>
/// The dan dojo's credit: the series on show, the course being played (a challenge's number), the finished run's
/// result, each player's bests, and keeping a passed run (it uploads with the next sync; the server decides the badge).
/// The courses themselves are the library's (the app's DanCourses, matched against TaikOnline's catalogue).
/// </summary>
public sealed class DanModule(CreditState credit, Func<ScoreStore?> scores)
{
    /// <summary>The series the course list shows.</summary>
    public string Series { get; set; } = "green";

    /// <summary>The challenge course being played: its number in <see cref="Series"/>; 0: a rank.</summary>
    public int Challenge { get; set; }

    /// <summary>The course run the next results show; null: a song's.</summary>
    public DanCourseResult? Result { get; set; }

    /// <summary>The playing drum's player's best result per course hash (the course list's stamps).</summary>
    public IReadOnlyDictionary<string, int> Bests() => TaikoGuest.Profiles[credit.PlayerSide] is { } player && scores() is { } store
        ? store.DanBests(player.Baid) : new Dictionary<string, int>();

    /// <summary>
    /// Keeps a finished run (failed runs aren't kept, here or on the server). False: not kept (failed, no store, or the
    /// store refused it, logged: a lost score must not end the credit).
    /// </summary>
    public bool Keep(DanPlayRecord record)
    {
        if (record.Result.Level == 0 || scores() is not { } store)
            return false;
        try
        {
            store.SaveDan(record);
            return true;
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
            return false;
        }
    }
}
