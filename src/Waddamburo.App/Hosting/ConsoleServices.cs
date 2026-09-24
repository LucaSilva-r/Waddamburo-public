using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

// The front-end services the hosts call: the authored sounds when a sound root is given, else console traces.

internal sealed class ViewerFrontendServices(AuthoredSoundController? sounds, bool freePlay) : ILumenFrontendServices
{
    private readonly AuthoredSoundController? _sounds = sounds;

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

internal sealed class TraceSoundController : ISongSelectSoundController
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

internal sealed class TracePreviewController : ISongPreviewController
{
    public static TracePreviewController Instance { get; } = new();

    public void SetPreview(SongPreviewRequest? request)
    {
        if (request is not null)
            Console.WriteLine($"Song preview requested at {request.Start.TotalSeconds:0.###}s for {request.Song}.");
    }
}

internal static class HostValueText
{
    public static string Format(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => "undefined",
        LumenHostValueKind.Null => "null",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        _ => "undefined",
    };
}
