using Waddamburo.Game.Sound;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Skins.Green.Sound;

namespace Waddamburo.App.Audio;

/// <summary>
/// The sounds: the engine's player (<see cref="Bank"/>), the skin's set it asks by name, and the game's screens'
/// sounds (<see cref="Green"/>: its movies ask for bank cues).
/// </summary>
internal sealed class GameSounds
{
    /// <param name="green">The game's sound tree (its screens' movies ask for its cues).</param>
    /// <param name="set">What the engine's names play: a pack of ours or the user's over the game's (SoundPack).</param>
    public GameSounds(AudioEngine audio, GreenSoundSet green, ISoundSet set)
    {
        Set = set;
        Bank = new SoundBank(audio);
        Screens = new GreenScreenSounds(new GreenSounds(green, Bank), StopAll);
        Gameplay = new GameplaySounds(Bank, set);
    }

    /// <summary>The skin's sounds the engine asks for by name.</summary>
    public ISoundSet Set { get; }

    public SoundBank Bank { get; }
    /// <summary>The game's screens' controllers (their movies ask for bank cues).</summary>
    public GreenScreenSounds Screens { get; }
    public GreenSounds Green => Screens.Green;
    public AttractSounds Attract => Screens.Attract;
    public FrontendSounds Frontend => Screens.Frontend;
    public GameplaySounds Gameplay { get; }
    public ResultSounds Results => Screens.Results;
    public WaiwaiResultSounds WaiwaiResults => Screens.WaiwaiResults;
    public RetrySounds Retry => Screens.Retry;
    public GameOverSounds GameOver => Screens.GameOver;

    /// <summary>Plays the skin's sound for an engine name (nothing when the skin has none).</summary>
    public void Play(string name, bool trace = true) => Play(new SoundRequest(name), trace);

    public void Play(SoundRequest request, bool trace = true)
    {
        foreach (var cue in Set.Describe(request))
            Bank.Play(cue, trace: trace);
    }

    /// <summary>The engine's own menus' drum feedback.</summary>
    public void Menu(bool don) => Play(don ? "ui.don" : "ui.ka", trace: false);

    /// <summary>Streams the skin's track for an engine name.</summary>
    public SoundHandle? PlayMusic(string name, bool loop) => music(name) is { } track ? Bank.PlayMusic(track, loop) : null;

    /// <summary>Decodes the skin's track for an engine name now; what starts it.</summary>
    public Action? PrepareMusic(string name, bool loop) => music(name) is { } track ? Bank.PrepareMusic(track, loop) : null;

    private SoundSource? music(string name) => Set.Describe(new(name)) is [var first, ..] ? first.Source : null;

    /// <summary>A sound decoded, for mixing into a stream (the calibration's clicks); null without one.</summary>
    public AudioClip? Clip(string name) => Set.Describe(new(name)) is [var first, ..] ? Bank.Clip(first.Source) : null;

    /// <summary>A coin's (or a service credit's) sound on the coin channel; each takes a coin sound's length.</summary>
    public void PlayCoin(bool service)
    {
        if (Set.Describe(new("coin.insert")) is not [var coin, ..])
            return;
        var sound = service && Set.Describe(new("coin.service")) is [var credit, ..] ? credit : coin;
        Bank.PlayCoin(sound.Source, coin.Source);
    }

    public bool IsCoinPlaying => Bank.IsCoinPlaying;

    /// <summary>Everything but the coin stops, and the next credit starts fresh.</summary>
    public void StopAll()
    {
        Bank.StopAll();
    }
}
