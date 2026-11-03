using System.Globalization;

namespace Waddamburo.App.Flow;

/// <summary>
/// The cabinet's running totals for the service menu's BOOKKEEPING page, kept in waddamburo/bookkeeping.cfg
/// (key = value): time switched on and in songs, credits played (and those on free play), coins, power-ons.
/// </summary>
internal sealed class Bookkeeping(string? path)
{
    public const string FileName = "bookkeeping.cfg";

    public long PowerOnTicks { get; set; }
    public long PlayTicks { get; set; }
    public int PlayCount { get; set; }
    public int CoinCount { get; set; }
    public int FreePlayCount { get; set; }
    public int PowerOnCount { get; set; }

    /// <summary>The file's counts (none when it is missing or unreadable).</summary>
    public static Bookkeeping Load(string? path)
    {
        var book = new Bookkeeping(path);
        if (path is null || !File.Exists(path))
            return book;
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (line.Split('=', 2) is [var key, var value] && long.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var number))
                    switch (key.Trim())
                    {
                        case "power_on_ticks": book.PowerOnTicks = number; break;
                        case "play_ticks": book.PlayTicks = number; break;
                        case "play_count": book.PlayCount = (int)number; break;
                        case "coin_count": book.CoinCount = (int)number; break;
                        case "free_play_count": book.FreePlayCount = (int)number; break;
                        case "power_on_count": book.PowerOnCount = (int)number; break;
                    }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning BOOKKEEPING_LOAD: {exception.Message}");
        }
        return book;
    }

    /// <summary>A credit started (<paramref name="free"/>: on free play).</summary>
    public void CountCredit(bool free)
    {
        PlayCount++;
        if (free)
            FreePlayCount++;
    }

    public void Reset() => PowerOnTicks = PlayTicks = PlayCount = CoinCount = FreePlayCount = PowerOnCount = 0;

    public void Save()
    {
        if (path is null) return;
        try
        {
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                $"# Waddamburo cabinet bookkeeping (the service menu's BOOKKEEPING page). Times in 60 Hz ticks.\n"
                + $"power_on_ticks = {PowerOnTicks}\nplay_ticks = {PlayTicks}\nplay_count = {PlayCount}\n"
                + $"coin_count = {CoinCount}\nfree_play_count = {FreePlayCount}\npower_on_count = {PowerOnCount}\n"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning BOOKKEEPING_SAVE: {exception.Message}");
        }
    }
}
