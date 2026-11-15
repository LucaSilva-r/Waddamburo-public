using Waddamburo.Skins.Green.Sound;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green;

// The front-end services the hosts call: the game's sounds when there is audio, else console traces.

public sealed class ViewerFrontendServices(FrontendSounds? sounds, bool freePlay) : ILumenFrontendServices
{
    private readonly FrontendSounds? _sounds = sounds;

    public bool IsReady => true;

    public bool IsStartLumen => true;

    public bool IsFreePlay => freePlay; // config.cfg free_play

    public void Initialize()
    {
    }

    public bool TryEnterPlayer() => true;

    public void RequestSound(LumenFrontendSoundRequest request)
    {
        if (_sounds is not null)
        {
            _sounds.RequestSound(request);
            return;
        }
        Console.WriteLine(
            $"Lumen.{request.Kind}({string.Join(", ", request.Arguments.Select(HostValueText.Format))})");
    }

    public void StopVoice(int? cue = null)
    {
        if (_sounds is null)
            Console.WriteLine($"Lumen.StopVoice({cue})");
        else if (cue is { } number)
            _sounds.StopVoice(number);
        else
            _sounds.StopVoice();
    }

    public LumenHostValue CallExternalInterface(LumenHostCall hostCall)
    {
        Console.WriteLine(
            $"ExternalInterface.call({string.Join(", ", hostCall.Arguments.Select(HostValueText.Format))})");
        return LumenHostValue.Undefined;
    }
}

public sealed class TraceSoundController : ISongSelectSoundController
{
    public static TraceSoundController Instance { get; } = new();

    public void RequestSound(SongSelectSoundRequest request)
    {
        Console.WriteLine(
            $"Lumen.{request.Kind}({string.Join(", ", request.Arguments.Select(HostValueText.Format))})");
    }

    public void SelectCategoryVoice(string category) =>
        Console.WriteLine($"Category voice requested for \"{category}\".");

    public void StopVoice() => Console.WriteLine("Lumen.StopVoice()");
}
