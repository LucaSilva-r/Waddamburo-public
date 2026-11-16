using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>What <see cref="AudioEngine"/> reads from a playback device for its clock.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>The format the mixer renders. Samples are interleaved floats.</summary>
    SdlAudioFormat Format { get; }

    SdlAudioFormat HardwareFormat { get; }

    int HardwareBufferFrames { get; }

    string Driver { get; }

    /// <summary>Frames handed to the device since it opened.</summary>
    ulong SubmittedFrames { get; }

    /// <summary>Submitted frames not played yet.</summary>
    ulong QueuedFrames { get; }

    /// <summary>Device-side numbers for the audio clock's diagnostic log line; empty when there are none.</summary>
    string Diagnostics => "";
}

/// <summary>An output whose device asks for each buffer (WASAPI exclusive, ASIO): the mix runs on its thread.</summary>
public interface IPullAudioOutput : IAudioOutput
{
    /// <summary>Starts rendering: <paramref name="render"/> fills one device buffer each time the device asks.</summary>
    void Start(Action<Span<float>> render);

    /// <summary>The device thread's error; it then goes silent.</summary>
    Exception? Failure { get; }
}

/// <summary>
/// WASAPI exclusive-mode, event-driven playback of the default endpoint at the device's minimum
/// period. The device thread renders each endpoint buffer straight from the mixer (pull), so a
/// sound starts at most one period after it is played, and heard about two periods later. The
/// endpoint is held until disposal: other applications are silent meanwhile.
/// </summary>
/// <remarks>COM is driven through raw vtable slots so nothing depends on built-in COM interop.</remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WasapiExclusiveOutput : IPullAudioOutput
{
    private const int ExclusiveMode = 1; // AUDCLNT_SHAREMODE_EXCLUSIVE
    private const uint EventCallback = 0x00040000; // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    private const int BufferSizeNotAligned = unchecked((int)0x88890019);
    private const uint BufferFlagsSilent = 2;

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IidAudioClock = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");

    // Diagnostics (the audio clock's log line): late device events, and the device's own play position.
    private long _lateEvents, _worstGapTicks, _devicePlayedFrames = -1;
    // The mix's time per buffer (total, count, worst) and the GC's pause total, since the last diagnostic line.
    private long _renderTicks, _renders, _worstRenderTicks;
    private TimeSpan _gcPause = GC.GetTotalPauseDuration();

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new();
    private readonly AutoResetEvent _bufferReady = new(false);
    private Exception? _openFailure;
    private Action<Span<float>>? _render;
    private volatile bool _stopping;
    private long _submittedFrames;
    // Silent frames the client was given before the mix (the prime and the probe): the device clock counts them.
    private long _silentFrames;
    private nint _client;
    // The thread's COM objects and the format the device took.
    private nint _device, _renderClient, _clock;
    private ulong _clockFrequency;
    private WaveFormat _format;
    // Submitted frames when the current client opened (its clock counts from there).
    private long _submittedAtOpen;
    private bool _pcm16;

    /// <summary>
    /// Opens the endpoint named <paramref name="deviceName"/> (its friendly name; null or not found = the default);
    /// <paramref name="periodFrames"/> 0 = the device minimum period (its default when the minimum proves too short),
    /// -1 = its default period.
    /// </summary>
    public WasapiExclusiveOutput(int sampleRate = SdlAudioDevice.DefaultSampleRate, int channels = SdlAudioDevice.DefaultChannels,
        int periodFrames = 0, string? deviceName = null)
    {
        Format = HardwareFormat = new SdlAudioFormat(sampleRate, channels);
        // One MTA thread owns every COM object: no apartment marshalling, and the render loop is its own.
        _thread = new Thread(() => run(periodFrames, deviceName)) { IsBackground = true, Name = "WASAPI exclusive", Priority = ThreadPriority.Highest };
        _thread.Start();
        _opened.Wait();
        if (_openFailure is not null)
        {
            _thread.Join();
            throw _openFailure;
        }
    }

    public SdlAudioFormat Format { get; }
    public SdlAudioFormat HardwareFormat { get; }
    public int HardwareBufferFrames { get; private set; }
    public string Driver => _pcm16 ? "wasapi-exclusive (PCM16)" : "wasapi-exclusive (float)";
    public ulong SubmittedFrames => (ulong)Interlocked.Read(ref _submittedFrames);

    /// <summary>The device thread's error (the endpoint was lost, or a buffer call failed); it then goes silent.</summary>
    public Exception? Failure { get; private set; }

    // ponytail: each event writes a whole buffer behind the one playing, so about one buffer is always
    // queued; GetCurrentPadding from the game thread would be exact but crosses COM apartments.
    public ulong QueuedFrames => (ulong)HardwareBufferFrames;

    // Played by the device (its IAudioClock, primed silence included) less submitted: grows when events are missed.
    public string Diagnostics
    {
        get
        {
            var played = Interlocked.Read(ref _devicePlayedFrames);
            var gap = Interlocked.Exchange(ref _worstGapTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var device = played < 0 ? "no device clock"
                : FormattableString.Invariant($"device ahead of submitted {(played - _silentFrames - ((long)SubmittedFrames - _submittedAtOpen)) * 1000.0 / Format.SampleRate:F1} ms");
            var renders = Interlocked.Exchange(ref _renders, 0);
            var mix = Interlocked.Exchange(ref _renderTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, renders);
            var worstMix = Interlocked.Exchange(ref _worstRenderTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var pause = GC.GetTotalPauseDuration();
            var gc = (pause - _gcPause).TotalMilliseconds;
            _gcPause = pause;
            return FormattableString.Invariant(
                $"exclusive period {HardwareBufferFrames * 1000.0 / Format.SampleRate:F2} ms, late events {Interlocked.Exchange(ref _lateEvents, 0)}, worst gap {gap:F1} ms, mix {mix:F2} ms (worst {worstMix:F1}), GC pauses {gc:F1} ms, {device}");
        }
    }

    /// <summary>Starts rendering: <paramref name="render"/> fills one endpoint buffer per device period.</summary>
    public void Start(Action<Span<float>> render) => Volatile.Write(ref _render, render);

    private void run(int periodFrames, string? deviceName)
    {
        nint enumerator = 0;
        nint task = 0;
        long defaultPeriod;
        // Device minimum: watched while it plays, the default period taken when it can't keep up.
        var watched = false;
        try
        {
            check(CoInitializeEx(0, 0), "initialize COM");
            var clsid = ClsidDeviceEnumerator;
            var enumeratorIid = IidDeviceEnumerator;
            check(CoCreateInstance(&clsid, 0, 0x17, &enumeratorIid, &enumerator), "create the device enumerator");
            _device = deviceName is null ? 0 : named(enumerator, deviceName);
            if (_device == 0)
            {
                nint fallback;
                check(call(enumerator, 4, 0, 0, (nint)(&fallback)), "get the default playback device"); // eRender, eConsole
                _device = fallback;
            }
            _client = activate(_device);
            // Float first (no conversion); most hardware only takes integer PCM in exclusive mode.
            _pcm16 = !supported(pcm16: false);
            if (_pcm16 && !supported(pcm16: true))
                throw new InvalidOperationException(
                    $"The device takes neither float nor 16-bit PCM at {Format.SampleRate} Hz, {Format.Channels} channels in exclusive mode.");

            long minimumPeriod;
            check(call(_client, 9, (nint)(&defaultPeriod), (nint)(&minimumPeriod)), "read the device period");
            uint taskIndex = 0;
            task = AvSetMmThreadCharacteristicsW("Pro Audio", &taskIndex); // 0 = unavailable; harmless
            if (periodFrames < 0 || minimumPeriod <= 0)
                open(defaultPeriod);
            else if (periodFrames > 0)
                open(duration(periodFrames));
            else
            {
                open(minimumPeriod);
                // Some drivers (USB class ones) report a minimum they can't keep: events come late, the sound breaks
                // up and the whole PC crawls. Half a second of silence tells; then the device's default period.
                var late = minimumPeriod < defaultPeriod ? lateShare() : 0;
                Console.WriteLine(FormattableString.Invariant(
                    $"Audio: {late:P1} of the exclusive device's events late at its minimum period{(late > TooLate ? ", using its default" : "")}."));
                if (late > TooLate)
                {
                    reopen(defaultPeriod);
                    raisePeriodFallback();
                }
                else
                    watched = minimumPeriod < defaultPeriod;
            }
        }
        catch (Exception exception)
        {
            _openFailure = exception;
            if (task != 0)
                _ = AvRevertMmThreadCharacteristics(task);
            release(ref _clock);
            release(ref _renderClient);
            release(ref _client);
            release(ref _device);
            release(ref enumerator);
            _opened.Set();
            return;
        }
        _opened.Set();

        var channels = Format.Channels;
        var samples = new float[HardwareBufferFrames * channels];
        var periodTicks = ticks(HardwareBufferFrames);
        long lastEvent = 0, windowStart = System.Diagnostics.Stopwatch.GetTimestamp(), windowEvents = 0, windowLate = 0;
        try
        {
            while (!_stopping)
            {
                if (!_bufferReady.WaitOne(200))
                    continue;
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastEvent != 0)
                {
                    var gap = now - lastEvent;
                    windowEvents++;
                    if (gap > periodTicks * 3 / 2)
                    {
                        windowLate++;
                        Interlocked.Increment(ref _lateEvents);
                    }
                    if (gap > Interlocked.Read(ref _worstGapTicks))
                        Interlocked.Exchange(ref _worstGapTicks, gap);
                }
                lastEvent = now;
                // Each second at the minimum period (wall time: a device that slows down sends fewer events): too many
                // late events take the default period for the rest of the run. A USB class driver was seen keeping its
                // 3 ms minimum for minutes, then sending events at half rate (the sound starved).
                if (watched && now - windowStart >= System.Diagnostics.Stopwatch.Frequency)
                {
                    // A window of a few events (one long stall) says nothing about the period.
                    if (windowEvents >= 20 && (double)windowLate / windowEvents > TooLate)
                    {
                        Console.WriteLine(FormattableString.Invariant(
                            $"Audio: {(double)windowLate / windowEvents:P1} of the exclusive device's events late at its minimum period while playing, using its default."));
                        reopen(defaultPeriod);
                        raisePeriodFallback();
                        watched = false;
                        samples = new float[HardwareBufferFrames * channels];
                        periodTicks = ticks(HardwareBufferFrames);
                        lastEvent = 0;
                        continue;
                    }
                    (windowStart, windowEvents, windowLate) = (now, 0, 0);
                }
                ulong devicePosition, qpc;
                if (_clockFrequency != 0 && call(_clock, 4, (nint)(&devicePosition), (nint)(&qpc)) >= 0)
                    Interlocked.Exchange(ref _devicePlayedFrames, (long)(devicePosition * (ulong)Format.SampleRate / _clockFrequency));
                byte* data;
                check(call(_renderClient, 3, (uint)HardwareBufferFrames, (nint)(&data)), "get a buffer");
                var render = Volatile.Read(ref _render);
                var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (render is null)
                    Array.Clear(samples);
                else
                    render(samples);
                // The mix's own time: when it nears the period, late events are the game's, not the device's.
                var renderTicks = System.Diagnostics.Stopwatch.GetTimestamp() - renderStart;
                Interlocked.Add(ref _renderTicks, renderTicks);
                Interlocked.Increment(ref _renders);
                if (renderTicks > Interlocked.Read(ref _worstRenderTicks))
                    Interlocked.Exchange(ref _worstRenderTicks, renderTicks);
                if (_pcm16)
                {
                    var output = (short*)data;
                    for (var index = 0; index < samples.Length; index++)
                        output[index] = (short)(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
                }
                else
                    samples.AsSpan().CopyTo(new Span<float>(data, samples.Length));
                check(call(_renderClient, 4, (uint)HardwareBufferFrames, 0u), "release a buffer");
                // ponytail: counted after the mixer ran, so a position read in between sees one period
                // less submitted; AudioPlaybackClock slews over that. Lock with the engine if it shows.
                Interlocked.Add(ref _submittedFrames, HardwareBufferFrames);
            }
        }
        catch (Exception exception)
        {
            // ponytail: an unplugged or reclaimed endpoint stops the sound; no reopen or fallback to shared.
            Failure = exception;
        }
        finally
        {
            if (task != 0)
                _ = AvRevertMmThreadCharacteristics(task);
            if (_client != 0)
                call(_client, 11);
            release(ref _clock);
            release(ref _renderClient);
            var client = _client;
            _client = 0;
            release(ref client);
            release(ref _device);
            release(ref enumerator);
        }
    }

    // More late device events than this share: the period is too short for the device.
    private const double TooLate = 0.1;

    private void raisePeriodFallback() => AudioNotices.Raise(AudioProblem.ExclusivePeriodFallback, FormattableString.Invariant(
        $"{HardwareBufferFrames}|{HardwareBufferFrames * 1000.0 / Format.SampleRate:0.#}"));

    private long ticks(int frames) => (long)((double)frames / Format.SampleRate * System.Diagnostics.Stopwatch.Frequency);

    // Initializes _client at period (a whole number of the device's blocks when it asks), primes it and starts it.
    private void open(long period)
    {
        var format = _format;
        var result = call(_client, 3, ExclusiveMode, EventCallback, period, period, (nint)(&format), (nint)0);
        if (result == BufferSizeNotAligned)
        {
            // The device wants a whole number of its own blocks: a failed client can't retry, take a new one.
            uint aligned;
            check(call(_client, 4, (nint)(&aligned)), "read the aligned buffer size");
            release(ref _client);
            _client = activate(_device);
            period = duration((int)aligned);
            result = call(_client, 3, ExclusiveMode, EventCallback, period, period, (nint)(&format), (nint)0);
        }
        check(result, "open the device in exclusive mode (another application may be using it)");
        uint frames;
        check(call(_client, 4, (nint)(&frames)), "read the buffer size");
        HardwareBufferFrames = (int)frames;
        check(call(_client, 13, _bufferReady.SafeWaitHandle.DangerousGetHandle()), "set the buffer event");
        var renderIid = IidRenderClient;
        nint renderClient, clock = 0;
        check(call(_client, 14, (nint)(&renderIid), (nint)(&renderClient)), "get the render client");
        _renderClient = renderClient;
        var clockIid = IidAudioClock;
        ulong frequency = 0;
        if (call(_client, 14, (nint)(&clockIid), (nint)(&clock)) < 0 || call(clock, 3, (nint)(&frequency)) < 0)
            frequency = 0; // diagnostics only
        (_clock, _clockFrequency) = (clock, frequency);
        // A new client's clock starts over: the diagnostics count from here.
        Interlocked.Exchange(ref _devicePlayedFrames, -1);
        _submittedAtOpen = Interlocked.Read(ref _submittedFrames);
        _silentFrames = 0;
        // Pre-fill one silent buffer so the first event has something playing behind it.
        silence();
        check(call(_client, 10), "start playback");
    }

    // Stops and drops the client, then opens a new one at period.
    private void reopen(long period)
    {
        call(_client, 11);
        release(ref _clock);
        release(ref _renderClient);
        release(ref _client);
        _client = activate(_device);
        open(period);
    }

    private void silence()
    {
        byte* data;
        check(call(_renderClient, 3, (uint)HardwareBufferFrames, (nint)(&data)), "fill a silent buffer");
        check(call(_renderClient, 4, (uint)HardwareBufferFrames, BufferFlagsSilent), "fill a silent buffer");
        _silentFrames += HardwareBufferFrames;
    }

    // The share of device events more than half a period late over half a second of silence.
    private double lateShare()
    {
        var periodTicks = ticks(HardwareBufferFrames);
        var end = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 2;
        long last = 0, events = 0, late = 0;
        while (System.Diagnostics.Stopwatch.GetTimestamp() < end)
        {
            if (!_bufferReady.WaitOne(200))
                return 1; // no events at all
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (last != 0 && now - last > periodTicks * 3 / 2)
                late++;
            (last, events) = (now, events + 1);
            silence();
        }
        return events < 2 ? 1 : (double)late / (events - 1);
    }

    // The active playback endpoint whose friendly name is name (as SDL lists it), or 0.
    private static nint named(nint enumerator, string name)
    {
        nint collection = 0;
        if (call(enumerator, 3, 0, 1u, (nint)(&collection)) < 0) // EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)
            return 0;
        try
        {
            uint count;
            if (call(collection, 3, (nint)(&count)) < 0)
                return 0;
            for (var index = 0u; index < count; index++)
            {
                nint device = 0;
                if (call(collection, 4, index, (nint)(&device)) < 0)
                    continue;
                if (friendlyName(device) == name)
                    return device;
                release(ref device);
            }
            Console.Error.WriteLine($"Audio: no playback device named '{name}', using the default.");
            AudioNotices.Raise(AudioProblem.DeviceMissing, name);
            return 0;
        }
        finally
        {
            release(ref collection);
        }
    }

    private static string? friendlyName(nint device)
    {
        nint store = 0;
        if (call(device, 4, 0u, (nint)(&store)) < 0) // OpenPropertyStore(STGM_READ)
            return null;
        try
        {
            var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 }; // PKEY_Device_FriendlyName
            var value = stackalloc byte[32]; // PROPVARIANT: 24 bytes on x64
            new Span<byte>(value, 32).Clear();
            if (call(store, 5, (nint)(&key), (nint)value) < 0) // IPropertyStore.GetValue
                return null;
            var name = *(ushort*)value == 31 ? Marshal.PtrToStringUni(*(nint*)(value + 8)) : null; // VT_LPWSTR
            _ = PropVariantClear(value);
            return name;
        }
        finally
        {
            release(ref store);
        }
    }

    // Sets _format to the float or 16-bit format; true when the client takes it in exclusive mode.
    private bool supported(bool pcm16)
    {
        var bits = pcm16 ? 16 : 32;
        _format = new WaveFormat
        {
            Tag = (ushort)(pcm16 ? 1 : 3), // WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT
            Channels = (ushort)Format.Channels,
            SamplesPerSecond = (uint)Format.SampleRate,
            BitsPerSample = (ushort)bits,
            BlockAlign = (ushort)(Format.Channels * bits / 8),
            AverageBytesPerSecond = (uint)(Format.SampleRate * Format.Channels * bits / 8),
        };
        var format = _format;
        return call(_client, 7, ExclusiveMode, (nint)(&format), (nint)0) == 0;
    }

    private long duration(int frames) => (10_000_000L * frames + Format.SampleRate / 2) / Format.SampleRate;

    private static nint activate(nint device)
    {
        nint client;
        var iid = IidAudioClient;
        check(call(device, 3, (nint)(&iid), 0x17u, (nint)0, (nint)(&client)), "activate the audio client");
        return client;
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        _bufferReady.Set();
        _thread.Join();
        _bufferReady.Dispose();
        _opened.Dispose();
    }

    private static void check(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException($"WASAPI failed to {operation} (0x{result:X8}).");
    }

    private static void release(ref nint unknown)
    {
        if (unknown == 0)
            return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable(unknown, 2))(unknown);
        unknown = 0;
    }

    private static void* vtable(nint self, int slot) => (*(void***)self)[slot];

    private static int call(nint self, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)vtable(self, slot))(self);

    private static int call<T>(nint self, int slot, T a) where T : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, int>)vtable(self, slot))(self, a);

    private static int call<T, U>(nint self, int slot, T a, U b) where T : unmanaged where U : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, int>)vtable(self, slot))(self, a, b);

    private static int call<T, U, V>(nint self, int slot, T a, U b, V c) where T : unmanaged where U : unmanaged where V : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, V, int>)vtable(self, slot))(self, a, b, c);

    private static int call<T, U, V, W>(nint self, int slot, T a, U b, V c, W d)
        where T : unmanaged where U : unmanaged where V : unmanaged where W : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, V, W, int>)vtable(self, slot))(self, a, b, c, d);

    private static int call<T, U, V, W, X, Y>(nint self, int slot, T a, U b, V c, W d, X e, Y f)
        where T : unmanaged where U : unmanaged where V : unmanaged where W : unmanaged where X : unmanaged where Y : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, V, W, X, Y, int>)vtable(self, slot))(self, a, b, c, d, e, f);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(byte* value);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string task, uint* taskIndex);

    [DllImport("avrt.dll")]
    private static extern int AvRevertMmThreadCharacteristics(nint handle);
}
