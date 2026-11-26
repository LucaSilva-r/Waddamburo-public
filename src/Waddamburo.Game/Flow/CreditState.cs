using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Flow;

/// <summary>The credit's mode, picked at the entry (AI battle) or by the players joined (Waiwai for two).</summary>
public enum CreditMode { Normal, Waiwai, AiBattle }

/// <summary>
/// The credit's own state: its mode, who joined on which drum, what the entry starts from, the tutorials watched, the
/// songs played, and what a finished play hands its results screen. The flows set it; every skin's screens read it
/// (docs/development/app-refactor.md).
/// </summary>
public class CreditState
{
    public CreditMode Mode { get; set; }

    /// <summary>Two players browse Waiwai's song select (and may switch to the normal one).</summary>
    public bool Waiwai
    {
        get => Mode == CreditMode.Waiwai;
        set => Mode = value ? CreditMode.Waiwai : Mode == CreditMode.Waiwai ? CreditMode.Normal : Mode;
    }

    /// <summary>The credit is an AI battle (entry's mode select, SetNextScene(10)): its song select, battles and results.</summary>
    public bool AiBattle
    {
        get => Mode == CreditMode.AiBattle;
        set => Mode = value ? CreditMode.AiBattle : Mode == CreditMode.AiBattle ? CreditMode.Normal : Mode;
    }

    // --- Players ---

    /// <summary>The joined player's drum (0 left, 1 right; 0 for two players); scenes after entry follow it.</summary>
    public int PlayerSide { get; set; }

    /// <summary>This credit's id: its songs' Don Medals are paid once (a new one with each credit).</summary>
    public Guid CreditId { get; set; } = Guid.NewGuid();

    /// <summary>Both drums joined (or an online song played as two).</summary>
    public bool TwoPlayers { get; set; }

    /// <summary>An online partner's side in Song Select: their Don-chan and name board show there (null: none).</summary>
    public int? PartnerSide { get; set; }

    /// <summary>Songs played this credit. A new credit (0) offers the tutorials again.</summary>
    public int SongsPlayed
    {
        get;
        set
        {
            field = value;
            if (value == 0)
            {
                (TutorialSeen, WaiwaiTutorialSeen, AiTutorialSeen) = (false, false, false);
                Started?.Invoke();
            }
        }
    }

    /// <summary>A new credit began (<see cref="SongsPlayed"/> went back to 0): what the features keep per credit goes.</summary>
    public event Action? Started;

    /// <summary>The tutorial was watched this credit: Song Select drops its folder (one flag per song select).</summary>
    public bool TutorialSeen { get; set; }
    public bool WaiwaiTutorialSeen { get; set; }
    public bool AiTutorialSeen { get; set; }

    // --- The next entry ---

    /// <summary>SetPrevious trigger for the next entry: SCENE_TRIGGER_DON_1P (0) or _COIN (3).</summary>
    public int EntryTrigger { get; set; }

    /// <summary>The next entry is the home player setup (the drums are the setup's until it closes).</summary>
    public bool EntrySetup { get; set; }

    /// <summary>
    /// The next entry comes from Song Select (SCENE_SONGSELECT, 1) with this drum's player already in: the
    /// other drum joins there (its SetPrevious trigger, <see cref="EntryTrigger"/>). Null: from the attract.
    /// </summary>
    public int? EntryRejoined { get; set; }

    /// <summary>A card read in the attract loop, for the entry about to load.</summary>
    public ScoreProfile? EntryCard { get; set; }

    /// <summary>A card rejected in the attract loop: the entry about to load shows the red band.</summary>
    public bool EntryCardRejected { get; set; }

    // --- What a finished play hands its results ---

    /// <summary>The song's genre and stage (the results and song_info show them).</summary>
    public TaikoSongInfo SongInfo { get; set; } = new(0, 1);

    /// <summary>Per lane, the best before the last saved play on its chart, or that play's score when it is the first.</summary>
    public long?[] Bests { get; } = new long?[2];

    /// <summary>Each lane's place on the chart's online top three after its play (null: not known).</summary>
    public RankingPlacement?[] Placements { get; } = new RankingPlacement?[2];

    public long? Best(int lane) => (uint)lane < (uint)Bests.Length ? Bests[lane] : null;

    public RankingPlacement? Placement(int lane) => (uint)lane < (uint)Placements.Length ? Placements[lane] : null;

    /// <summary>Waiwai's numbers for its results screen, from the finished song.</summary>
    public Func<WaiwaiOutcome?>? WaiwaiOutcome { get; set; }

}
