namespace Waddamburo.Game.Scores;

/// <summary>A player's Banapass profile: whose plays are saved, and their Don.</summary>
public sealed record ScoreProfile(long Baid, string Name)
{
    /// <summary>A home PC's guest: plays are kept locally under this baid and never uploaded.</summary>
    public const long LocalGuestBaid = 0;

    public static ScoreProfile LocalGuest { get; } = new(LocalGuestBaid, "");

    public Don.DonLook? Look { get; init; }

    /// <summary>The account's custom Don-chan picture (a transparent PNG on the server), for the picker.</summary>
    public string? Avatar { get; init; }

    /// <summary>The website account's display name (Latin letters too, unlike the Don-chan's); null for none.</summary>
    public string? AccountName { get; init; }

    /// <summary>The name board's title (empty: none) and its plate (player_name TITLE_LABEL: 0 normal, 1 rainbow, 2 gold, 3 platinum).</summary>
    public string? Title { get; init; }

    public int TitlePlate { get; init; }

    /// <summary>The equipped nameplate rank (0 hidden, 1-25 Beginner through Tatsujin).</summary>
    public int DanRank { get; init; }

    public bool DanGold { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int DisplayDanRank => DanRank is >= 1 and <= 25 ? DanRank : 0;

    /// <summary>What the home entry's name board shows: the account name, else the Don-chan's.</summary>
    public string DisplayName => AccountName is { Length: > 0 } account ? account : Name;

    /// <summary>The token its plays upload with at home (a stored account's, or a friend's short-lived one).</summary>
    public string? Token { get; init; }

    /// <summary>Don Medals to spend; null from a server without medals.</summary>
    public int? Medals { get; init; }

    /// <summary>The day (yyyy-MM-dd) of the last credit that paid medals; null for none yet.</summary>
    public string? LastMedalDay { get; init; }

    /// <summary>"Only what I earned": what stays locked for this player (null or empty: everything is unlocked).</summary>
    public ProfileLocks? Locked { get; init; }
}

/// <summary>
/// What the server keeps locked in "Only what I earned" (TaikOnline's WaddamburoUnlocks): the reward shop's goods not bought
/// yet. Stock songs by musicid, costume parts by Green's ids. Custom libraries never lock.
/// </summary>
public sealed record ProfileLocks(List<string> Songs, List<int> Kigurumi, List<int> Head, List<int> Body, List<int> Puchi)
{
    /// <summary>Goods just bought (reward shop type: 1 song by musicid, 3 kigurumi, 4 body, 5 head, 7 puchi) no longer lock.</summary>
    public ProfileLocks Unlock(int type, int id, string? musicId) => type switch
    {
        1 => this with { Songs = [.. Songs.Where(song => song != musicId)] },
        3 => this with { Kigurumi = [.. Kigurumi.Where(item => item != id)] },
        4 => this with { Body = [.. Body.Where(item => item != id)] },
        5 => this with { Head = [.. Head.Where(item => item != id)] },
        7 => this with { Puchi = [.. Puchi.Where(item => item != id)] },
        _ => this,
    };
}

/// <summary>One line of a chart's server ranking: a player's best.</summary>
public sealed record RankingEntry(long Baid, string Name, int Score);

/// <summary>A chart's best crown, as the server numbers them (Dondaful 3: 全良, no 可 and no 不可).</summary>
public enum TaikoCrown { None, Clear, FullCombo, Dondaful }
