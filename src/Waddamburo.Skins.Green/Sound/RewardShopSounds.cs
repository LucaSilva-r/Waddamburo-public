using Waddamburo.Game.Sound;


namespace Waddamburo.Skins.Green.Sound;

/// <summary>reward_shop's cues (traced research/traces/session-rewards-1.md: medal mode and shopping).</summary>
public sealed class RewardShopSounds(GreenSounds bank, GameOverSounds gameOver) : IRewardShopSoundController
{
    private SoundHandle? _music;

    public void StartMedalMusic() => gameOver.StartMedalMusic();

    public void StartShopMusic() => _music ??= bank.PlayMusic("JINGLE_GOHOBI", loop: true);

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
        // The movie's SoundDef ids → the traced bank cues; the group is the player (0 1P, 1 2P), who hear the same sounds.
        // ponytail: 2P's don/ka share 1P's cues (only 1P traced); the costume change has its own side's cue.
        (string Bank, int Cue)? cue = (request.Kind, request.Cue) switch
        {
            (RewardShopSoundRequestKind.Voice, 0) => ("VO_GOHOBI", 0), // ごほうびタイム
            (RewardShopSoundRequestKind.Voice, 2) => ("VO_GOHOBI", 3), // the shop opens
            (RewardShopSoundRequestKind.Voice, 3) => ("VO_GOHOBI", 4), // "get する?"
            (RewardShopSoundRequestKind.Voice, 5) => ("VO_GOHOBI", 7), // all bought
            (RewardShopSoundRequestKind.Effect, 0) => ("SE_COM", 1), // don
            (RewardShopSoundRequestKind.Effect, 1) => ("SE_COM", 4), // ka
            (RewardShopSoundRequestKind.Effect, 2) => ("SE_COM", 7), // cancel
            (RewardShopSoundRequestKind.Effect, 3) => ("SE_COM", 13), // SE_CNTDOWN: the menus' countdown tick
            (RewardShopSoundRequestKind.Effect, 4) => ("SE_COM", 19), // SE_COM_KISEKAE_L: 1P's costume change
            (RewardShopSoundRequestKind.Effect, 5) => ("SE_COM", 20), // SE_COM_KISEKAE_R: 2P's (as at the entry)
            (RewardShopSoundRequestKind.Effect, 8) => ("SE_GOHOBI", 2), // the medal card in
            (RewardShopSoundRequestKind.Effect, 9) => ("SE_GOHOBI", 4), // the count, every 50 ms
            (RewardShopSoundRequestKind.Effect, 12) => ("SE_GOHOBI", 9), // the shop opens
            (RewardShopSoundRequestKind.Effect, 13) => ("SE_GOHOBI", 10), // the slider
            (RewardShopSoundRequestKind.Effect, 16) => ("SE_GOHOBI", 13), // complete
            _ => null,
        };
        if (cue is var (name, index))
            bank.Play(name, index);
        else
            Console.WriteLine($"RewardShop.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
    }
}
