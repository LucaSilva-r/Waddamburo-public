using Waddamburo.App.Audio;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Home;

/// <summary>Where the game is, for the volumes on top of the master one: the attract (with its demo plays), the menus, or a song.</summary>
internal enum MenuVolumeScope { Song, Menus, Attract }

/// <summary>
/// The home menu's sound: the player's volume settings on the mixer, the menus' music held while the menu is open,
/// and the attract muted under it.
/// </summary>
internal sealed class MenuAudio(AudioEngine? audio, GameSounds? sounds)
{
    private bool _menuMusicHeld;
    private ArcadeSettings? _settings;
    private MenuVolumeScope _scope;

    /// <summary>Which of the menus' own volumes applies now (re-applied on a change).</summary>
    public void Scope(MenuVolumeScope scope)
    {
        if (_scope == scope) return;
        _scope = scope;
        if (_settings is { } settings)
            Apply(settings);
    }

    /// <summary>Volumes (percent) on a curve that sounds even: half way is about a quarter of the level.</summary>
    public void Apply(ArcadeSettings settings)
    {
        _settings = settings;
        if (audio?.Mixer is not { } mixer) return;
        static float level(int percent) => percent * percent / 10000f;
        mixer.MasterVolume = level(settings.MasterVolume) * _scope switch
        {
            MenuVolumeScope.Attract => level(settings.AttractVolume),
            MenuVolumeScope.Menus => level(settings.MenuVolume),
            _ => 1,
        };
        mixer.SetBusGain(AudioBus.Bgm, level(settings.MusicVolume));
        mixer.SetBusGain(AudioBus.Preview, level(settings.MusicVolume));
        mixer.SetBusGain(AudioBus.DrumHit, level(settings.DrumVolume));
        mixer.SetBusGain(AudioBus.MenuSound, level(settings.EffectsVolume));
        mixer.SetBusGain(AudioBus.Coin, level(settings.EffectsVolume));
        mixer.SetBusGain(AudioBus.Voice, level(settings.VoiceVolume));
        if (sounds is not null)
            sounds.Gameplay.Panning = settings.StereoPanning;
    }

    /// <summary>The entry's and Song Select's music and preview hold while their menu is open.</summary>
    public void HoldMenuMusic(bool held)
    {
        if (audio is null || _menuMusicHeld == held) return;
        _menuMusicHeld = held;
        audio.Mixer.SetBusPaused(AudioBus.Bgm, held);
        audio.Mixer.SetBusPaused(AudioBus.Preview, held);
    }

    /// <summary>
    /// The attract (and its demo play) goes on silent under the menu: its music, voices and drums. The menu's own
    /// sounds (drum and effect buses) still sound.
    /// </summary>
    public void MuteAttract(bool muted)
    {
        if (sounds is not null)
            sounds.Gameplay.Muted = sounds.Attract.Muted = muted;
        if (audio is null) return;
        foreach (var bus in (AudioBus[])[AudioBus.Bgm, AudioBus.Preview, AudioBus.Voice])
            audio.Mixer.SetBusMuted(bus, muted);
    }
}
