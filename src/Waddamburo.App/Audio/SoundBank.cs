using System.Collections.Immutable;
using Waddamburo.App.Hosting;
using Waddamburo.Game.Sound;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Skins.Green.Sound;

namespace Waddamburo.App.Audio;

/// <summary>
/// The engine's sound player: decodes a skin's <see cref="SoundSource"/>s (any format the decoders read) and plays
/// them on their buses, keeps the latest voice (one voice at a time per side), the coin channel, and reports each
/// missing sound once. A sound cuts off its own previous play (as the cabinet does: Don stops Don, not Ka); two
/// players stay apart because each plays its own panned sound. The bank / cue calls are the game's sounds
/// (<see cref="GreenSoundSet"/>), for the screens whose sounds are not asked by name yet.
/// </summary>
internal sealed class SoundBank
{
    private readonly AudioEngine _audio;
    private readonly GreenSoundSet _green;
    private readonly Dictionary<(string Path, int Stream), AudioClip> _clips = [];
    private readonly HashSet<(string Path, int Stream)> _reportedFailures = [];
    private readonly Dictionary<(string Path, int Stream), AudioPlaybackHandle> _lastPlayed = [];
    private AudioClip? _latestVoice;
    private int _latestVoiceCue = -1;
    // Per side (-1 left, 0 centre, 1 right; the sound's authored pan): two players' voices (full combo 154/155,
    // combo 1/2) play together, each cutting only its own side's; a centred voice cuts them all.
    private readonly Dictionary<int, AudioPlaybackHandle> _oneShotVoices = [];
    private AudioPlaybackHandle? _loopVoice;
    private AudioPlaybackHandle? _coin;
    // When the coin channel takes the next queued sound: a coin sound's length after the last one started.
    private long _coinSlotEnds;

    public SoundBank(AudioEngine audio, GreenSoundSet green)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _green = green ?? throw new ArgumentNullException(nameof(green));
    }

    public bool TryGetBankName(int bankId, out string name) => _green.TryGetBankName(bankId, out name);

    public static AudioBus BusOf(SoundBus bus) => bus switch
    {
        SoundBus.Music => AudioBus.Bgm,
        SoundBus.Voice => AudioBus.Voice,
        SoundBus.Drum => AudioBus.DrumHit,
        SoundBus.Coin => AudioBus.Coin,
        _ => AudioBus.MenuSound,
    };

    /// <summary>Plays a skin's sound on its bus (drum hits are not logged: too many).</summary>
    public void Play(SoundCue cue, bool loop = false) => play(cue.Source, BusOf(cue.Bus), loop, trace: cue.Bus != SoundBus.Drum);

    /// <summary>Plays a game bank cue: VO_ banks on the voice bus (replacing the current voice), others as menu sounds.</summary>
    public void Play(string bank, int cue, AudioBus? busOverride = null, bool loop = false, bool trace = true) =>
        play(_green.Cue(bank, cue), busOverride ?? BusOf(GreenSoundSet.BusOf(bank)), loop, trace);

    private void play(SoundSource source, AudioBus bus, bool loop, bool trace)
    {
        var key = source.Key;
        // Never decoded inside a frame: a first use decodes on the thread pool and plays at a later Pump (a frame
        // or so late), as decoding a cue took 4-28 ms on a low-end PC and showed as the note lane jumping.
        if (!_clips.ContainsKey(key))
        {
            if (!_reportedFailures.Contains(key))
            {
                decodeAhead(source);
                _deferred.Add((key, bus, () => play(source, bus, loop, trace)));
            }
            return;
        }
        try
        {
            var clip = load(source);
            if (bus == AudioBus.Voice)
            {
                if (clip.Pan == 0)
                    stopVoicePlaying(); // not the voices still decoding: they were asked for after this one
                else
                    stopVoiceSide(Math.Sign(clip.Pan));
                _latestVoice = clip;
                // ponytail: StopVoice(cue) compares a bank cue's number only (the movies' call has no bank).
                _latestVoiceCue = source.Stream - 1;
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
                    _oneShotVoices[Math.Sign(clip.Pan)] = handle;
            }
            if (trace)
                Console.WriteLine($"Authored sound {source.Label} -> {bus} ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(source, exception);
        }
    }

    /// <summary>Decodes a sound ahead of time (drum hits must not stall on first use).</summary>
    public void Preload(SoundSource source)
    {
        try
        {
            _ = load(source);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(source, exception);
        }
    }

    public void Preload(string bank, int cue) => Preload(_green.Cue(bank, cue));

    private readonly Dictionary<(string Path, int Stream), Task<AudioClip>> _decoding = [];
    private readonly List<((string Path, int Stream) Key, AudioBus Bus, Action Play)> _deferred = [];

    private void decodeAhead(SoundSource source)
    {
        if (_clips.ContainsKey(source.Key) || _decoding.ContainsKey(source.Key))
            return;
        var format = _audio.Mixer.Format;
        _decoding[source.Key] = Task.Run(() => decode(source, format));
    }

    /// <summary>Each frame: sounds decoded on the thread pool join the cache, and the plays that waited for them start.</summary>
    public void Pump()
    {
        if (_decoding.Count == 0)
            return;
        foreach (var (key, task) in _decoding.Where(static pair => pair.Value.IsCompleted).ToArray())
        {
            _decoding.Remove(key);
            if (task.IsCompletedSuccessfully)
                _clips.TryAdd(key, task.Result);
            else if (task.Exception?.InnerException is { } exception)
                reportUnavailable(key, $"{Path.GetFileName(key.Path)}#{key.Stream}", exception);
        }
        for (var index = 0; index < _deferred.Count;)
        {
            var (key, _, play) = _deferred[index];
            if (_decoding.ContainsKey(key)) { index++; continue; }
            _deferred.RemoveAt(index);
            if (_clips.ContainsKey(key))
                play();
        }
    }

    /// <summary>A cue's decoded sound (for mixing it into a stream); null when it is unavailable.</summary>
    public AudioClip? Clip(string bank, int cue)
    {
        var source = _green.Cue(bank, cue);
        try
        {
            return load(source);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(source, exception);
            return null;
        }
    }

    /// <summary>Starts a bgm/nub track on the music bus; null when the file is unavailable.</summary>
    public AudioPlaybackHandle? PlayMusic(string label, string name, bool loop)
    {
        var path = _green.Music(name).Path;
        try
        {
            var handle = loop ? _audio.PlayLoop(path, AudioBus.Bgm) : _audio.PlayOneShot(path, AudioBus.Bgm);
            Console.WriteLine($"{label} music {name} -> Bgm.");
            return handle;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add((path, -1)))
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
            var clip = AudioClip.Load(_green.Music(name).Path, _audio.Mixer.Format, TimeSpan.FromMinutes(2));
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

    /// <summary>Every one-shot cue of <paramref name="bank"/> still sounding stops.</summary>
    public void StopBank(string bank)
    {
        var path = _green.Cue(bank, 0).Path;
        foreach (var (key, handle) in _lastPlayed)
            if (key.Path == path)
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
        _deferred.Clear();
        _oneShotVoices.Clear();
        _loopVoice = null;
        _latestVoice = null;
    }

    // A voice still decoding counts: whoever waits for the voice to end must not go on before it starts.
    public bool IsVoicePlaying => _oneShotVoices.Values.Any(handle => IsPlaying(handle)) || IsPlaying(_loopVoice)
        || _deferred.Exists(static entry => entry.Bus == AudioBus.Voice);

    /// <summary>Stops the current voice only when it is the given cue.</summary>
    public void StopVoice(int cue)
    {
        if (_latestVoice is not null && _latestVoiceCue == cue)
            StopVoice();
    }

    public void StopVoice()
    {
        _deferred.RemoveAll(static entry => entry.Bus == AudioBus.Voice);
        stopVoicePlaying();
    }

    private void stopVoicePlaying()
    {
        foreach (var oneShot in _oneShotVoices.Values)
            _audio.Mixer.Stop(oneShot);
        if (_loopVoice is { } looped)
            _audio.Mixer.Stop(looped);
        _oneShotVoices.Clear();
        _loopVoice = null;
        _latestVoice = null;
    }

    // A side's voice replaces that side's and the centred one (and the loop), not the other player's.
    private void stopVoiceSide(int side)
    {
        foreach (var key in new[] { side, 0 })
            if (_oneShotVoices.Remove(key, out var handle))
                _audio.Mixer.Stop(handle);
        if (_loopVoice is { } looped)
            _audio.Mixer.Stop(looped);
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
        var source = _green.Cue("SE_COM", cue);
        try
        {
            var clip = load(source);
            var slot = cue == CoinCue ? clip.Duration : load(_green.Cue("SE_COM", CoinCue)).Duration;
            Stop(_coin);
            _coin = _audio.Mixer.Play(clip, AudioBus.Coin);
            _coinSlotEnds = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(slot.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            Console.WriteLine($"Authored sound {source.Label} -> Coin ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(source, exception);
        }
    }

    /// <summary>The coin channel's slot is still taken (a coin sound's length after the last one started).</summary>
    public bool IsCoinPlaying => IsPlaying(_coin) && System.Diagnostics.Stopwatch.GetTimestamp() < _coinSlotEnds;

    private AudioClip load(SoundSource source)
    {
        if (_clips.TryGetValue(source.Key, out var clip))
            return clip;
        // A sound already decoding in the background: wait for it rather than decode it twice.
        clip = _decoding.Remove(source.Key, out var pending) ? pending.GetAwaiter().GetResult() : decode(source, _audio.Mixer.Format);
        _clips.Add(source.Key, clip);
        return clip;
    }

    // Pure (no player state): it runs on the thread pool too.
    private static AudioClip decode(SoundSource source, SdlAudioFormat format)
    {
        var clip = AudioClip.Load(source.Path, format, sourceStreamIndex: checked((uint)source.Stream));
        if (source.Settings?.Invoke() is not { } settings)
            return clip;
        return clip.WithPitch(settings.PitchCents).WithPan(settings.PanDegrees).WithLeadingSilence(settings.Delay).WithGain(settings.Gain);
    }

    private void reportUnavailable(SoundSource source, Exception exception) => reportUnavailable(source.Key, source.Label, exception);

    private void reportUnavailable((string Path, int Stream) key, string label, Exception exception)
    {
        if (_reportedFailures.Add(key))
            Console.Error.WriteLine($"Authored sound {label} is unavailable: {exception.Message}");
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
