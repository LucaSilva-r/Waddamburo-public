using Waddamburo.Game.Dan;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Hosting;

/// <summary>
/// The game-side hosts of the movies on stage, as <see cref="LayerHostFactory"/> builds them: what the flows poll
/// (Song Select's requests, the revival's outcome, the entry's cards). Each is null until its movie loads, and
/// stays set after it is gone (a flow checks the scene it is in).
/// </summary>
internal sealed class LoadedHosts
{
    public EntrySceneHost? Entry { get; set; }
    public SongSelectHostBinding? SongSelect { get; set; }
    public DaniSelectHostBinding? DaniSelect { get; set; }

    /// <summary>The course behind each of the course list's boards (null: the back board).</summary>
    public IReadOnlyList<DanCourseStatus?> DanBoards { get; set; } = [];

    public ModeTutorialHostBinding? ModeTutorial { get; set; }

    /// <summary>The Waiwai results on stage (null after normal results load).</summary>
    public WaiwaiResultHostBinding? WaiwaiResult { get; set; }
    public DaniResultHostBinding? DaniResult { get; set; }
    public AiSectionResultBinding? AiSectionResult { get; set; }
    public AiRemindBinding? AiRemind { get; set; }

    /// <summary>AI battle's results backdrop (result_bg).</summary>
    public LumenPlayer? AiResultBackground { get; set; }
    public RetryGameHostBinding? Retry { get; set; }
    public GameOverHostBinding? GameOver { get; set; }
    public RewardShopHostBinding? RewardShop { get; set; }
    public AttractHostBinding? Attract { get; set; }
}
