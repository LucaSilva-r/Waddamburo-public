using Waddamburo.App.Scenes;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Flow;

/// <summary>
/// Who is playing this credit: the joined drums (0 left, 1 right), the songs played, the player a replay shows in
/// a drum's place, and an online song played as two players. The panels, Song Select's name boards and the
/// gameplay Don slots follow it, and the later scenes' hosts read it.
/// </summary>
internal sealed class PlayerSession(GameShell shell)
{
    /// <summary>The credit's joined drums (0 left, 1 right).</summary>
    public SortedSet<int> JoinedSides { get; } = [];

    /// <summary>Songs played this credit.</summary>
    // A new credit (0) offers the tutorial again.
    public int SongsPlayed
    {
        get;
        set
        {
            field = value;
            if (value == 0)
                shell.Hosts.TutorialSeen = false;
        }
    }

    // The drum's own player while a replay shows another's (restored back in Song Select).
    private (int Side, ScoreProfile? Profile)? _watchedOver;

    /// <summary>An online song and its results play as two players (see <see cref="PlayOnlineAsTwoPlayers"/>).</summary>
    public bool OnlineTwoPlayers { get; private set; }

    /// <summary>A drum joins the credit. At home its player becomes the one online (the first of two; a guest is nobody).</summary>
    public void Join(int side)
    {
        JoinedSides.Add(side);
        apply();
        if (shell.Arcade.Home)
            shell.Sync.SetOnlineAccount(JoinedSides.Select(static joined => TaikoGuest.Profiles[joined]?.Token).FirstOrDefault(static token => token is not null));
    }

    /// <summary>A new credit: guests on every side (cards and the home account attach as players join), nobody joined.</summary>
    public void Reset()
    {
        shell.Room?.Leave();
        // A credit starts with guests (default Dons), with the website's current looks once they attach.
        if (!shell.Arcade.Home)
            shell.Hosts.ForgetLastSong();
        Array.Clear(TaikoGuest.Profiles);
        Array.Clear(TaikoGuest.Options);
        Array.Clear(TaikoGuest.Tones);
        Array.Clear(TaikoGuest.Mania);
        Array.Clear(TaikoGuest.LastCourse);
        shell.Sync.RefreshProfiles();
        for (var slot = 0; slot < 3; slot++)
            shell.Don?.SetLook(slot, null);
        JoinedSides.Clear();
        _rightAsLeft = false;
        shell.Application.SoleDrum = null;
        apply();
    }

    /// <summary>A replay shows its player on <paramref name="side"/>: their name boards and Don, through its results.</summary>
    public void WatchAs(int side, ScoreProfile player)
    {
        _watchedOver ??= (side, TaikoGuest.Profiles[side]);
        TaikoGuest.Profiles[side] = player;
        shell.Don?.SetLook(side, player.Look);
    }

    /// <summary>The drum's own player while a replay shows another (null when none is watched).</summary>
    public ScoreProfile? Watcher => _watchedOver is var (_, profile) ? profile : null;

    /// <summary>The drum's own player is back after a replay (nothing when none was watched).</summary>
    public void StopWatching()
    {
        if (_watchedOver is not var (side, profile))
            return;
        _watchedOver = null;
        TaikoGuest.Profiles[side] = profile;
        shell.Don?.SetLook(side, profile?.Look);
    }

    /// <summary>
    /// An online song and its results run as two players at the drums do (shutter, Don-chans, results), with the partner on
    /// the other drum; Song Select stays one player's, with the partner shown beside them.
    /// </summary>
    public void PlayOnlineAsTwoPlayers(bool on)
    {
        OnlineTwoPlayers = on;
        shell.Hosts.TwoPlayers = on || JoinedSides.Count == 2;
        if (shell.Don is not null)
            shell.Don.GameplaySide = on ? 0 : shell.Hosts.PlayerSide;
    }

    /// <summary>The sides Song Select shows a player on: the joined ones, and an online partner's.</summary>
    public int[] SongSelectSides => [.. (JoinedSides.Count == 0 ? [shell.Hosts.PlayerSide] : JoinedSides)
        .Concat(shell.Hosts.PartnerSide is { } partner ? [partner] : []).Distinct()];

    /// <summary>AI-don's whole costume (traced cos_149000).</summary>
    private const int AiDonCostume = 149;

    /// <summary>The entry's mode select picked the credit's mode: AI battle or the normal song select.</summary>
    public void ChooseMode(bool aiBattle)
    {
        var changed = shell.Hosts.AiBattle != aiBattle;
        shell.Hosts.AiBattle = aiBattle;
        // AI battle is one drum's: a lone right-drum player plays as player 1 (the recomp: movie player 0, the top
        // lane, the left drum's sounds), their drum read as the left one; the other drum is AI-don's and does nothing.
        var move = aiBattle && JoinedSides is { Count: 1, Min: 1 };
        if (move || !aiBattle && _rightAsLeft)
        {
            swapSides();
            _rightAsLeft = move;
            changed = true;
        }
        shell.Application.SoleDrum = aiBattle ? _rightAsLeft ? 1 : 0 : null;
        // A new credit in the same mode: the other drum's look may have been reset since (a new entry).
        if (!changed && !aiBattle) return;
        // The other drum's Don is AI-don (traced: Don slot p1 in the whole costume cos_149000, from song select on).
        var other = 1 - (JoinedSides.Count > 0 ? JoinedSides.Min : shell.Hosts.PlayerSide); // apply() sets PlayerSide below
        if (aiBattle)
        {
            shell.Don?.SetLook(other, null);
            shell.Don?.SetCostume(other, Waddamburo.Game.Don.DonCostume.FromWhole(AiDonCostume));
        }
        else
            shell.Don?.SetLook(other, null);
        if (changed)
            apply();
    }

    // A lone right-drum player playing AI battle as player 1 (their data moved to the left drum's slot).
    private bool _rightAsLeft;

    private void swapSides()
    {
        static void swap<T>(T[] sides) => (sides[0], sides[1]) = (sides[1], sides[0]);
        swap(TaikoGuest.Profiles);
        swap(TaikoGuest.Options);
        swap(TaikoGuest.Tones);
        swap(TaikoGuest.Mania);
        swap(TaikoGuest.LastCourseStar);
        swap(TaikoGuest.LastCourse);
        var side = JoinedSides.Min;
        JoinedSides.Clear();
        JoinedSides.Add(1 - side);
        for (var slot = 0; slot < 2; slot++)
            shell.Don?.SetLook(slot, TaikoGuest.Profiles[slot]?.Look);
    }

    private void apply()
    {
        var two = JoinedSides.Count == 2;
        var first = JoinedSides.Count == 0 ? 0 : JoinedSides.Min;
        shell.Hosts.PlayerSide = first;
        shell.Hosts.TwoPlayers = two;
        // Two players start in Waiwai's song select (traced); one player only has the normal one.
        shell.Hosts.Waiwai = two && !shell.Hosts.AiBattle;
        if (shell.Indicators is { } indicators)
        {
            indicators.Side = first;
            indicators.TwoPlayers = two;
            indicators.AnyJoined = JoinedSides.Count > 0;
            indicators.AiBattle = shell.Hosts.AiBattle;
        }
        if (shell.Don is not null) shell.Don.GameplaySide = first;
        shell.Catalog.Replace(FlowScenes.SongSelectScene(SongSelectSides, shell.Hosts.Waiwai, shell.Hosts.AiBattle));
    }
}
