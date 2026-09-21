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
    private readonly Dictionary<(string Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(string Bank, int Cue)> _reportedFailures = [];
    private AudioClip? _latestVoice;
    private AudioPlaybackHandle? _oneShotVoiceHandle;
    private AudioPlaybackHandle? _loopVoiceHandle;

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
                playEntrySystemEffect(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.LoopVoice:
                replayLatestVoiceOnce();
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
                replayLatestVoiceOnce();
                break;
            case SongSelectSoundRequestKind.SystemEffect:
                traceUnmapped(request.Kind.ToString(), request.Arguments);
                break;
            case SongSelectSoundRequestKind.PlayerEffect:
                playPlayerEffect(request.Arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public bool IsVoicePlaying => isPlaying(_oneShotVoiceHandle) || isPlaying(_loopVoiceHandle);

    public void StopVoice()
    {
        if (_loopVoiceHandle is not { } handle)
            return;
        _audio.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
        _loopVoiceHandle = null;
    }

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

        playNamedBankCue(bankName, cueId);
    }

    private void playPlayerEffect(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out _)
            || !tryInteger(arguments, 1, out var hitKind))
        {
            traceUnmapped("PlayerEffect", arguments);
            return;
        }

        var cueId = hitKind switch
        {
            0 or 2 => 0, // Don (centre/confirm variants)
            1 => 3, // Ka (rim)
            _ => -1,
        };
        if (cueId < 0)
        {
            traceUnmapped("PlayerEffect", arguments);
            return;
        }
        playNamedBankCue("SE_COM", cueId, AudioBus.DrumHit);
    }

    private void playEntrySystemEffect(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out var group)
            || !tryInteger(arguments, 1, out var hitKind)
            || group != 0)
        {
            traceUnmapped("SystemEffect", arguments);
            return;
        }

        var cueId = hitKind switch
        {
            0 => 0, // Don / confirm
            1 => 3, // Ka / navigation
            _ => -1,
        };
        if (cueId < 0)
        {
            traceUnmapped("SystemEffect", arguments);
            return;
        }
        playNamedBankCue("SE_COM", cueId, AudioBus.DrumHit);
    }

    private void playNamedBankCue(string bankName, int cueId, AudioBus? busOverride = null)
    {
        var key = (bankName, cueId);
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
            var bus = busOverride
                ?? (bankName.StartsWith("VO_", StringComparison.Ordinal) ? AudioBus.Voice : AudioBus.MenuSound);
            if (bus == AudioBus.Voice)
            {
                stopVoiceImmediately(_oneShotVoiceHandle);
                stopVoiceImmediately(_loopVoiceHandle);
                _latestVoice = clip;
                _loopVoiceHandle = null;
            }
            var handle = _audio.Mixer.Play(clip, bus);
            if (bus == AudioBus.Voice)
                _oneShotVoiceHandle = handle;
            Console.WriteLine($"Authored sound {bankName}#{cueId} -> {bus}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add(key))
                Console.Error.WriteLine($"Authored sound {bankName}#{cueId} is unavailable: {exception.Message}");
        }
    }

    private void replayLatestVoiceOnce()
    {
        if (_latestVoice is null || IsVoicePlaying)
            return;
        _loopVoiceHandle = _audio.Mixer.Play(_latestVoice, AudioBus.Voice);
        Console.WriteLine("Authored voice replay -> Voice.");
    }

    private bool isPlaying(AudioPlaybackHandle? handle)
        => handle is { } value && _audio.Mixer.IsPlaying(value);

    private void stopVoiceImmediately(AudioPlaybackHandle? handle)
    {
        if (handle is { } value)
            _audio.Mixer.Stop(value);
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
