using Waddamburo.Formats.Audio;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

/// <summary>Resolves authored nuSound2 bank/cue requests against a user-supplied sound tree.</summary>
internal sealed class AuthoredSoundController : ISongSelectSoundController
{
    private readonly AudioEngine _audio;
    private readonly string _bankRoot;
    private readonly NuSoundBankCatalog _catalog;
    private readonly Dictionary<(int Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(int Bank, int Cue)> _reportedFailures = [];
    private AudioClip? _latestVoice;

    public AuthoredSoundController(AudioEngine audio, string soundRoot)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ArgumentException.ThrowIfNullOrWhiteSpace(soundRoot);
        var root = Path.GetFullPath(soundRoot);
        _bankRoot = Path.Combine(root, "se");
        _catalog = NuSoundBankCatalog.Load(Path.Combine(root, "config", "nuSound2BankStr.bin"));
    }

    public void RequestSound(LumenFrontendSoundRequest request)
    {
        switch (request.Kind)
        {
            case LumenFrontendSoundRequestKind.Effect:
                playBankCue(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.SystemEffect:
                traceUnmapped("SystemEffect", request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.LoopVoice:
                if (_latestVoice is not null)
                {
                    _audio.Mixer.StopBus(AudioBus.Voice);
                    _audio.Mixer.Play(_latestVoice, AudioBus.Voice, loop: true);
                }
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
                if (_latestVoice is not null)
                {
                    _audio.Mixer.StopBus(AudioBus.Voice);
                    _audio.Mixer.Play(_latestVoice, AudioBus.Voice, loop: true);
                }
                break;
            case SongSelectSoundRequestKind.SystemEffect:
            case SongSelectSoundRequestKind.PlayerEffect:
                traceUnmapped(request.Kind.ToString(), request.Arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public void StopVoice() => _audio.Mixer.StopBus(AudioBus.Voice, TimeSpan.FromMilliseconds(20));

    private void playBankCue(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out var bankId) || !tryInteger(arguments, 1, out var cueId) || cueId < 0)
        {
            traceUnmapped("Effect", arguments);
            return;
        }
        if (!_catalog.TryGetName(bankId, out var bankName))
        {
            traceUnmapped("Effect", arguments);
            return;
        }

        var key = (bankId, cueId);
        try
        {
            if (!_clips.TryGetValue(key, out var clip))
            {
                var path = Path.Combine(_bankRoot, bankName + ".nub");
                clip = AudioClip.Load(
                    path,
                    _audio.Mixer.Format,
                    sourceStreamIndex: checked((uint)cueId + 1U));
                _clips.Add(key, clip);
            }
            var bus = bankName.StartsWith("VO_", StringComparison.Ordinal) ? AudioBus.Voice : AudioBus.MenuSound;
            if (bus == AudioBus.Voice)
                _latestVoice = clip;
            _audio.Mixer.Play(clip, bus);
            Console.WriteLine($"Authored sound {bankName}#{cueId} -> {bus}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add(key))
                Console.Error.WriteLine($"Authored sound {bankId}:{cueId} is unavailable: {exception.Message}");
        }
    }

    private static bool tryInteger(
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments,
        int index,
        out int result)
    {
        result = 0;
        if ((uint)index >= (uint)arguments.Length || arguments[index].Kind != LumenHostValueKind.Number)
            return false;
        var value = arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            return false;
        result = (int)value;
        return true;
    }

    private static void traceUnmapped(
        string kind,
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        Console.WriteLine($"Lumen.{kind}({string.Join(", ", arguments.Select(formatValue))}) [unmapped]");
    }

    private static string formatValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => "undefined",
        LumenHostValueKind.Null => "null",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        _ => "undefined",
    };
}
