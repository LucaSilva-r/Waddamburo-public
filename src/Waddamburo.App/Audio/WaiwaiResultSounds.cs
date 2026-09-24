using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// Waiwai's results (traced session9/11/13): PlaySE(id) -> SE_WAIRSLT_COLLABO_00, PlayVO(id) ->
/// VO_WAIRSLT_COLLABO_00; the comment voice (PlayVO 9) is 14 + its text.nut sentence.
/// </summary>
internal sealed class WaiwaiResultSounds(SoundBank bank) : IWaiwaiResultSoundController
{
    private static readonly Dictionary<int, int> Effects = new()
    {
        [2] = 0, [3] = 1, [4] = 3, [5] = 4, [6] = 6, [8] = 10, [9] = 12, [10] = 13, [11] = 17, [12] = 18,
    };

    public void RequestSound(WaiwaiResultSoundRequest request)
    {
        var effect = request.Kind == WaiwaiResultSoundKind.Effect;
        int? cue = effect
            ? Effects.TryGetValue(request.Id, out var value) ? value : null
            : request.Id switch { 1 => 2, 2 => 4, 6 => 13, 9 => 14 + request.Sentence, _ => null };
        if (cue is not { } found)
        {
            Console.WriteLine($"WaiwaiResult.{request.Kind}({request.Id}) [unmapped]");
            return;
        }
        bank.Play(effect ? "SE_WAIRSLT_COLLABO_00" : "VO_WAIRSLT_COLLABO_00", found, effect ? null : AudioBus.Voice,
            trace: request.Id != 2);
    }
}
