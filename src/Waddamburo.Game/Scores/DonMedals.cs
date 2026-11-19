namespace Waddamburo.Game.Scores;

/// <summary>A credit's Don Medals as Green pays them: 100, plus 50/100/300 for the day's, week's and month's first credit.</summary>
public sealed record MedalCredit(int Medals, bool Daily, bool Weekly, bool Monthly);

/// <summary>Green's Don Medal rules (TaikOnline pays the same from the songs; the game shows them at the credit's end).</summary>
public static class DonMedals
{
    /// <summary>Shown by reward_shop as its limit (traced constant).</summary>
    public const int Cap = 7600;

    /// <summary>
    /// The credit played on <paramref name="today"/>: each bonus when the last paid credit came before the start of
    /// today, this week, this month (or there was none).
    /// </summary>
    public static MedalCredit ForCredit(DateOnly? lastCredit, DateOnly today)
    {
        // ponytail: weeks start on Monday; Green's own week start is not known (TaikOnline uses the same).
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var daily = lastCredit is not { } last || last < today;
        var weekly = lastCredit is not { } lastWeek || lastWeek < weekStart;
        var monthly = lastCredit is not { } lastMonth || lastMonth < new DateOnly(today.Year, today.Month, 1);
        return new(100 + (daily ? 50 : 0) + (weekly ? 100 : 0) + (monthly ? 300 : 0), daily, weekly, monthly);
    }

    /// <summary>
    /// Green's shop season for a day, by its windows' month/day (Spring 1 from 03-14, Summer 2 from 05-30, Fall 3
    /// from 09-04, Winter 4 from 12-05), with the telop TaikOnline's season data gives it.
    /// </summary>
    public static (int Id, string Telop) Season(DateOnly day)
    {
        var date = day.Month * 100 + day.Day;
        return date switch
        {
            >= 1205 or < 314 => (4, "Winter reward shop"),
            >= 904 => (3, "Fall reward shop"),
            >= 530 => (2, "Summer reward shop"),
            _ => (1, "Spring reward shop"),
        };
    }
}
