using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Rewards;

/// <summary>
/// A carded player's medal screen: their drum, medals before this credit and what it paid, and the rewards to hand
/// over (Green's TYPE_SONG 1 / SUIT 3 / BODY 4 / HEAD 5).
/// </summary>
/// <remarks><paramref name="Alone"/>: No game over with it (home's "New rewards").</remarks>
public sealed record MedalScreen(int Side, int MedalsBefore, MedalCredit Credit, IReadOnlyList<PendingReward>? Rewards = null, bool Alone = false);

/// <summary>
/// Don Medals and rewards: what a credit's end pays and hands over (asked of TaikOnline while the last results play),
/// and the rewards a player has waiting (home's "New rewards"). The server hears they were shown once the screen ends.
/// </summary>
/// <remarks>
/// <paramref name="serverFor"/>: A player's server and baid; null: offline.<br/>
/// <paramref name="scores"/>: The score store a credit's end uploads from; null: none.<br/>
/// </remarks>
public sealed class RewardsModule(CreditState credit, Func<ScoreProfile, (ScoreClient Client, long? Baid)?> serverFor, Func<ScoreStore?> scores)
{
    private Task<(int Side, List<PendingReward> Rewards)>? _creditEnd;
    private Action? _seen;

    /// <summary>The medal screen to show; null: none.</summary>
    public MedalScreen? Showing { get; private set; }

    /// <summary>The first joined drum whose player has rewards waiting on a server; null: none.</summary>
    public int? PendingSide(IEnumerable<int> joined) => joined.Cast<int?>()
        .FirstOrDefault(side => TaikoGuest.Profiles[side!.Value] is { PendingRewards.Count: > 0 } profile && serverFor(profile) is not null);

    /// <summary>
    /// An arcade credit is ending: the first carded drum on a server uploads its songs, then TaikOnline checks the
    /// credit's titles, in the background (the medal screen shows them if the answer is in by then).
    /// </summary>
    public void AskCreditEnd(IEnumerable<int> joined) => _creditEnd = ask(joined);

    private Task<(int Side, List<PendingReward> Rewards)>? ask(IEnumerable<int> joined)
    {
        var creditId = credit.CreditId;
        foreach (var side in joined)
        {
            if (TaikoGuest.Profiles[side] is not { Baid: > 0 } profile || serverFor(profile) is not var (client, baid)
                || scores() is not { } store)
                continue;
            return Task.Run(async () =>
            {
                try
                {
                    return (side, await client.CreditEndAsync(store, baid, creditId).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"Warning REWARDS: the credit's rewards did not come ({exception.Message}).");
                    return (side, new List<PendingReward>());
                }
            });
        }
        return null;
    }

    /// <summary>
    /// The arcade credit's end: the first carded player (a server with medals) gets the credit's Don Medals with its
    /// bonuses, as Green paid them from the last paid day, and the rewards if TaikOnline answered in time. False:
    /// nobody to show. ponytail: one carded player; a second card's medals are paid by the server but not shown.
    /// </summary>
    public bool PayCreditEnd(IEnumerable<int> joined)
    {
        // A revival's end asks only now: its rewards wait for the next credit.
        var endRewards = _creditEnd ?? ask(joined);
        _creditEnd = null;
        foreach (var side in joined)
        {
            if (TaikoGuest.Profiles[side] is not { Baid: > 0, Medals: { } medals } profile)
                continue;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var paid = DonMedals.ForCredit(DateOnly.TryParse(profile.LastMedalDay, CultureInfo.InvariantCulture, out var last) ? last : null, today);
            var rewards = endRewards is { IsCompletedSuccessfully: true, Result: var (rewardSide, pending) } && rewardSide == side ? pending : [];
            Showing = new MedalScreen(side, medals, paid, rewards);
            _seen = rewards.Count > 0 ? seen(profile, rewards) : null;
            // Until the next card read brings the server's: the next credit on this cabinet sees this one.
            TaikoGuest.Profiles[side] = profile with { Medals = medals + paid.Medals, LastMedalDay = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            return true;
        }
        return false;
    }

    /// <summary>Home's "New rewards": the first waiting player's rewards (no medals paid). False: none.</summary>
    /// <remarks>ponytail: one player's rewards per visit; a second player's wait for the next.</remarks>
    public bool ShowPending(IEnumerable<int> joined)
    {
        if (PendingSide(joined) is not { } side || TaikoGuest.Profiles[side] is not { PendingRewards: { } rewards } profile
            || seen(profile, rewards) is not { } tell)
            return false;
        Showing = new MedalScreen(side, profile.Medals ?? 0, new MedalCredit(0, false, false, false), rewards, Alone: true);
        _seen = () =>
        {
            if (TaikoGuest.Profiles[side] is { } shown && shown.Baid == profile.Baid)
                TaikoGuest.Profiles[side] = shown with { PendingRewards = null };
            tell();
        };
        return true;
    }

    /// <summary>Shows a medal screen as it is (diagnostic start scenes).</summary>
    public void Show(MedalScreen screen) => Showing = screen;

    /// <summary>The medal screen is over: the server hears its rewards were shown.</summary>
    public void Shown()
    {
        _seen?.Invoke();
        _seen = null;
        Showing = null;
    }

    private Action? seen(ScoreProfile profile, List<PendingReward> rewards) => serverFor(profile) is var (client, baid)
        ? () => _ = Task.Run(async () =>
        {
            try
            {
                await client.RewardsSeenAsync(rewards, baid).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Warning REWARDS: the server did not hear the rewards were shown ({exception.Message}).");
            }
        })
        : null;
}
