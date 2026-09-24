using System.Collections.Immutable;
using Waddamburo.App.Hosting;
using Waddamburo.Formats.Audio;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// The user's sound tree (data/sound): nuSound2 bank cues (se/*.nub, named by
/// config/nuSound2BankStr.bin) and music (bgm/nub). Plays cues on their buses, keeps the latest
/// voice (one voice at a time), the coin channel, and reports each missing sound once.
/// </summary>
internal sealed class SoundBank
{
    private readonly AudioEngine _audio;
    private readonly string _bankRoot;
    private readonly string _musicRoot;
    private readonly NuSoundBankCatalog _catalog;
    private readonly Dictionary<(string Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(string Bank, int Cue)> _reportedFailures = [];
    private AudioClip? _latestVoice;
    private int _latestVoiceCue = -1;
    private AudioPlaybackHandle? _oneShotVoice;
    private AudioPlaybackHandle? _loopVoice;
    private AudioPlaybackHandle? _coin;

    public SoundBank(AudioEngine audio, string soundRoot)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ArgumentException.ThrowIfNullOrWhiteSpace(soundRoot);
        var root = Path.GetFullPath(soundRoot);
        _bankRoot = Path.Combine(root, "se");
        _musicRoot = Path.Combine(root, "bgm", "nub");
        _catalog = NuSoundBankCatalog.Load(Path.Combine(root, "config", "nuSound2BankStr.bin"));
    }

    public bool TryGetBankName(int bankId, out string name) => _catalog.TryGetName(bankId, out name);

    /// <summary>Plays a bank cue: VO_ banks on the voice bus (replacing the current voice), others as menu sounds.</summary>
    public void Play(string bank, int cue, AudioBus? busOverride = null, bool loop = false, bool trace = true)
    {
        var key = (bank, cue);
        try
        {
            var clip = load(key);
            var bus = busOverride ?? (bank.StartsWith("VO_", StringComparison.Ordinal) ? AudioBus.Voice : AudioBus.MenuSound);
            if (bus == AudioBus.Voice)
            {
                StopVoice();
                _latestVoice = clip;
                _latestVoiceCue = cue;
            }
            var handle = _audio.Mixer.Play(clip, bus, loop: loop);
            if (bus == AudioBus.Voice)
            {
                if (loop)
                    _loopVoice = handle;
                else
                    _oneShotVoice = handle;
            }
            if (trace)
                Console.WriteLine($"Authored sound {bank}#{cue} -> {bus} ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(key, exception);
        }
    }

    /// <summary>Decodes a cue ahead of time (drum hits must not stall on first use).</summary>
    public void Preload(string bank, int cue)
    {
        try
        {
            _ = load((bank, cue));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable((bank, cue), exception);
        }
    }

    /// <summary>Starts a bgm/nub track on the music bus; null when the file is unavailable.</summary>
    public AudioPlaybackHandle? PlayMusic(string label, string name, bool loop)
    {
        var path = Path.Combine(_musicRoot, name + ".nub");
        try
        {
            var handle = loop ? _audio.PlayLoop(path, AudioBus.Bgm) : _audio.PlayOneShot(path, AudioBus.Bgm);
            Console.WriteLine($"{label} music {name} -> Bgm.");
            return handle;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add((name, -1)))
                Console.Error.WriteLine($"{label} music {name} is unavailable: {exception.Message}");
            return null;
        }
    }

    public bool IsPlaying(AudioPlaybackHandle? handle) => handle is { } value && _audio.Mixer.IsPlaying(value);

    /// <summary>Fades a track out (20 ms).</summary>
    public void Stop(AudioPlaybackHandle? handle)
    {
        if (handle is { } value)
            _audio.Mixer.Stop(value, TimeSpan.FromMilliseconds(20));
    }

    /// <summary>Everything but the coin channel stops (leaving a scene that owns all its sounds).</summary>
    public void StopAll()
    {
        foreach (var bus in Enum.GetValues<AudioBus>().Where(static bus => bus != AudioBus.Coin))
            _audio.Mixer.StopBus(bus);
        _oneShotVoice = null;
        _loopVoice = null;
        _latestVoice = null;
    }

    public bool IsVoicePlaying => IsPlaying(_oneShotVoice) || IsPlaying(_loopVoice);

    /// <summary>Stops the current voice only when it is the given cue.</summary>
    public void StopVoice(int cue)
    {
        if (_latestVoice is not null && _latestVoiceCue == cue)
            StopVoice();
    }

    public void StopVoice()
    {
        if (_oneShotVoice is { } oneShot)
            _audio.Mixer.Stop(oneShot);
        if (_loopVoice is { } looped)
            _audio.Mixer.Stop(looped);
        _oneShotVoice = null;
        _loopVoice = null;
        _latestVoice = null;
    }

    /// <summary>The movies' LoopVoice: the latest voice once more, unless a voice is playing.</summary>
    public void ReplayLatestVoice()
    {
        if (_latestVoice is null || IsVoicePlaying)
            return;
        _loopVoice = _audio.Mixer.Play(_latestVoice, AudioBus.Voice);
        Console.WriteLine("Authored voice replay -> Voice.");
    }

    /// <summary>
    /// One queued coin's sound (traced SE_COM cue 9 per credited coin), on its own bus. The caller
    /// credits the next coin only once this has finished, so every coin is heard in full.
    /// </summary>
    public void PlayCoin()
    {
        try
        {
            var clip = load(("SE_COM", 9));
            _coin = _audio.Mixer.Play(clip, AudioBus.Coin);
            Console.WriteLine($"Authored sound SE_COM#9 -> Coin ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(("SE_COM", 9), exception);
        }
    }

    public bool IsCoinPlaying => IsPlaying(_coin);

    private AudioClip load((string Bank, int Cue) key)
    {
        if (_clips.TryGetValue(key, out var clip))
            return clip;
        var path = Path.Combine(_bankRoot, key.Bank + ".nub");
        clip = AudioClip.Load(path, _audio.Mixer.Format, sourceStreamIndex: checked((uint)key.Cue + 1U));
        _clips.Add(key, clip);
        return clip;
    }

    private void reportUnavailable((string Bank, int Cue) key, Exception exception)
    {
        if (_reportedFailures.Add(key))
            Console.Error.WriteLine($"Authored sound {key.Bank}#{key.Cue} is unavailable: {exception.Message}");
    }

    /// <summary>A whole-number argument of a movie's sound call.</summary>
    public static bool TryInteger(ImmutableArray<LumenHostValue> arguments, int index, out int result)
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

    public static void TraceUnmapped(string kind, ImmutableArray<LumenHostValue> arguments) =>
        Console.WriteLine($"Lumen.{kind}({string.Join(", ", arguments.Select(HostValueText.Format))}) [unmapped]");
}
