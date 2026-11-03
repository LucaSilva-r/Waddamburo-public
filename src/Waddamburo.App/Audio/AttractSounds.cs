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

    /// <summary>
    /// Under the menu: the SE_ATTRACT cues sounding stop and new ones are skipped (they share the menu sounds'
    /// bus, which stays on; short one-shots, so stopping them is as good as muting).
    /// </summary>
    public bool Muted
    {
        get;
        set
        {
            if (value && !field)
                bank.StopBank("SE_ATTRACT");
            field = value;
        }
    }

    public void PlayAttractCue(bool voice, int cue)
    {
        if (!Muted)
            bank.Play(voice ? "VO_ATTRACT" : "SE_ATTRACT", cue);
    }

    /// <summary>A drum hit leaving the attract loop (traced SE_COM cue 1 at the title).</summary>
    public void PlayExit() => bank.Play("SE_COM", 1, AudioBus.MenuSound);
}
