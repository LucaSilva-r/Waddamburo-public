using Waddamburo.Game.Lumen;

namespace Waddamburo.App.Audio;

/// <summary>reward_shop's cues (traced research/traces/session-rewards-1.md: medal mode and shopping).</summary>
internal sealed class RewardShopSounds(SoundBank bank, GameOverSounds gameOver) : IRewardShopSoundController
{
    public void StartMedalMusic() => gameOver.StartMedalMusic();

    public void RequestSound(RewardShopSoundRequest request)
    {
        switch (request.Kind, request.Group, request.Cue)
        {
            case (RewardShopSoundRequestKind.Voice, 0, 0): // ごほうびタイム
                bank.Play("VO_GOHOBI", 0);
                break;
            case (RewardShopSoundRequestKind.Effect, 0, 8): // the medal card in
                bank.Play("SE_GOHOBI", 2);
                break;
            case (RewardShopSoundRequestKind.Effect, 0, 9): // the count, every 50 ms
                bank.Play("SE_GOHOBI", 4);
                break;
            default:
                Console.WriteLine($"RewardShop.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
                break;
        }
    }
}
