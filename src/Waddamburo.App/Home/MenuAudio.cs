using Waddamburo.App.Audio;
using Waddamburo.App.Gameplay;
using Waddamburo.Game.Flow;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Home;

/// <summary>
/// The home menu's sound: the player's volume settings on the mixer, a sample of the bus whose volume
/// is being edited, and the menus' music held while the menu is open.
/// </summary>
internal sealed class MenuAudio(AudioEngine? audio, GameSounds? sounds, SongSelectCatalogView catalog, CatalogAssetRouter assets)
{
    private AudioBus? _sampleBus;
    private AudioPlaybackHandle? _sampleMusic;
    private int _sampleNextTick;
    private bool _menuMusicHeld;

    /// <summary>Volumes (percent) on a curve that sounds even: half way is about a quarter of the level.</summary>
    public void Apply(ArcadeSettings settings)
    {
        if (audio?.Mixer is not { } mixer) return;
        static float level(int percent) => percent * percent / 10000f;
        mixer.MasterVolume = level(settings.MasterVolume);
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
    /// While a volume is edited its bus keeps sounding (<paramref name="bus"/>; null: nothing): a random
    /// song, a menu sound, or a Don-chan line, each repeated once it has finished.
    /// </summary>
    public void Sample(AudioBus? bus, int tick)
    {
        if (bus != _sampleBus)
        {
            if (_sampleMusic is { } music)
                audio?.Mixer.Stop(music, TimeSpan.FromMilliseconds(20));
            if (_sampleBus == AudioBus.Voice)
                sounds?.Bank.StopVoice();
            _sampleMusic = null;
            _sampleBus = bus;
            _sampleNextTick = 0;
        }
        if (bus is not { } playing || audio is null || tick < _sampleNextTick)
            return;
        switch (playing)
        {
            case AudioBus.Bgm when _sampleMusic is not { } music || !audio.Mixer.IsPlaying(music):
                _sampleMusic = randomSong(audio);
                _sampleNextTick = tick + 60;
                break;
            case AudioBus.MenuSound:
                sounds?.Bank.Play("SE_COM", 13, AudioBus.MenuSound, trace: false);
                _sampleNextTick = tick + 60;
                break;
            case AudioBus.Voice when sounds?.Bank is { IsVoicePlaying: false } bank:
                bank.Play("VO_SELECT", 1, trace: false);
                _sampleNextTick = tick + 30;
                break;
        }
    }

    // A random catalog song from its preview point, on the music bus.
    // ponytail: opens the file on the tick (a short hitch); open it in the background if that shows.
    private AudioPlaybackHandle? randomSong(AudioEngine engine)
    {
        var songs = catalog.Categories.SelectMany(static category => category.Songs)
            .Where(static song => song.Descriptor.AudioAsset is not null).ToArray();
        if (songs.Length == 0) return null;
        var song = songs[Random.Shared.Next(songs.Length)].Descriptor;
        try
        {
            var input = assets.OpenReadAsync(song.AudioAsset!).AsTask().GetAwaiter().GetResult();
            BufferedAudioSource source;
            try
            {
                source = new BufferedAudioSource(input, engine.Mixer.Format, song.PreviewStart ?? TimeSpan.Zero);
            }
            catch
            {
                input.Dispose();
                throw;
            }
            return engine.Mixer.PlayStream(source, AudioBus.Bgm);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"Volume sample '{song.Title.Primary}' unavailable: {exception.Message}");
            return null;
        }
    }
}
