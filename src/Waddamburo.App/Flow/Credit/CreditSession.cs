using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Flow.Credit;

/// <summary>A card's reward shop: this season's goods and the card's medals, and buying goods by number.</summary>
internal sealed record CreditShop(Task<ShopState> State, Func<int, Task<ShopPurchase>> Purchase);

/// <summary>The credit's mode, picked at the entry (AI battle) or by the players joined (Waiwai for two).</summary>
internal enum CreditMode { Normal, Waiwai, AiBattle }

/// <summary>
/// The credit's own state: its mode, who joined on which drum, what the entry starts from, the tutorials
/// watched, the songs played, and what a finished play hands its results scene. Flows and hosts read it here;
/// no flow keeps another's state (docs/development/app-refactor.md).
/// </summary>
internal sealed class CreditSession
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

    /// <summary>The carded player whose Don Medals the credit's end shows (reward_shop over the game over); null: none.</summary>
    public RewardShopPlayer? RewardPlayer { get; set; }

    /// <summary>
    /// An arcade credit's last results ask TaikOnline for the rewards its titles gave (a carded drum's): Green's medal
    /// screen shows them if the answer is in by then. Null: none asked.
    /// </summary>
    public Task<(int Side, List<Waddamburo.Game.Scores.PendingReward> Rewards)>? EndRewards { get; set; }

    /// <summary>Each drum's card's reward shop (fetched at the card read; the entry offers it once one is in); null: none.</summary>
    public CreditShop?[] Shops { get; } = new CreditShop?[2];

    /// <summary>The shop's goods: a card's whose shop is in (the season's goods are the same for every card).</summary>
    public ShopState? ShopGoods => Shops.FirstOrDefault(static shop => shop?.State.IsCompletedSuccessfully == true)?.State.Result;

    /// <summary>
    /// The reward shop scene's goods: the mode select offers the shop before a card's fetch is in, so this waits for one
    /// still running (up to 10 s, the scene is loading anyway). ponytail: none came in (the fetch failed): an empty
    /// shop to look at, not an error message.
    /// </summary>
    public ShopState WaitForShopGoods()
    {
        var pending = Shops.Where(static shop => shop is not null && !shop.State.IsCompleted).Select(static shop => (Task)shop!.State).ToArray();
        if (ShopGoods is null && pending.Length > 0)
            Task.WaitAny(pending, TimeSpan.FromSeconds(10));
        return ShopGoods ?? new ShopState(0, "", 0, []);
    }

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
                Array.Clear(Shops);
            }
        }
    }

    /// <summary>The tutorial was watched this credit: Song Select drops its folder (one flag per song select).</summary>
    public bool TutorialSeen { get; set; }
    public bool WaiwaiTutorialSeen { get; set; }
    public bool AiTutorialSeen { get; set; }

    // --- The next entry ---

    /// <summary>SetPrevious trigger for the next entry: SCENE_TRIGGER_DON_1P (0) or _COIN (3).</summary>
    public int EntryTrigger { get; set; }

    /// <summary>The next entry is the home player setup (see <see cref="EntrySceneHost.SetupMode"/>).</summary>
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

    // --- Dan ---

    /// <summary>The series dani_select lists.</summary>
    public string DanSeries { get; set; } = "green";

    /// <summary>The challenge course being played: its number in <see cref="DanSeries"/>; 0: a rank.</summary>
    public int DanChallenge { get; set; }

    // --- What a finished play hands its results ---

    /// <summary>The song's genre and stage (the results and song_info show them).</summary>
    public TaikoSongInfo SongInfo { get; set; } = new(0, 1);

    /// <summary>Per lane, the best before the last saved play on its chart, or that play's score when it is the first.</summary>
    public long?[] Bests { get; } = new long?[2];

    /// <summary>Each lane's place on the chart's online top three after its play (null: not known).</summary>
    public RankingPlacement?[] Placements { get; } = new RankingPlacement?[2];

    public long? Best(int lane) => (uint)lane < (uint)Bests.Length ? Bests[lane] : null;

    public RankingPlacement? Placement(int lane) => (uint)lane < (uint)Placements.Length ? Placements[lane] : null;


    /// <summary>The AI battle song the next results show; null: not a battle.</summary>
    public AiBattleOutcome? AiOutcome { get; set; }

    /// <summary>The AI battle results' standing (rank, points, wins before and after the battle).</summary>
    public AiResultNumbers? AiNumbers { get; set; }

    /// <summary>The player's per-section bests before the battle the results show; null: a first play.</summary>
    public IReadOnlyList<AiSectionCounts>? AiSectionBest { get; set; }

    /// <summary>The course play the next results show; null: a song's.</summary>
    public DanCourseResult? DanResult { get; set; }
}
