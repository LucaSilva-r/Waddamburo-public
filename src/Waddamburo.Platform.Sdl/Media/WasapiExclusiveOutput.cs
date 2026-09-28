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
}

/// <summary>
/// WASAPI exclusive-mode, event-driven playback of the default endpoint at the device's minimum
/// period. The device thread renders each endpoint buffer straight from the mixer (pull), so a
/// sound starts at most one period after it is played, and heard about two periods later. The
/// endpoint is held until disposal: other applications are silent meanwhile.
/// </summary>
/// <remarks>COM is driven through raw vtable slots so nothing depends on built-in COM interop.</remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WasapiExclusiveOutput : IAudioOutput
{
    private const int ExclusiveMode = 1; // AUDCLNT_SHAREMODE_EXCLUSIVE
    private const uint EventCallback = 0x00040000; // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    private const int BufferSizeNotAligned = unchecked((int)0x88890019);
    private const uint BufferFlagsSilent = 2;

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new();
    private readonly AutoResetEvent _bufferReady = new(false);
    private Exception? _openFailure;
    private Action<Span<float>>? _render;
    private volatile bool _stopping;
    private long _submittedFrames;
    private nint _client;
    private bool _pcm16;

    /// <summary>Opens the default endpoint; <paramref name="periodFrames"/> 0 = the device minimum period.</summary>
    public WasapiExclusiveOutput(int sampleRate = SdlAudioDevice.DefaultSampleRate, int channels = SdlAudioDevice.DefaultChannels,
        int periodFrames = 0)
    {
        Format = HardwareFormat = new SdlAudioFormat(sampleRate, channels);
        // One MTA thread owns every COM object: no apartment marshalling, and the render loop is its own.
        _thread = new Thread(() => run(periodFrames)) { IsBackground = true, Name = "WASAPI exclusive", Priority = ThreadPriority.Highest };
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

    /// <summary>Starts rendering: <paramref name="render"/> fills one endpoint buffer per device period.</summary>
    public void Start(Action<Span<float>> render) => Volatile.Write(ref _render, render);

    private void run(int periodFrames)
    {
        nint enumerator = 0, device = 0, renderClient = 0;
        nint task = 0;
        try
        {
            check(CoInitializeEx(0, 0), "initialize COM");
            var clsid = ClsidDeviceEnumerator;
            var enumeratorIid = IidDeviceEnumerator;
            check(CoCreateInstance(&clsid, 0, 0x17, &enumeratorIid, &enumerator), "create the device enumerator");
            check(call(enumerator, 4, 0, 0, (nint)(&device)), "get the default playback device"); // eRender, eConsole
            var format = stackalloc WaveFormat[1];
            _client = activate(device);
            // Float first (no conversion); most hardware only takes integer PCM in exclusive mode.
            _pcm16 = !supported(format, pcm16: false);
            if (_pcm16 && !supported(format, pcm16: true))
                throw new InvalidOperationException(
                    $"The device takes neither float nor 16-bit PCM at {Format.SampleRate} Hz, {Format.Channels} channels in exclusive mode.");

            long defaultPeriod, minimumPeriod;
            check(call(_client, 9, (nint)(&defaultPeriod), (nint)(&minimumPeriod)), "read the device period");
            var period = periodFrames > 0 ? duration(periodFrames) : minimumPeriod > 0 ? minimumPeriod : defaultPeriod;
            var result = call(_client, 3, ExclusiveMode, EventCallback, period, period, (nint)format, (nint)0);
            if (result == BufferSizeNotAligned)
            {
                // The device wants a whole number of its own blocks: a failed client can't retry, take a new one.
                uint aligned;
                check(call(_client, 4, (nint)(&aligned)), "read the aligned buffer size");
                release(ref _client);
                _client = activate(device);
                period = duration((int)aligned);
                result = call(_client, 3, ExclusiveMode, EventCallback, period, period, (nint)format, (nint)0);
            }
            check(result, "open the device in exclusive mode (another application may be using it)");
            uint frames;
            check(call(_client, 4, (nint)(&frames)), "read the buffer size");
            HardwareBufferFrames = (int)frames;
            check(call(_client, 13, _bufferReady.SafeWaitHandle.DangerousGetHandle()), "set the buffer event");
            var renderIid = IidRenderClient;
            check(call(_client, 14, (nint)(&renderIid), (nint)(&renderClient)), "get the render client");
            // Pre-fill one silent buffer so the first event has something playing behind it.
            byte* data;
            check(call(renderClient, 3, frames, (nint)(&data)), "prime the buffer");
            check(call(renderClient, 4, frames, BufferFlagsSilent), "prime the buffer");
            check(call(_client, 10), "start playback");
            uint taskIndex = 0;
            task = AvSetMmThreadCharacteristicsW("Pro Audio", &taskIndex); // 0 = unavailable; harmless
        }
        catch (Exception exception)
        {
            _openFailure = exception;
            release(ref renderClient);
            release(ref _client);
            release(ref device);
            release(ref enumerator);
            _opened.Set();
            return;
        }
        _opened.Set();

        var channels = Format.Channels;
        var samples = new float[HardwareBufferFrames * channels];
        try
        {
            while (!_stopping)
            {
                if (!_bufferReady.WaitOne(200))
                    continue;
                byte* data;
                check(call(renderClient, 3, (uint)HardwareBufferFrames, (nint)(&data)), "get a buffer");
                var render = Volatile.Read(ref _render);
                if (render is null)
                    Array.Clear(samples);
                else
                    render(samples);
                if (_pcm16)
                {
                    var output = (short*)data;
                    for (var index = 0; index < samples.Length; index++)
                        output[index] = (short)(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
                }
                else
                    samples.AsSpan().CopyTo(new Span<float>(data, samples.Length));
                check(call(renderClient, 4, (uint)HardwareBufferFrames, 0u), "release a buffer");
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
            release(ref renderClient);
            var client = _client;
            _client = 0;
            release(ref client);
            release(ref device);
            release(ref enumerator);
        }
    }

    private bool supported(WaveFormat* format, bool pcm16)
    {
        var bits = pcm16 ? 16 : 32;
        *format = new WaveFormat
        {
            Tag = (ushort)(pcm16 ? 1 : 3), // WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT
            Channels = (ushort)Format.Channels,
            SamplesPerSecond = (uint)Format.SampleRate,
            BitsPerSample = (ushort)bits,
            BlockAlign = (ushort)(Format.Channels * bits / 8),
            AverageBytesPerSecond = (uint)(Format.SampleRate * Format.Channels * bits / 8),
        };
        return call(_client, 7, ExclusiveMode, (nint)format, (nint)0) == 0;
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

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string task, uint* taskIndex);

    [DllImport("avrt.dll")]
    private static extern int AvRevertMmThreadCharacteristics(nint handle);
}
