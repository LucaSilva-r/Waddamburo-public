using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The game's sounds from the user's sound tree: one shared bank, one cue map per scene.</summary>
internal sealed class GameSounds
{
    public GameSounds(AudioEngine audio, string soundRoot)
    {
        Bank = new SoundBank(audio, soundRoot);
        Attract = new AttractSounds(Bank);
        Frontend = new FrontendSounds(Bank);
        Gameplay = new GameplaySounds(Bank);
        Results = new ResultSounds(Bank);
        WaiwaiResults = new WaiwaiResultSounds(Bank);
        Retry = new RetrySounds(Bank, StopAll);
        GameOver = new GameOverSounds(Bank);
        RewardShop = new RewardShopSounds(Bank, GameOver);
    }

    public SoundBank Bank { get; }
    public AttractSounds Attract { get; }
    public FrontendSounds Frontend { get; }
    public GameplaySounds Gameplay { get; }
    public ResultSounds Results { get; }
    public WaiwaiResultSounds WaiwaiResults { get; }
    public RetrySounds Retry { get; }
    public GameOverSounds GameOver { get; }
    public RewardShopSounds RewardShop { get; }

    /// <summary>The scenes' music, decoded in the background and kept: it starts at once, however slow the storage.</summary>
    // ponytail: the scene classes' own names listed again; a missing one only starts late, as without this.
    public void PreloadMusic() => Bank.PreloadMusic("JINGLE_ENTRY", "JINGLE_SEISEKI", "JINGLE_RENDA", "JINGLE_GOVER", "JINGLE_GOHOBI",
        "JINGLE_WAIRSLT", "JINGLE_AIBTL_RSLT", "JINGLE_AIBTL_REMIND", "JINGLE_DANI");

    /// <summary>Everything but the coin stops, and the next credit starts fresh.</summary>
    public void StopAll()
    {
        Bank.StopAll();
    }
}
