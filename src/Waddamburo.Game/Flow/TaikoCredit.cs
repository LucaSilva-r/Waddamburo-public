namespace Waddamburo.Game.Flow;

/// <summary>
/// Arcade credit rules (user-described, traced in research/traces/session4-revival.md): a credit plays
/// a fixed number of songs; failing the first one offers the drum-roll revival (retry_game), whose
/// loss ends the credit; later failures just move on.
/// </summary>
public enum TaikoCreditNext { NextSong, Revival, End }

public static class TaikoCredit
{
    /// <summary>
    /// What follows a song's results, per player cleared (one or two players). One player: a failed
    /// first song earns the revival drum roll. Two players have no revival: the credit ends only when
    /// both failed the first song (user-confirmed cabinet rule). Otherwise the credit plays
    /// <paramref name="songsPerSession"/> songs (config.cfg; the traced cabinet used 2).
    /// </summary>
    public static TaikoCreditNext Next(int stage, IReadOnlyList<bool> cleared, int songsPerSession) =>
        stage == 1 && !cleared.Any(static value => value)
            ? cleared.Count == 1 ? TaikoCreditNext.Revival : TaikoCreditNext.End
            : stage >= songsPerSession ? TaikoCreditNext.End : TaikoCreditNext.NextSong;

    /// <summary>
    /// result.lm SetEndMessage: 2 revival; 1 ("another chance") when one of two players failed the
    /// first song (traced session9-2p; first song only, user-confirmed); otherwise 0 (traced after a
    /// cleared song and at the credit's end).
    /// </summary>
    public static int EndMessage(int stage, IReadOnlyList<bool> cleared, int songsPerSession) =>
        Next(stage, cleared, songsPerSession) switch
        {
            TaikoCreditNext.Revival => 2,
            TaikoCreditNext.NextSong when stage == 1 && cleared.Count == 2 && cleared.Contains(false) => 1,
            _ => 0,
        };
}
