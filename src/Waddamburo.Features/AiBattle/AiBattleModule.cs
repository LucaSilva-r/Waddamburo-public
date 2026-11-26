using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.AiBattle;

/// <summary>
/// AI battle: the installation's ranks and AI-don levels, each player's standing, what a battle earns (rank points,
/// first-win and high-level bonuses) and the last battle's results. The lanes and the AI-don simulation are the
/// engine's (<see cref="AiBattleProgress"/>, <see cref="AiBattleSimulation"/>).
/// </summary>
/// <param name="home">At home a drum without a player plays as this PC's guest.</param>
public sealed class AiBattleModule(CreditState credit, Func<bool> home, Func<ScoreStore?> scores)
{
    /// <summary>The rank ladder (the installation's ghostinfo.xml; Green's when it has none).</summary>
    public AiBattleRanks Ranks { get; set; } = AiBattleRanks.Green;

    /// <summary>AI-don's levels (ghosttuning.xml; empty when the data has none).</summary>
    public IReadOnlyList<GhostLevel> Levels { get; set; } = [];

    /// <summary>False for a Green from before the update that added AI battle: no frontend offers it.</summary>
    public bool Installed { get; set; } = true;

    /// <summary>A drum's player (at home a guest is this PC's); null: nobody.</summary>
    public ScoreProfile? Player(int side) => TaikoGuest.Profiles[side] ?? (home() ? ScoreProfile.LocalGuest : null);

    /// <summary>A drum's player's standing (rank points, wins, last timing); none without a player or store.</summary>
    public AiProgress Standing(int side) => Player(side) is { } profile && scores() is { } store ? store.AiProgress(profile.Baid) : AiProgress.None;

    /// <summary>The playing drum's standing.</summary>
    public AiProgress Standing() => Standing(credit.PlayerSide);

    /// <summary>The battle the next results show; null: not a battle.</summary>
    public AiBattleOutcome? Outcome { get; set; }

    /// <summary>The results' standing (rank, points, wins before and after the battle).</summary>
    public AiResultNumbers? Numbers { get; private set; }

    /// <summary>The player's per-section bests before the battle the results show; null: a first play.</summary>
    public IReadOnlyList<AiSectionCounts>? SectionBest { get; private set; }

    /// <summary>A song ended: the last battle's numbers go (the next results set their own).</summary>
    public void Forget() => (Numbers, SectionBest) = (null, null);

    /// <summary>
    /// A finished battle on the player's lane, saved with the play: the rank points it earns on the standing before it
    /// (a server setup's, else the store's). Sets the results' numbers.
    /// </summary>
    /// <param name="uploads">A signed-in player's battle: it uploads and plays Green's ranks (the server replays it).</param>
    /// <param name="usual">The course's own chart, not a simplified support level.</param>
    public AiBattlePlay Record(AiBattleOutcome outcome, ScoreProfile profile, ScoreStore store, string sha, bool cleared,
        TaikoCourse course, int stars, bool uploads, bool usual, AiBattleSession? session, int supportLevel)
    {
        var ranks = uploads ? AiBattleRanks.Green : Ranks;
        var before = session is { } setup ? new AiProgress(setup.Points, setup.Wins, setup.Timing) : store.AiProgress(profile.Baid);
        // Offline, a signed-in player's battle counts provisionally; the server verifies it on upload.
        var ranked = session?.Ranked ?? uploads;
        var award = ranks.Award(before.Points, course, stars, outcome.Won, cleared, usual: usual && ranked, firstWin: !store.HasAiWin(profile.Baid, sha));
        SectionBest = store.AiSectionBest(profile.Baid, sha);
        var rank = ranks.RankOf(before.Points);
        Numbers = new(rank, ranks.RankOf(before.Points + award.Points), ranks.PointsIntoRank(before.Points),
            award.Points, award.FirstWin, award.HighLevel, ranks.StepSize(rank), before.Wins, AiBattleRanks.NextRewardWins(before.Wins));
        return new AiBattlePlay(outcome.Won, string.Concat(outcome.Sections.Select(static section => section.Won ? 'W' : 'L')),
            supportLevel, award.Points)
        {
            SessionId = session?.Id, Ranked = ranked,
            SectionCounts = AiSectionCounts.Format(outcome.Sections.Select(static section => section.Player)),
        };
    }
}
