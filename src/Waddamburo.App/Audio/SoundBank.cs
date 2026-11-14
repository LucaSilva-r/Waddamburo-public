using Waddamburo.Game.Sound;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// The engine's sound player: decodes a skin's <see cref="SoundSource"/>s (any format the decoders read) and plays
/// them on their buses, keeps the latest voice (one voice at a time per side), the coin channel, and reports each
/// missing sound once. A sound cuts off its own previous play (as the cabinet does: Don stops Don, not Ka); two
/// players stay apart because each plays its own panned sound.
/// </summary>
internal sealed class SoundBank : ISoundPlayer
{
    private readonly AudioEngine _audio;
    private readonly Dictionary<(string Path, int Stream), AudioClip> _clips = [];
    private readonly HashSet<(string Path, int Stream)> _reportedFailures = [];
    private readonly Dictionary<(string Path, int Stream), AudioPlaybackHandle> _lastPlayed = [];
    private AudioClip? _latestVoice;
    private SoundSource? _latestVoiceSource;
    // Per side (-1 left, 0 centre, 1 right; the sound's authored pan): two players' voices (full combo 154/155,
    // combo 1/2) play together, each cutting only its own side's; a centred voice cuts them all.
    private readonly Dictionary<int, AudioPlaybackHandle> _oneShotVoices = [];
    private AudioPlaybackHandle? _loopVoice;
    private AudioPlaybackHandle? _coin;
    // When the coin channel takes the next queued sound: a coin sound's length after the last one started.
    private long _coinSlotEnds;

    public SoundBank(AudioEngine audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public static AudioBus BusOf(SoundBus bus) => bus switch
    {
        SoundBus.Music => AudioBus.Bgm,
        SoundBus.Voice => AudioBus.Voice,
        SoundBus.Drum => AudioBus.DrumHit,
        SoundBus.Coin => AudioBus.Coin,
        _ => AudioBus.MenuSound,
    };

    /// <summary>Plays a skin's sound on its bus (drum hits are not logged unless asked: too many).</summary>
    public void Play(SoundCue cue, bool loop = false, bool? trace = null) =>
        play(cue.Source, BusOf(cue.Bus), loop, trace ?? cue.Bus != SoundBus.Drum);

    private static SoundHandle handle(AudioPlaybackHandle value) => new(value.Value);

    private static AudioPlaybackHandle? handle(SoundHandle? value) => value is { } some ? new AudioPlaybackHandle(some.Value) : null;

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
                _latestVoiceSource = source;
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

    /// <summary>A sound decoded (for mixing it into a stream); null when it is unavailable.</summary>
    public AudioClip? Clip(SoundSource source)
    {
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

    /// <summary>Streams a track on the music bus; null when the file is unavailable.</summary>
    public SoundHandle? PlayMusic(SoundSource music, bool loop)
    {
        try
        {
            var started = loop ? _audio.PlayLoop(music.Path, AudioBus.Bgm) : _audio.PlayOneShot(music.Path, AudioBus.Bgm);
            Console.WriteLine($"Music {music.Label} -> Bgm.");
            return handle(started);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add((music.Path, -1)))
                Console.Error.WriteLine($"Music {music.Label} is unavailable: {exception.Message}");
            return null;
        }
    }

    private AudioPlaybackHandle? _preparedMusic;

    public Action? PrepareMusic(SoundSource music, bool loop)
    {
        try
        {
            var clip = AudioClip.Load(music.Path, _audio.Mixer.Format, TimeSpan.FromMinutes(2));
            return () =>
            {
                if (_preparedMusic is { } previous) _audio.Mixer.Stop(previous);
                _preparedMusic = _audio.Mixer.Play(clip, AudioBus.Bgm, loop: loop);
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"Music {music.Label} unavailable: {exception.Message}");
            return null;
        }
    }

    public bool IsPlaying(SoundHandle? handle) => SoundBank.handle(handle) is { } value && _audio.Mixer.IsPlaying(value);

    private bool isPlaying(AudioPlaybackHandle? handle) => handle is { } value && _audio.Mixer.IsPlaying(value);

    public void StopFile(string path)
    {
        foreach (var (key, playing) in _lastPlayed)
            if (key.Path == path)
                _audio.Mixer.Stop(playing, TimeSpan.FromMilliseconds(20));
    }

    public void Stop(SoundHandle? handle) => stop(SoundBank.handle(handle));

    private void stop(AudioPlaybackHandle? handle)
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
    public bool IsVoicePlaying => _oneShotVoices.Values.Any(handle => isPlaying(handle)) || isPlaying(_loopVoice)
        || _deferred.Exists(static entry => entry.Bus == AudioBus.Voice);

    public void StopVoiceIf(Func<SoundSource, bool> latest)
    {
        if (_latestVoice is not null && _latestVoiceSource is { } source && latest(source))
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

    /// <summary>
    /// One queued coin's (or service credit's) sound, on its own bus. Each takes a coin sound's length
    /// (<paramref name="slot"/>'s: <see cref="IsCoinPlaying"/>); the next one cuts what is left of it, as the cabinet
    /// cuts its 3 s service sound when credits are spammed. The last one plays in full.
    /// </summary>
    public void PlayCoin(SoundSource source, SoundSource slot)
    {
        try
        {
            var clip = load(source);
            var length = load(slot).Duration;
            stop(_coin);
            _coin = _audio.Mixer.Play(clip, AudioBus.Coin);
            _coinSlotEnds = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(length.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            Console.WriteLine($"Authored sound {source.Label} -> Coin ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(source, exception);
        }
    }

    /// <summary>The coin channel's slot is still taken (a coin sound's length after the last one started).</summary>
    public bool IsCoinPlaying => isPlaying(_coin) && System.Diagnostics.Stopwatch.GetTimestamp() < _coinSlotEnds;

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
}
