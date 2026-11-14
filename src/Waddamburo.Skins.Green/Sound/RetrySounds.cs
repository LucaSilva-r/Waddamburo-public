using Waddamburo.Game.Sound;

namespace Waddamburo.Skins.Green.Sound;

/// <summary>The revival drum roll: JINGLE_RENDA and retry_game.lm's VO_/SE_REVIVAL requests.</summary>
public sealed class RetrySounds(GreenSounds bank, Action stopAll) : IRetrySoundController
{
    private SoundHandle? _music;

    public void StartMusic()
    {
        if (!bank.IsPlaying(_music))
            _music = bank.PlayMusic("JINGLE_RENDA", loop: true);
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
                bank.Play("SE_COM", 0, SoundBus.Drum);
                break;
            case (RetrySoundRequestKind.Effect, 0, 101):
                bank.Play("SE_COM", 13, SoundBus.Menu);
                break;
            default:
                Console.WriteLine($"Retry.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
                break;
        }
    }
}
