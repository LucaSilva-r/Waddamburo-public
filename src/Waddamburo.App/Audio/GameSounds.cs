using Waddamburo.Game.Sound;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The sounds: the engine's player (<see cref="Bank"/>) and the skin's set it asks by name.</summary>
internal sealed class GameSounds
{
    /// <param name="audio">The mixer the sounds play on.</param>
    /// <param name="set">What the engine's names play: a pack of ours or the user's over the game's (SoundPack).</param>
    public GameSounds(AudioEngine audio, ISoundSet set)
    {
        Set = set;
        Bank = new SoundBank(audio);
        Gameplay = new GameplaySounds(Bank, set);
    }

    /// <summary>The skin's sounds the engine asks for by name.</summary>
    public ISoundSet Set { get; }

    public SoundBank Bank { get; }
    public GameplaySounds Gameplay { get; }

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

    private SoundHandle? _screenMusic;
    private string? _screenMusicName;

    /// <summary>The screen's music, one at a time, looped: the skin's track for a name, unless it already plays.</summary>
    public void StartScreenMusic(string name)
    {
        if (_screenMusicName == name && Bank.IsPlaying(_screenMusic))
            return;
        StopScreenMusic();
        (_screenMusic, _screenMusicName) = (PlayMusic(name, loop: true), name);
    }

    public void StopScreenMusic()
    {
        Bank.Stop(_screenMusic);
        (_screenMusic, _screenMusicName) = (null, null);
    }

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

    /// <summary>The screens' music, decoded in the background and kept: it starts at once, however slow the storage.</summary>
    // ponytail: the engine's music names listed again; a missing one only starts late, as without this.
    public void PreloadMusic()
    {
        foreach (var name in new[] { "music.entry", "music.result", "music.roll", "music.gameover", "music.shop",
            "music.result.waiwai", "music.result.ai", "music.remind", "music.dan" })
            if (Set.Describe(new(name)) is [var first, ..])
                Bank.PreloadMusic(first.Source);
    }

    /// <summary>Everything but the coin stops, and the next credit starts fresh.</summary>
    public void StopAll()
    {
        Bank.StopAll();
    }
}
