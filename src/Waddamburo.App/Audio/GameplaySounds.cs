using Waddamburo.App.Gameplay;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// A song: the drums' hit sounds and the game's event cues (combo voices, banners, balloons,
/// kusudama, Waiwai's cut-ins and rare note, the song's end).
/// </summary>
internal sealed class GameplaySounds(SoundBank bank)
{
    /// <summary>Waiwai gameplay plays its own drum tone (traced SE_GAME_NEIRO_100_L/R).</summary>
    // ponytail: Waiwai song select's tone choice (AssignTone / RequestToneSE) is not offered yet.
    public bool Waiwai { get; set; }

    // A drum's hit sounds: centred for one player; in two-player play the left drum's pan left and
    // the right drum's right (traced session9-2p).
    private string drumBank(int? lane) => (Waiwai ? "SE_GAME_NEIRO_100" : "SE_GAME_NEIRO_000") + lane switch
    {
        0 => "_L",
        1 => "_R",
        _ => "_C",
    };

    public void PrepareDrums(bool twoPlayers = false)
    {
        foreach (var lane in twoPlayers ? new int?[] { 0, 1 } : [null])
            foreach (var cue in new[] { 0, 1 })
                bank.Preload(drumBank(lane), cue);
    }

    public void PlayDrum(int? lane, bool don) => bank.Play(drumBank(lane), don ? 0 : 1, AudioBus.DrumHit, trace: false);

    /// <summary>
    /// <paramref name="lane"/> is null for one player. Two players: 0 left, 1 right; a player's own
    /// cues follow the one-player cue as +1 / +2 (traced session9-2p: combo voice 1/2, banners
    /// 10/11 · 7/8 · 13/14, full-combo voice 154/155, balloon 2, roll voice 158, kusudama miss voice 160/161).
    /// </summary>
    public void Play(int? lane, GameplaySoundEvent sound)
    {
        // ponytail: the hundred-combo voice follows the same rule untraced.
        int own(int cue) => lane is { } player ? cue + 1 + player : cue;
        switch (sound)
        {
            case GameplaySoundEvent.LongNoteStarted:
                bank.Play("VO_GAME", own(156));
                break;
            case GameplaySoundEvent.BalloonPopped:
                bank.Play("SE_GAME", own(0));
                break;
            case GameplaySoundEvent.KusudamaPopped:
                bank.Play("SE_GAME", 3);
                break;
            case GameplaySoundEvent.KusudamaFailed:
                bank.Play("SE_GAME", 5);
                bank.Play("VO_GAME", own(159));
                break;
            case GameplaySoundEvent.FiftyCombo:
                bank.Play("VO_GAME", own(0));
                break;
            case GameplaySoundEvent.HundredCombo:
                bank.Play("VO_GAME", own(3));
                break;
            case GameplaySoundEvent.FailBanner:
                bank.Play("SE_GAME", own(9));
                break;
            case GameplaySoundEvent.ClearBanner:
                bank.Play("SE_GAME", own(6));
                break;
            case GameplaySoundEvent.FullComboBanner:
                bank.Play("SE_GAME", own(12));
                bank.Play("VO_GAME", own(153));
                break;
            // Waiwai (traced session11-waiwai): SE_WAIENSO 0 as the song starts, 4 per together
            // section, 2 per solo section.
            // Traced SE_GAME_COLLABO_00 cue 0 / 1 with the rare note: taken as the player who hit it.
            case GameplaySoundEvent.RareHit:
                bank.Play("SE_GAME_COLLABO_00", lane ?? 0);
                break;
            case GameplaySoundEvent.WaiwaiStart:
                bank.Play("SE_WAIENSO", 0);
                break;
            case GameplaySoundEvent.SynchroCutIn:
                bank.Play("SE_WAIENSO", 4);
                break;
            case GameplaySoundEvent.SoloCutIn:
                bank.Play("SE_WAIENSO", 2);
                break;
            case GameplaySoundEvent.SongFinished:
                bank.Play("VO_RESULT", 0);
                bank.Play("SE_GAME", 15);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sound));
        }
    }
}
