using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The revival drum roll: JINGLE_RENDA and retry_game.lm's VO_/SE_REVIVAL requests.</summary>
internal sealed class RetrySounds(SoundBank bank, Action stopAll) : IRetrySoundController
{
    private AudioPlaybackHandle? _music;

    public void StartMusic()
    {
        if (!bank.IsPlaying(_music))
            _music = bank.PlayMusic("Revival", "JINGLE_RENDA", loop: true);
    }

    public void StopAll() => stopAll();

    public void RequestSound(RetrySoundRequest request)
    {
        switch (request.Kind, request.Group, request.Cue)
        {
            case (RetrySoundRequestKind.Voice, 0, 1 or 2 or 3 or 4 or 6 or 7 or 8):
                bank.Play("VO_REVIVAL", request.Cue);
                break;
            case (RetrySoundRequestKind.Effect, 0, 0 or 1 or 3 or 5 or 7 or 8 or 12 or 14 or 16):
                bank.Play("SE_REVIVAL", request.Cue);
                break;
            case (RetrySoundRequestKind.Effect, 1, 100):
                bank.Play("SE_COM", 0, AudioBus.DrumHit);
                break;
            case (RetrySoundRequestKind.Effect, 0, 101):
                bank.Play("SE_COM", 13, AudioBus.MenuSound);
                break;
            default:
                Console.WriteLine($"Retry.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
                break;
        }
    }
}
