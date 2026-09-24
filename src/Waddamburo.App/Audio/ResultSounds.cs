using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The results screen: its music and result.lm's voice/effect requests.</summary>
internal sealed class ResultSounds(SoundBank bank) : IResultSoundController
{
    private AudioPlaybackHandle? _music;

    /// <summary>The results music: JINGLE_SEISEKI, or JINGLE_WAIRSLT after a Waiwai song.</summary>
    public void StartMusic(bool waiwai = false)
    {
        if (!bank.IsPlaying(_music))
            _music = bank.PlayMusic("Result", waiwai ? "JINGLE_WAIRSLT" : "JINGLE_SEISEKI", loop: true);
    }

    public void StopMusic()
    {
        bank.Stop(_music);
        _music = null;
    }

    public void RequestSound(ResultSoundRequest request)
    {
        // Group 1/2 = the left/right player's request. The right player's voices are Katsu-chan's,
        // one cue after Don-chan's (traced (2, 1/6/8) -> VO_RESULT 2/12/16); effects are shared.
        var katsu = request.Group == 2;
        var group = katsu ? 1 : request.Group;
        var sound = (request.Kind, group, request.Cue) switch
        {
            (ResultSoundRequestKind.Voice, 1, 1) => (Bank: "VO_RESULT", Cue: 1),
            (ResultSoundRequestKind.Voice, 1, 3) => (Bank: "VO_RESULT", Cue: 5), // Katsu (2, 3) -> 6 traced
            (ResultSoundRequestKind.Voice, 1, 4) => (Bank: "VO_RESULT", Cue: 7), // Katsu (2, 4) -> 8 traced
            (ResultSoundRequestKind.Voice, 1, 5) => (Bank: "VO_RESULT", Cue: 9),
            (ResultSoundRequestKind.Voice, 1, 6) => (Bank: "VO_RESULT", Cue: 11),
            (ResultSoundRequestKind.Voice, 1, 7) => (Bank: "VO_RESULT", Cue: 13),
            (ResultSoundRequestKind.Voice, 1, 8) => (Bank: "VO_RESULT", Cue: 15),
            (ResultSoundRequestKind.Voice, 0, 9) => (Bank: "VO_RESULT", Cue: 17),
            (ResultSoundRequestKind.Voice, 0, 10) => (Bank: "VO_RESULT", Cue: 18),
            (ResultSoundRequestKind.Effect, 1, 0) => (Bank: "SE_RESULT", Cue: 0),
            (ResultSoundRequestKind.Effect, 1, 1) => (Bank: "SE_RESULT", Cue: 3),
            (ResultSoundRequestKind.Effect, 1, 2) => (Bank: "SE_RESULT", Cue: 6),
            (ResultSoundRequestKind.Effect, 1, 5) => (Bank: "SE_RESULT", Cue: 15),
            // Two players: 22 whenever either cleared (traced one failed + one cleared, and both full
            // combo); both failed takes the one-player failure cue (user-confirmed: no applause).
            (ResultSoundRequestKind.Effect, 0, 6) => (Bank: "SE_RESULT",
                Cue: request.TwoPlayers ? request.Cleared ? 22 : 20
                    : request.FullCombo ? 22 : request.Cleared ? 18 : 20),
            (ResultSoundRequestKind.Effect, 0, 7) => (Bank: "SE_RESULT", Cue: 24),
            _ => default,
        };
        if (sound.Bank is null)
        {
            Console.WriteLine($"Result.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
            return;
        }
        if (katsu && request.Kind == ResultSoundRequestKind.Voice)
            sound.Cue++;
        // Two players: each player's counting effects are their own, one and two after the one-player
        // cue (traced (1|2, 0/1/2) -> SE_RESULT 1/2, 4/5, 7/8; ponytail: cue 5 follows untraced).
        else if (request.TwoPlayers && request.Kind == ResultSoundRequestKind.Effect && request.Group is 1 or 2)
            sound.Cue += request.Group;
        bank.Play(sound.Bank, sound.Cue, request.Kind == ResultSoundRequestKind.Voice ? AudioBus.Voice : null,
            trace: request.Cue != 1 || request.Kind != ResultSoundRequestKind.Effect);
    }
}
