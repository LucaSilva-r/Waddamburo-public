using System.Diagnostics;

namespace Waddamburo.Platform.Sdl.Media;

// Device voices: on an SDL output every sound plays on a stream of its own, which SDL mixes on its native audio
// thread. Clips go in whole, streams are kept queued ahead, so a managed GC pause (which stops every managed thread,
// the old mix thread included) no longer silences anything already playing. Volumes, fades and pauses are applied as
// each stream's gain by Pump, which also refills and reaps; a pause only delays those (a fade or a refill). Outputs
// that pull the mix (WASAPI exclusive, ASIO) keep Render.
public sealed partial class AudioMixer
{
    // How far ahead streams are kept queued: past the longest pause expected (a Switch's scene-switch collections take
    // up to ~1.2 s). Sources that follow the game live (scrubbing a review) keep only a few periods.
    private static readonly TimeSpan StreamAhead = TimeSpan.FromSeconds(PlatformInfo.IsSwitch ? 2 : PlatformInfo.IsMobile ? 1 : 0.5);
    private const int LiveAheadFrames = 2048;
    private const int ReadFrames = 4096;

    private Func<VoiceChannel>? _createStream;
    private readonly List<DeviceVoice> _deviceVoices = [];
    private float[] _scratch = [];
    private long _lastPump;

    /// <summary>Plays every sound on a device stream of its own (null: back to <see cref="Render"/>, the output changes).</summary>
    public void UseDeviceVoices(Func<VoiceChannel>? create)
    {
        lock (_gate)
        {
            _createStream = create;
            foreach (var voice in _deviceVoices)
                release(voice);
            _deviceVoices.Clear();
            _lastPump = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Sounds play on device streams: <see cref="Pump"/> drives them, <see cref="Render"/> has nothing to mix.</summary>
    public bool UsesDeviceVoices
    {
        get
        {
            lock (_gate)
                return _createStream is not null;
        }
    }

    /// <summary>A stream's frames put on the device and still queued there (the song clock), or null.</summary>
    public (ulong Submitted, ulong Queued)? StreamCounters(AudioPlaybackHandle handle)
    {
        lock (_gate)
            return _deviceVoices.Find(voice => voice.Handle == handle) is { } voice
                ? (voice.Stream.SubmittedFrames, voice.Stream.QueuedFrames) : null;
    }

    private DeviceVoice? startDevice(AudioPlaybackHandle handle, AudioBus bus, float volume, AudioClip? clip, bool loop,
        IAudioStreamSource? source)
    {
        if (_createStream is null)
            return null;
        // Read ahead of the output: a read the decoder cannot fill yet is not a gap, and no silence stands in for it.
        if (source is AudioStreamTransport transport)
            transport.Gapless = true;
        else if (source is DecodingClipSource decoding)
            decoding.ReadAhead = true;
        var voice = new DeviceVoice(handle, bus, volume, _createStream(), clip, loop, source, source is ScrubAudioSource);
        voice.Stream.Gain = gainOf(voice);
        _deviceVoices.Add(voice);
        // What can go now goes now (a clip whole): the pump only tops up.
        fill(voice, aheadFrames(voice, null));
        return voice;
    }

    /// <summary>
    /// Refills, applies gains and fades, reaps finished voices (the engine calls it every few ms); <paramref name="ahead"/>
    /// queues further for a known stall.
    /// </summary>
    public void Pump(TimeSpan? ahead = null)
    {
        lock (_gate)
        {
            if (_createStream is null)
                return;
            var now = Stopwatch.GetTimestamp();
            var elapsed = (int)Math.Min(Format.SampleRate, Stopwatch.GetElapsedTime(_lastPump, now).TotalSeconds * Format.SampleRate);
            _lastPump = now;
            foreach (var bus in _buses)
                bus.Advance(elapsed);
            _output.Advance(elapsed);
            for (var index = _deviceVoices.Count - 1; index >= 0; index--)
            {
                var voice = _deviceVoices[index];
                if (voice.FadeFramesTotal != 0 && !voice.Paused && (voice.FadeFramesRemaining -= elapsed) <= 0)
                {
                    _deviceVoices.RemoveAt(index);
                    release(voice);
                    continue;
                }
                if (!voice.Paused)
                    fill(voice, aheadFrames(voice, ahead));
                voice.Stream.Gain = gainOf(voice);
                if (voice.Ended && voice.Stream.QueuedFrames == 0)
                {
                    _deviceVoices.RemoveAt(index);
                    release(voice);
                }
            }
        }
    }

    private long aheadFrames(DeviceVoice voice, TimeSpan? ahead) => voice.Live ? LiveAheadFrames
        : (long)(Math.Max(StreamAhead.TotalSeconds, ahead?.TotalSeconds ?? 0) * Format.SampleRate);

    private void fill(DeviceVoice voice, long target)
    {
        var channels = Format.Channels;
        if (voice.Clip is { } clip)
        {
            if (!voice.Loop)
            {
                // Whole, once: SDL holds it from here.
                if (voice.ClipPosition < clip.FrameCount)
                {
                    voice.Stream.Put(clip.Samples[(voice.ClipPosition * channels)..]);
                    voice.ClipPosition = clip.FrameCount;
                }
                voice.Ended = true;
                return;
            }
            var (loopStart, loopEnd) = clip.LoopRegion is { } region ? (region.StartFrame, region.EndFrame) : (0, clip.FrameCount);
            while ((long)voice.Stream.QueuedFrames < target && loopEnd > loopStart)
            {
                if (voice.ClipPosition >= loopEnd)
                    voice.ClipPosition = loopStart;
                var frames = Math.Min(loopEnd - voice.ClipPosition, ReadFrames);
                voice.Stream.Put(clip.Samples.Slice(voice.ClipPosition * channels, frames * channels));
                voice.ClipPosition += frames;
            }
            return;
        }
        var source = voice.Source!;
        if (_scratch.Length < ReadFrames * channels)
            _scratch = new float[ReadFrames * channels];
        while (!voice.Ended && (long)voice.Stream.QueuedFrames < target)
        {
            var samples = source.Read(_scratch.AsSpan(0, ReadFrames * channels));
            if (samples < 0 || samples % channels != 0)
                throw new InvalidDataException("An audio stream source returned a partial or invalid frame count.");
            voice.Stream.Put(_scratch.AsSpan(0, samples));
            if (samples == 0)
            {
                voice.Ended = source.IsCompleted;
                break; // nothing ready yet (still decoding)
            }
        }
    }

    private float gainOf(DeviceVoice voice)
    {
        var bus = _buses[(int)voice.Bus];
        if (bus.Muted)
            return 0f;
        var fade = voice.FadeFramesTotal == 0 ? 1f : Math.Max(0f, (float)voice.FadeFramesRemaining / voice.FadeFramesTotal);
        return Math.Clamp(_masterVolume * _busGains[(int)voice.Bus] * bus.VolumeAt(0) * voice.Volume * fade * _output.VolumeAt(0), 0f, 1f);
    }

    private static void release(DeviceVoice voice)
    {
        voice.Stream.Destroy();
        voice.Source?.Dispose();
    }

    private sealed class DeviceVoice(AudioPlaybackHandle handle, AudioBus bus, float volume, VoiceChannel stream, AudioClip? clip,
        bool loop, IAudioStreamSource? source, bool live)
    {
        public AudioPlaybackHandle Handle { get; } = handle;
        public AudioBus Bus { get; } = bus;
        public float Volume { get; } = volume;
        public VoiceChannel Stream { get; } = stream;
        public AudioClip? Clip { get; } = clip;
        public bool Loop { get; } = loop;
        public IAudioStreamSource? Source { get; } = source;
        public bool Live { get; } = live;
        public int ClipPosition { get; set; }
        // Everything there is has been put in (a clip whole, or a finished source): it ends once the stream drains.
        public bool Ended { get; set; }
        public long FadeFramesTotal { get; private set; }
        public long FadeFramesRemaining { get; set; }
        private bool _paused;

        public bool Paused
        {
            get => _paused;
            set => Stream.Paused = _paused = value;
        }

        public void BeginFade(long frames)
        {
            if (FadeFramesRemaining != 0 && FadeFramesRemaining <= frames)
                return;
            FadeFramesTotal = frames;
            FadeFramesRemaining = frames;
        }
    }
}
