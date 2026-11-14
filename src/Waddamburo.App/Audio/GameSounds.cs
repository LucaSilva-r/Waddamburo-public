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
    public GameSounds(AudioEngine audio, string soundRoot)
    {
        var set = new GreenSoundSet(soundRoot);
        Set = set;
        Bank = new SoundBank(audio);
        Green = new GreenSounds(set, Bank);
        Attract = new AttractSounds(Green);
        Frontend = new FrontendSounds(Green);
        Gameplay = new GameplaySounds(Bank, set);
        Results = new ResultSounds(Green);
        WaiwaiResults = new WaiwaiResultSounds(Green);
        Retry = new RetrySounds(Green, StopAll);
        GameOver = new GameOverSounds(Green);
    }

    /// <summary>The skin's sounds the engine asks for by name.</summary>
    public ISoundSet Set { get; }

    public SoundBank Bank { get; }
    public GreenSounds Green { get; }
    public AttractSounds Attract { get; }
    public FrontendSounds Frontend { get; }
    public GameplaySounds Gameplay { get; }
    public ResultSounds Results { get; }
    public WaiwaiResultSounds WaiwaiResults { get; }
    public RetrySounds Retry { get; }
    public GameOverSounds GameOver { get; }

    /// <summary>Plays the skin's sound for an engine name (nothing when the skin has none).</summary>
    public void Play(string name, bool trace = true)
    {
        foreach (var cue in Set.Describe(new(name)))
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
