using System.Collections.Immutable;
using Waddamburo.App.Hosting;
using Waddamburo.Formats.Audio;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// The user's sound tree (data/sound): nuSound2 bank cues (se/*.nub, named by
/// config/nuSound2BankStr.bin) and music (bgm/nub). Plays cues on their buses, keeps the latest
/// voice (one voice at a time), the coin channel, and reports each missing sound once. A cue cuts
/// off its own previous play (as the cabinet does: Don stops Don, not Ka); two players stay apart
/// because each plays its own panned bank or cue.
/// </summary>
internal sealed class SoundBank
{
    private readonly AudioEngine _audio;
    private readonly string _bankRoot;
    private readonly string _musicRoot;
    private readonly NuSoundBankCatalog _catalog;
    private readonly Dictionary<(string Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(string Bank, int Cue)> _reportedFailures = [];
    private readonly Dictionary<(string Bank, int Cue), AudioPlaybackHandle> _lastPlayed = [];
    private AudioClip? _latestVoice;
    private int _latestVoiceCue = -1;
    private AudioPlaybackHandle? _oneShotVoice;
    private AudioPlaybackHandle? _loopVoice;
    private AudioPlaybackHandle? _coin;
    // When the coin channel takes the next queued sound: a coin sound's length after the last one started.
    private long _coinSlotEnds;

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
            // A fade, as a hard cut clicks. Drum hits release over 20 ms: fitted to a Green roll recorded
            // on rpcs3 (60 hits/s), where 3 ms sounded thin; other cues keep a few milliseconds.
            if (!loop && _lastPlayed.TryGetValue(key, out var previous))
                _audio.Mixer.Stop(previous, TimeSpan.FromMilliseconds(bus == AudioBus.DrumHit ? 20 : 3));
            var handle = _audio.Mixer.Play(clip, bus, loop: loop);
            if (!loop)
                _lastPlayed[key] = handle;
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

    /// <summary>A cue's decoded sound (for mixing it into a stream); null when it is unavailable.</summary>
    public AudioClip? Clip(string bank, int cue)
    {
        try
        {
            return load((bank, cue));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable((bank, cue), exception);
            return null;
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

    private AudioPlaybackHandle? _tutorialMusic;

    public Action? PrepareTutorialMusic(string name)
    {
        if (Path.GetFileName(name) != name) return null;
        try
        {
            var clip = AudioClip.Load(Path.Combine(_musicRoot, name + ".nub"), _audio.Mixer.Format, TimeSpan.FromMinutes(2));
            return () =>
            {
                if (_tutorialMusic is { } previous) _audio.Mixer.Stop(previous);
                _tutorialMusic = _audio.Mixer.Play(clip, AudioBus.Bgm, loop: name != "JINGLE_SILENCE");
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"Tutorial music {name} unavailable: {exception.Message}");
            return null;
        }
    }

    public bool IsPlaying(AudioPlaybackHandle? handle) => handle is { } value && _audio.Mixer.IsPlaying(value);

    /// <summary>Fades a track out (20 ms).</summary>
    /// <summary>Every one-shot cue of <paramref name="bank"/> still sounding stops.</summary>
    public void StopBank(string bank)
    {
        foreach (var (key, handle) in _lastPlayed)
            if (key.Bank == bank)
                _audio.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
    }

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

    /// <summary>SE_COM's coin cue (traced per credited coin) and its service credit cue (traced session-service-coin).</summary>
    public const int CoinCue = 9, ServiceCue = 10;

    /// <summary>
    /// One queued coin's (or service credit's, <paramref name="cue"/>) sound, on its own bus. Each takes a coin
    /// sound's length (<see cref="IsCoinPlaying"/>); the next one cuts what is left of it, as the cabinet cuts its
    /// 3 s service sound when credits are spammed. The last one plays in full.
    /// </summary>
    public void PlayCoin(int cue = CoinCue)
    {
        try
        {
            var clip = load(("SE_COM", cue));
            var slot = cue == CoinCue ? clip.Duration : load(("SE_COM", CoinCue)).Duration;
            Stop(_coin);
            _coin = _audio.Mixer.Play(clip, AudioBus.Coin);
            _coinSlotEnds = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(slot.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            Console.WriteLine($"Authored sound SE_COM#{cue} -> Coin ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(("SE_COM", cue), exception);
        }
    }

    /// <summary>The coin channel's slot is still taken (a coin sound's length after the last one started).</summary>
    public bool IsCoinPlaying => IsPlaying(_coin) && System.Diagnostics.Stopwatch.GetTimestamp() < _coinSlotEnds;

    private AudioClip load((string Bank, int Cue) key)
    {
        if (_clips.TryGetValue(key, out var clip))
            return clip;
        var path = Path.Combine(_bankRoot, key.Bank + ".nub");
        var header = bankHeader(path, key.Cue);
        // vgmstream leaves the header's authored gain to the player.
        clip = AudioClip.Load(path, _audio.Mixer.Format, sourceStreamIndex: checked((uint)key.Cue + 1U))
            .WithLeadingSilence(NubCueDelay.Read(header, key.Cue))
            .WithGain(NubCueDelay.ReadGain(header, key.Cue));
        _clips.Add(key, clip);
        return clip;
    }

    // The bank's header up to the cue's stream header (NubCueDelay reads no further).
    private static byte[] bankHeader(string path, int cue)
    {
        using var file = File.OpenRead(path);
        var table = new byte[0x20 + (cue + 1) * 4];
        if (file.Read(table) < table.Length)
            return table;
        var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(^4));
        if (header > file.Length)
            return table;
        var bytes = new byte[(int)Math.Min(file.Length, header + 0x50L)];
        file.Position = 0;
        file.ReadExactly(bytes);
        return bytes;
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
