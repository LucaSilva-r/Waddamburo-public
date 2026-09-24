using System.Collections.Immutable;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// Entry and Song Select: the movies name bank cues by id (RequestSE(bank, cue)); drum feedback,
/// genre voices and the voice replay are the game's.
/// </summary>
internal sealed class FrontendSounds(SoundBank bank) : ISongSelectSoundController
{
    private bool _categoryVoiceSelected;

    /// <summary>Entry's sound calls.</summary>
    public void RequestSound(LumenFrontendSoundRequest request)
    {
        switch (request.Kind)
        {
            case LumenFrontendSoundRequestKind.Effect:
                playBankCue(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.SystemEffect:
                playEntrySystemEffect(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.LoopVoice:
                bank.ReplayLatestVoice();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public void RequestSound(SongSelectSoundRequest request)
    {
        switch (request.Kind)
        {
            case SongSelectSoundRequestKind.Effect:
                playBankCue(request.Arguments);
                break;
            case SongSelectSoundRequestKind.LoopVoice:
                bank.ReplayLatestVoice();
                break;
            case SongSelectSoundRequestKind.SystemEffect:
                SoundBank.TraceUnmapped(request.Kind.ToString(), request.Arguments);
                break;
            case SongSelectSoundRequestKind.PlayerEffect:
                playPlayerEffect(request.Arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public void PlayEntryVoice(int cue) => bank.Play("VO_ENTRY", cue);

    public void SelectCategoryVoice(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var cue = category switch
        {
            "Namco Original" => 0,
            "Pop" or "J-POP" => 1,
            "Game Music" => 2,
            "Classical" => 3,
            "Variety" => 4,
            "Anime" => 5,
            "Vocaloid" => 7,
            // The mode-switch folder (traced 65 in normal Song Select, 66 in Waiwai's).
            "To Waiwai" => 65,
            "To Normal" => 66,
            _ => -1,
        };
        if (cue < 0)
        {
            Console.WriteLine($"Category voice for \"{category}\" is unmapped.");
            StopVoice();
            return;
        }
        _categoryVoiceSelected = true;
        bank.Play("VO_SELECT", cue, AudioBus.Voice);
    }

    public void StopVoice() => bank.StopVoice();

    public void StopVoice(int cue) => bank.StopVoice(cue);

    /// <summary>A new credit: Song Select's genre voice is chosen afresh.</summary>
    public void Reset() => _categoryVoiceSelected = false;

    private void playBankCue(ImmutableArray<LumenHostValue> arguments)
    {
        if (!SoundBank.TryInteger(arguments, 0, out var bankId) || !SoundBank.TryInteger(arguments, 1, out var cueId)
            || cueId < 0 || !bank.TryGetBankName(bankId, out var bankName))
        {
            SoundBank.TraceUnmapped("Effect", arguments);
            return;
        }
        // Song Select emits its stock initial-genre sound ID after host data is assigned. The
        // host-owned category selection above has already resolved the custom catalog's semantic
        // genre and must not be replaced by that ID.
        if (_categoryVoiceSelected && bankName == "VO_SELECT" && cueId == 32)
            return;
        bank.Play(bankName, cueId);
    }

    private void playPlayerEffect(ImmutableArray<LumenHostValue> arguments)
    {
        if (!SoundBank.TryInteger(arguments, 0, out _) || !SoundBank.TryInteger(arguments, 1, out var hitKind))
        {
            SoundBank.TraceUnmapped("PlayerEffect", arguments);
            return;
        }
        int? cue = hitKind switch
        {
            0 => 0, // Don
            1 => 3, // Ka
            2 => 6, // Close-folder substitution
            _ => null,
        };
        if (cue is { } value)
            bank.Play("SE_COM", value, AudioBus.DrumHit);
        else
            SoundBank.TraceUnmapped("PlayerEffect", arguments);
    }

    private void playEntrySystemEffect(ImmutableArray<LumenHostValue> arguments)
    {
        if (!SoundBank.TryInteger(arguments, 0, out var group) || !SoundBank.TryInteger(arguments, 1, out var hitKind)
            || group is not (0 or 1))
        {
            SoundBank.TraceUnmapped("SystemEffect", arguments);
            return;
        }
        // Group = the drum: the right one's Don played SE_COM cue 2 (traced session8-p2-solo); its Ka
        // shares the left drum's cue.
        int? cue = hitKind switch
        {
            0 => group == 0 ? 0 : 2, // Don / confirm
            1 => 3, // Ka / navigation
            _ => null,
        };
        if (cue is { } value)
            bank.Play("SE_COM", value, AudioBus.DrumHit);
        else
            SoundBank.TraceUnmapped("SystemEffect", arguments);
    }
}
