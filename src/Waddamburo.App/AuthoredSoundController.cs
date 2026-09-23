using Waddamburo.Formats.Audio;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

/// <summary>Resolves authored nuSound2 bank/cue requests against a user-supplied sound tree.</summary>
internal sealed class AuthoredSoundController : ISongSelectSoundController, IRetrySoundController
{
    private readonly AudioEngine _audio;
    private readonly string _bankRoot;
    private readonly string _musicRoot;
    private readonly NuSoundBankCatalog _catalog;
    private readonly Dictionary<(string Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(string Bank, int Cue)> _reportedFailures = [];
    private AudioClip? _latestVoice;
    private AudioPlaybackHandle? _oneShotVoiceHandle;
    private AudioPlaybackHandle? _loopVoiceHandle;
    private AudioPlaybackHandle? _retryMusicHandle;
    private bool _categoryVoiceSelected;

    public AuthoredSoundController(AudioEngine audio, string soundRoot)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ArgumentException.ThrowIfNullOrWhiteSpace(soundRoot);
        var root = Path.GetFullPath(soundRoot);
        _bankRoot = Path.Combine(root, "se");
        _musicRoot = Path.Combine(root, "bgm", "nub");
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

    public void RequestSound(RetrySoundRequest request)
    {
        if (request.Kind == RetrySoundRequestKind.Voice
            && request.Group == 0
            && request.Cue is 1 or 2 or 3 or 4 or 6 or 7 or 8)
        {
            playNamedBankCue("VO_REVIVAL", request.Cue);
        }
        else if (request.Kind == RetrySoundRequestKind.Effect)
        {
            if (request.Group == 0 && request.Cue is 0 or 1 or 3 or 5 or 7 or 8 or 12 or 14 or 16)
                playNamedBankCue("SE_REVIVAL", request.Cue);
            else if (request.Group == 1 && request.Cue == 100)
                playNamedBankCue("SE_COM", 0, AudioBus.DrumHit);
            else if (request.Group == 0 && request.Cue == 101)
                playNamedBankCue("SE_COM", 13, AudioBus.MenuSound);
            else
                traceUnmappedRetry(request);
        }
        else
        {
            traceUnmappedRetry(request);
        }
    }

    public void StopAll()
    {
        foreach (var bus in Enum.GetValues<AudioBus>())
            _audio.Mixer.StopBus(bus);
        _retryMusicHandle = null;
        _oneShotVoiceHandle = null;
        _loopVoiceHandle = null;
        _latestVoice = null;
        _categoryVoiceSelected = false;
    }

    public void StartMusic()
    {
        if (_retryMusicHandle is { } handle && _audio.Mixer.IsPlaying(handle))
            return;
        var path = Path.Combine(_musicRoot, "JINGLE_RENDA.nub");
        try
        {
            _retryMusicHandle = _audio.PlayLoop(path, AudioBus.Bgm);
            Console.WriteLine("Revival music JINGLE_RENDA -> Bgm.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add(("JINGLE_RENDA", -1)))
                Console.Error.WriteLine($"Revival music is unavailable: {exception.Message}");
        }
    }

    public bool IsVoicePlaying => isPlaying(_oneShotVoiceHandle) || isPlaying(_loopVoiceHandle);

    public void PlayDrum(bool don) => playNamedBankCue("SE_COM", don ? 0 : 3, AudioBus.DrumHit);

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
            _ => -1,
        };
        if (cue < 0)
        {
            Console.WriteLine($"Category voice for \"{category}\" is unmapped.");
            StopVoice();
            return;
        }

        _categoryVoiceSelected = true;
        playNamedBankCue("VO_SELECT", cue, AudioBus.Voice, loop: true);
    }

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

        // Song Select emits its stock initial-genre sound ID after host data is
        // assigned. The host-owned category selection above has already resolved
        // the custom catalog's semantic genre and must not be replaced by that ID.
        if (_categoryVoiceSelected && bankName == "VO_SELECT" && cueId == 32)
            return;

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

        var sound = hitKind switch
        {
            0 => (Bank: "SE_COM", Cue: 0), // Don
            1 => (Bank: "SE_COM", Cue: 3), // Ka
            2 => (Bank: "SE_COM", Cue: 6), // Close-folder substitution
            _ => default,
        };
        if (sound.Bank is null)
        {
            traceUnmapped("PlayerEffect", arguments);
            return;
        }
        playNamedBankCue(sound.Bank, sound.Cue, AudioBus.DrumHit);
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

    private void playNamedBankCue(
        string bankName,
        int cueId,
        AudioBus? busOverride = null,
        bool loop = false)
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
                _oneShotVoiceHandle = null;
                _loopVoiceHandle = null;
            }
            var handle = _audio.Mixer.Play(clip, bus, loop: loop);
            if (bus == AudioBus.Voice)
            {
                if (loop)
                    _loopVoiceHandle = handle;
                else
                    _oneShotVoiceHandle = handle;
            }
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

    private static void traceUnmappedRetry(RetrySoundRequest request) =>
        Console.WriteLine($"Retry.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");

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
