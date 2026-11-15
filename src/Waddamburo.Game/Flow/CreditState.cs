using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Flow;

/// <summary>
/// The credit's state the flows set before a screen loads (who plays where, the mode, what was seen, the results to
/// show): read by every skin's screens.
/// </summary>
public sealed class CreditState
{
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

    /// <summary>SetPrevious trigger for the next entry: SCENE_TRIGGER_DON_1P (0) or _COIN (3).</summary>
    public int EntryTrigger { get; set; }

    /// <summary>The joined player's drum (0 left, 1 right; 0 for two players); scenes after entry follow it.</summary>
    public int PlayerSide { get; set; }

    /// <summary>Both drums joined.</summary>
    public bool TwoPlayers { get; set; }

    /// <summary>An online partner's side in Song Select: their Don-chan and name board show there (null: none).</summary>
    public int? PartnerSide { get; set; }

    /// <summary>Two players browse Waiwai's song select (and may switch to the normal one).</summary>
    public bool Waiwai { get; set; }

    /// <summary>The credit is an AI battle (entry's mode select, SetNextScene(10)): its song select, battles and results.</summary>
    public bool AiBattle { get; set; }

    /// <summary>The tutorial was watched this credit: Song Select drops its folder.</summary>
    public bool TutorialSeen { get; set; }

    public bool WaiwaiTutorialSeen { get; set; }

    public bool AiTutorialSeen { get; set; }

    /// <summary>The series dani_select lists.</summary>
    public string DanSeries { get; set; } = "green";

    /// <summary>The challenge course being played: its number in <see cref="DanSeries"/>; 0: a rank.</summary>
    public int DanChallenge { get; set; }

    /// <summary>Waiwai's numbers for its results screen, from the finished song.</summary>
    public Func<WaiwaiOutcome?>? WaiwaiOutcome { get; set; }

    /// <summary>The AI battle song the next results show; null: not a battle.</summary>
    public AiBattleOutcome? AiOutcome { get; set; }

    /// <summary>The AI battle results' standing (rank, points, wins before and after the battle).</summary>
    public AiResultNumbers? AiNumbers { get; set; }

    /// <summary>The player's per-section bests before the battle the results show; null: a first play.</summary>
    public IReadOnlyList<AiSectionCounts>? AiSectionBest { get; set; }

    /// <summary>The course play the next results show; null: a song's.</summary>
    public DanCourseResult? DanResult { get; set; }
}
