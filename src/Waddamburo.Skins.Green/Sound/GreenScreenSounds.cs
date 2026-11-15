namespace Waddamburo.Skins.Green.Sound;

/// <summary>The game's screens' sound controllers over its sound tree (its movies ask for bank cues).</summary>
public sealed class GreenScreenSounds(GreenSounds green, Action stopAll)
{
    public GreenSounds Green { get; } = green;
    public AttractSounds Attract { get; } = new(green);
    public FrontendSounds Frontend { get; } = new(green);
    public ResultSounds Results { get; } = new(green);
    public WaiwaiResultSounds WaiwaiResults { get; } = new(green);
    public RetrySounds Retry { get; } = new(green, stopAll);
    public GameOverSounds GameOver { get; } = new(green);
}
