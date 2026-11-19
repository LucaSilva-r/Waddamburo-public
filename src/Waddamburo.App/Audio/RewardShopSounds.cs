using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>reward_shop's cues (traced research/traces/session-rewards-1.md: medal mode and shopping).</summary>
internal sealed class RewardShopSounds(SoundBank bank, GameOverSounds gameOver) : IRewardShopSoundController
{
    private AudioPlaybackHandle? _music;

    public void StartMedalMusic() => gameOver.StartMedalMusic();

    public void StartShopMusic() => _music ??= bank.PlayMusic("RewardShop", "JINGLE_GOHOBI", loop: true);

    public void StopMusic()
    {
        bank.Stop(_music);
        _music = null;
    }

    public void Bought()
    {
        bank.Play("SE_GOHOBI", 12);
        bank.Play("VO_GOHOBI", 6);
    }

    public void Answered() => bank.Play("SE_COM", 29);

    public void RequestSound(RewardShopSoundRequest request)
    {
        // The movie's SoundDef ids → the traced bank cues.
        (string Bank, int Cue)? cue = (request.Kind, request.Group, request.Cue) switch
        {
            (RewardShopSoundRequestKind.Voice, 0, 0) => ("VO_GOHOBI", 0), // ごほうびタイム
            (RewardShopSoundRequestKind.Voice, 0, 2) => ("VO_GOHOBI", 3), // the shop opens
            (RewardShopSoundRequestKind.Voice, 0, 3) => ("VO_GOHOBI", 4), // "get する?"
            (RewardShopSoundRequestKind.Voice, 0, 5) => ("VO_GOHOBI", 7), // all bought
            (RewardShopSoundRequestKind.Effect, 0, 0) => ("SE_COM", 1), // don
            (RewardShopSoundRequestKind.Effect, 0, 1) => ("SE_COM", 4), // ka
            (RewardShopSoundRequestKind.Effect, 0, 2) => ("SE_COM", 7), // cancel
            (RewardShopSoundRequestKind.Effect, 0, 3) => ("SE_COM", 13), // SE_CNTDOWN: the menus' countdown tick
            (RewardShopSoundRequestKind.Effect, 0, 4) => ("SE_COM", 19), // costume change
            (RewardShopSoundRequestKind.Effect, 0, 8) => ("SE_GOHOBI", 2), // the medal card in
            (RewardShopSoundRequestKind.Effect, 0, 9) => ("SE_GOHOBI", 4), // the count, every 50 ms
            (RewardShopSoundRequestKind.Effect, 0, 12) => ("SE_GOHOBI", 9), // the shop opens
            (RewardShopSoundRequestKind.Effect, 0, 13) => ("SE_GOHOBI", 10), // the slider
            (RewardShopSoundRequestKind.Effect, 0, 16) => ("SE_GOHOBI", 13), // complete
            _ => null,
        };
        if (cue is var (name, index))
            bank.Play(name, index);
        else
            Console.WriteLine($"RewardShop.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
    }
}
