namespace Waddamburo.Game.Scores;

/// <summary>A chart's top three after a play, and the play's place on it (-1: not on it).</summary>
public sealed record RankingPlacement(IReadOnlyList<RankingEntry> Top, int RankIn);

public static class RankingBoard
{
    public const int Size = 3;

    /// <summary>
    /// The board the results screen shows (traced: the top three with this play counted, and its
    /// slot). A player holds one line, their best; a play only ranks in when it beats it. Ties keep
    /// the earlier line ahead.
    /// </summary>
    public static RankingPlacement Place(IReadOnlyList<RankingEntry> top, long baid, string name, int score)
    {
        ArgumentNullException.ThrowIfNull(top);
        if (top.Any(line => line.Baid == baid && line.Score >= score))
            return new RankingPlacement([.. top.Take(Size)], -1);
        var lines = top.Where(line => line.Baid != baid).ToList();
        var rank = lines.FindIndex(line => line.Score < score);
        if (rank < 0)
            rank = lines.Count;
        lines.Insert(rank, new RankingEntry(baid, name, score));
        return new RankingPlacement([.. lines.Take(Size)], rank < Size ? rank : -1);
    }
}
