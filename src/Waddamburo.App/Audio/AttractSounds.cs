using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The boot screens and attract loop: their music streams and VO_/SE_ATTRACT cues.</summary>
internal sealed class AttractSounds(SoundBank bank) : IAttractSoundController
{
    private AudioPlaybackHandle? _stream;

    public void PlayAttractStream(string name)
    {
        StopAttractStream();
        _stream = bank.PlayMusic("Attract", name, loop: false);
    }

    public bool IsAttractStreamPlaying => bank.IsPlaying(_stream);

    public void StopAttractStream()
    {
        bank.Stop(_stream);
        _stream = null;
    }

    public void PlayAttractCue(bool voice, int cue) => bank.Play(voice ? "VO_ATTRACT" : "SE_ATTRACT", cue);

    /// <summary>A drum hit leaving the attract loop (traced SE_COM cue 1 at the title).</summary>
    public void PlayExit() => bank.Play("SE_COM", 1, AudioBus.MenuSound);
}
