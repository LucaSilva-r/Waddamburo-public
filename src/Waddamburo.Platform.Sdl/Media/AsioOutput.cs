using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// ASIO playback on an installed driver's first two outputs. The driver calls back for each buffer half
/// (bufferSwitch) on its own thread and the mixer renders straight into it (pull), converted to the driver's sample
/// type. The driver is loaded, started, stopped and released on one thread of ours, as ASIO hosts do. One driver at a
/// time: other programs can't use it meanwhile, as the driver decides.
/// </summary>
/// <remarks>
/// The IASIO interface is driven through raw vtable slots (the layout every ASIO driver exports), x64 only: on 32-bit
/// Windows the drivers use thiscall. No SDK code is used.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class AsioOutput : IPullAudioOutput
{
    // ASIO's own result codes and enumerations (long = 32 bits on Windows).
    private const int AsioOk = 0, AsioSuccess = 0x3f4847a0;
    private const int SelectorSupported = 1, EngineVersion = 2, ResetRequest = 3, BufferSizeChange = 4, ResyncRequest = 5,
        LatenciesChanged = 6, SupportsTimeInfo = 7;
    private const int Int16Lsb = 16, Int24Lsb = 17, Int32Lsb = 18, Float32Lsb = 19, Float64Lsb = 20, Int32Lsb16 = 24,
        Int32Lsb24 = 27;
    private const int Channels = 2;

    // The callbacks carry no context: the playing output (one driver at a time).
    private static AsioOutput? _current;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new();
    private readonly ManualResetEventSlim _stop = new();
    private Exception? _openFailure;
    private Action<Span<float>>? _render;
    private nint _driver;
    private BufferInfo* _buffers;
    private Callbacks* _callbacks;
    private int _sampleType;
    private bool _outputReady, _started;
    private int _outputPair;
    private readonly AutoResetEvent _reset = new(false);
    private float[] _samples = [];
    private long _submittedFrames;
    // Diagnostics: late callbacks, the worst gap, the mix's time, the GC's pauses.
    private long _lastCallback, _lateCallbacks, _worstGapTicks, _renderTicks, _renders, _worstRenderTicks;
    private TimeSpan _gcPause = GC.GetTotalPauseDuration();

    /// <summary>The installed ASIO drivers' names (HKLM\SOFTWARE\ASIO).</summary>
    public static string[] DriverNames()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO");
        return key?.GetSubKeyNames() ?? [];
    }

    /// <summary>
    /// Loads the driver named <paramref name="driverName"/> (null or not installed: the first one) at 48 kHz when it
    /// can (else at its own rate); <paramref name="bufferFrames"/> 0 = its preferred size, else the nearest it allows;
    /// <paramref name="outputPair"/> the stereo pair played on (0 = outputs 1-2, 1 = 3-4; one past the driver's: 1-2).
    /// </summary>
    public AsioOutput(string? driverName = null, int bufferFrames = 0, int outputPair = 0)
    {
        _outputPair = outputPair;
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("ASIO needs the 64-bit game.");
        if (Interlocked.CompareExchange(ref _current, this, null) is not null)
            throw new InvalidOperationException("An ASIO driver is already open.");
        _thread = new Thread(() => run(driverName, bufferFrames)) { IsBackground = true, Name = "ASIO" };
        // Drivers expect to be loaded and driven from a single-threaded apartment.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _opened.Wait();
        if (_openFailure is not null)
        {
            _thread.Join();
            _current = null;
            throw _openFailure;
        }
    }

    public SdlAudioFormat Format { get; private set; }
    public SdlAudioFormat HardwareFormat => Format;
    public int HardwareBufferFrames { get; private set; }
    public string Driver { get; private set; } = "asio";
    public ulong SubmittedFrames => (ulong)Interlocked.Read(ref _submittedFrames);

    /// <summary>The driver's outputs (mono channels).</summary>
    public int OutputCount { get; private set; }

    /// <summary>The stereo pair played on (0 = outputs 1-2), after a missing one fell back.</summary>
    public int OutputPair => _outputPair;

    /// <summary>The driver's output latency (its buffer included), as it reports it.</summary>
    public int OutputLatencyFrames { get; private set; }

    // ponytail: the driver's reported output latency stands for the frames queued; getSamplePosition would be exact.
    public ulong QueuedFrames => (ulong)OutputLatencyFrames;

    public Exception? Failure { get; private set; }

    public string Diagnostics
    {
        get
        {
            var gap = Interlocked.Exchange(ref _worstGapTicks, 0) * 1000.0 / Stopwatch.Frequency;
            var renders = Interlocked.Exchange(ref _renders, 0);
            var mix = Interlocked.Exchange(ref _renderTicks, 0) * 1000.0 / Stopwatch.Frequency / Math.Max(1, renders);
            var worstMix = Interlocked.Exchange(ref _worstRenderTicks, 0) * 1000.0 / Stopwatch.Frequency;
            var pause = GC.GetTotalPauseDuration();
            var gc = (pause - _gcPause).TotalMilliseconds;
            _gcPause = pause;
            return FormattableString.Invariant(
                $"asio buffer {HardwareBufferFrames * 1000.0 / Format.SampleRate:F2} ms, output latency {OutputLatencyFrames * 1000.0 / Format.SampleRate:F1} ms, late callbacks {Interlocked.Exchange(ref _lateCallbacks, 0)}, worst gap {gap:F1} ms, mix {mix:F2} ms (worst {worstMix:F1}), GC pauses {gc:F1} ms");
        }
    }

    public void Start(Action<Span<float>> render) => Volatile.Write(ref _render, render);

    private void run(string? driverName, int requestedFrames)
    {
        _ = CoInitializeEx(0, 2); // COINIT_APARTMENTTHREADED
        try
        {
            open(driverName, requestedFrames);
        }
        catch (Exception exception)
        {
            _openFailure = exception;
            close();
            CoUninitialize();
            _opened.Set();
            return;
        }
        _opened.Set();
        // A reset request (the driver's settings changed: its device, buffer or rate) reloads it, as ASIO hosts must;
        // writing on into the old buffers crackles.
        WaitHandle[] events = [_stop.WaitHandle, _reset];
        while (WaitHandle.WaitAny(events) == 1)
        {
            Thread.Sleep(200); // a panel change sends several in a row: one reload
            _reset.Reset();
            Console.WriteLine("Audio: the ASIO driver asked for a reset, reloading it.");
            close();
            try
            {
                open(driverName, requestedFrames);
                Console.WriteLine(FormattableString.Invariant(
                    $"Audio: {Driver}, {HardwareBufferFrames} frames at {Format.SampleRate} Hz, output latency {OutputLatencyFrames * 1000.0 / Format.SampleRate:F1} ms."));
            }
            catch (Exception exception)
            {
                Failure = exception;
                close();
                break;
            }
        }
        _stop.Wait();
        close();
        // The thread leaves COM as it entered it: a driver can keep per-thread state until then (a game that hung on
        // exit after ASIO4ALL was released).
        CoUninitialize();
    }

    // Loads, sets up and starts the driver (again after a reset: at the rate the mix was built for).
    private void open(string? driverName, int requestedFrames)
    {
        {
            var names = DriverNames();
            if (names.Length == 0)
                throw new InvalidOperationException("No ASIO driver is installed.");
            var name = driverName is not null && names.Contains(driverName) ? driverName : names[0];
            if (driverName is not null && name != driverName)
                Console.Error.WriteLine($"Audio: no ASIO driver named '{driverName}', using '{name}'.");
            using (var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\ASIO\{name}"))
            {
                if (key?.GetValue("CLSID") is not string text || !Guid.TryParse(text, out var clsid))
                    throw new InvalidOperationException($"The ASIO driver '{name}' has no CLSID.");
                nint driver;
                // An ASIO driver's interface id is its class id.
                check(CoCreateInstance(&clsid, 0, 1, &clsid, &driver), $"load the ASIO driver '{name}'");
                _driver = driver;
            }
            if (call(_driver, 3, GetDesktopWindow()) == 0) // init(sysHandle): an ASIOBool
                throw new InvalidOperationException($"The ASIO driver '{name}' failed to start: {errorMessage()}");
            Driver = $"asio ({name})";

            int inputs, outputs;
            asio(call(_driver, 9, (nint)(&inputs), (nint)(&outputs)), "count the channels");
            if (outputs < Channels)
                throw new InvalidOperationException($"The ASIO driver '{name}' has {outputs} output(s), the game needs two.");
            OutputCount = outputs;
            if ((_outputPair + 1) * Channels > outputs)
            {
                Console.Error.WriteLine($"Audio: the ASIO driver '{name}' has no outputs {_outputPair * 2 + 1}-{_outputPair * 2 + 2}, using 1-2.");
                _outputPair = 0;
            }

            // 48 kHz like the other outputs when the driver takes it; its own rate otherwise (the mix follows).
            double rate;
            if (call(_driver, 12, 48000.0) == AsioOk)
                asio(call(_driver, 14, 48000.0), "set the sample rate");
            asio(call(_driver, 13, (nint)(&rate)), "read the sample rate");
            if (Format.SampleRate != 0 && (int)rate != Format.SampleRate)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The ASIO driver now runs at {rate} Hz, not {Format.SampleRate} Hz; restart the game."));
            Format = new SdlAudioFormat((int)rate, Channels);

            int minimum, maximum, preferred, granularity;
            asio(call(_driver, 11, (nint)(&minimum), (nint)(&maximum), (nint)(&preferred), (nint)(&granularity)), "read the buffer sizes");
            HardwareBufferFrames = bufferSize(requestedFrames, minimum, maximum, preferred, granularity);

            var info = stackalloc ChannelInfo[1];
            *info = new ChannelInfo { Channel = _outputPair * Channels, IsInput = 0 };
            asio(call(_driver, 18, (nint)info), "read the output format");
            _sampleType = info->Type;
            if (sampleBytes(_sampleType) == 0)
                throw new InvalidOperationException($"The ASIO driver '{name}' uses sample type {_sampleType}, which the game can't write.");

            _buffers = (BufferInfo*)NativeMemory.AllocZeroed((nuint)(sizeof(BufferInfo) * Channels));
            for (var channel = 0; channel < Channels; channel++)
                _buffers[channel] = new BufferInfo { IsInput = 0, Channel = _outputPair * Channels + channel };
            _callbacks = (Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Callbacks));
            _callbacks->BufferSwitch = &bufferSwitch;
            _callbacks->SampleRateChanged = &sampleRateChanged;
            _callbacks->Message = &message;
            _callbacks->BufferSwitchTimeInfo = &bufferSwitchTimeInfo;
            _samples = new float[HardwareBufferFrames * Channels];
            asio(call(_driver, 19, (nint)_buffers, Channels, HardwareBufferFrames, (nint)_callbacks), "create the buffers");

            int inputLatency, outputLatency;
            if (call(_driver, 10, (nint)(&inputLatency), (nint)(&outputLatency)) == AsioOk)
                OutputLatencyFrames = outputLatency;
            if (OutputLatencyFrames <= 0)
                OutputLatencyFrames = HardwareBufferFrames;
            _outputReady = call(_driver, 23) == AsioOk; // outputReady: the driver plays a half as soon as it's told
            // Both halves silent before the first callback.
            for (var half = 0; half < 2; half++)
                for (var channel = 0; channel < Channels; channel++)
                    new Span<byte>(half == 0 ? _buffers[channel].Buffer0 : _buffers[channel].Buffer1,
                        HardwareBufferFrames * sampleBytes(_sampleType)).Clear();
            _lastCallback = 0;
            asio(call(_driver, 7), "start");
            _started = true;
        }
    }

    // Stops and releases the driver on the thread that loaded it.
    private void close()
    {
        if (_driver != 0)
        {
            if (_started)
                call(_driver, 8);
            _started = false;
            if (_buffers is not null)
                call(_driver, 20); // disposeBuffers
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable(_driver, 2))(_driver);
            _driver = 0;
        }
        NativeMemory.Free(_buffers);
        NativeMemory.Free(_callbacks);
        _buffers = null;
        _callbacks = null;
    }

    // The size the driver takes nearest to requested (0: its preferred). Granularity -1 = powers of two, 0 = only
    // the one size (minimum = maximum).
    private static int bufferSize(int requested, int minimum, int maximum, int preferred, int granularity)
    {
        if (requested <= 0 || minimum == maximum)
            return preferred > 0 ? preferred : minimum;
        var size = Math.Clamp(requested, minimum, maximum);
        if (granularity == -1)
        {
            var power = minimum;
            while (power < size && power * 2 <= maximum)
                power *= 2;
            return power;
        }
        return granularity > 0 ? minimum + (size - minimum) / granularity * granularity : preferred;
    }

    private static int sampleBytes(int type) => type switch
    {
        Int16Lsb => 2,
        Int24Lsb => 3,
        Int32Lsb or Float32Lsb or Int32Lsb16 or Int32Lsb24 => 4,
        Float64Lsb => 8,
        _ => 0,
    };

    // One buffer half: the mix, written into each channel's half in the driver's type.
    private void fill(int half)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastCallback != 0)
        {
            var gap = now - _lastCallback;
            if (gap > (long)(1.5 * HardwareBufferFrames / Format.SampleRate * Stopwatch.Frequency))
                Interlocked.Increment(ref _lateCallbacks);
            if (gap > Interlocked.Read(ref _worstGapTicks))
                Interlocked.Exchange(ref _worstGapTicks, gap);
        }
        _lastCallback = now;
        try
        {
            var samples = _samples;
            var render = Volatile.Read(ref _render);
            if (render is null)
                Array.Clear(samples);
            else
                render(samples);
            var renderTicks = Stopwatch.GetTimestamp() - now;
            Interlocked.Add(ref _renderTicks, renderTicks);
            Interlocked.Increment(ref _renders);
            if (renderTicks > Interlocked.Read(ref _worstRenderTicks))
                Interlocked.Exchange(ref _worstRenderTicks, renderTicks);
            var frames = HardwareBufferFrames;
            for (var channel = 0; channel < Channels; channel++)
            {
                var target = (byte*)(half == 0 ? _buffers[channel].Buffer0 : _buffers[channel].Buffer1);
                for (var frame = 0; frame < frames; frame++)
                {
                    var value = Math.Clamp(samples[frame * Channels + channel], -1f, 1f);
                    switch (_sampleType)
                    {
                        case Float32Lsb: ((float*)target)[frame] = value; break;
                        case Float64Lsb: ((double*)target)[frame] = value; break;
                        case Int16Lsb: ((short*)target)[frame] = (short)(value * short.MaxValue); break;
                        case Int32Lsb: ((int*)target)[frame] = (int)(value * int.MaxValue); break;
                        case Int32Lsb16: ((int*)target)[frame] = (int)(value * short.MaxValue); break;
                        case Int32Lsb24: ((int*)target)[frame] = (int)(value * 8388607); break;
                        case Int24Lsb:
                            var sample = (int)(value * 8388607);
                            var bytes = target + frame * 3;
                            (bytes[0], bytes[1], bytes[2]) = ((byte)sample, (byte)(sample >> 8), (byte)(sample >> 16));
                            break;
                    }
                }
            }
            if (_outputReady)
                call(_driver, 23);
            // ponytail: counted after the mix, as the WASAPI output does; the clock slews over the one buffer.
            Interlocked.Add(ref _submittedFrames, frames);
        }
        catch (Exception exception)
        {
            // An exception must not unwind into the driver: the sound stops, the engine reports it.
            Failure ??= exception;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void bufferSwitch(int half, int processNow) => _current?.fill(half);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint bufferSwitchTimeInfo(nint time, int half, int processNow)
    {
        _current?.fill(half);
        return time;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void sampleRateChanged(double rate) =>
        Console.Error.WriteLine(FormattableString.Invariant($"Audio: the ASIO driver changed its sample rate to {rate} Hz; restart the game."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int message(int selector, int value, nint data, double* option)
    {
        switch (selector)
        {
            case SelectorSupported:
                return value is EngineVersion or ResetRequest or BufferSizeChange or ResyncRequest or LatenciesChanged
                    or SupportsTimeInfo ? 1 : 0;
            case EngineVersion:
                return 2;
            case ResetRequest:
            case BufferSizeChange:
                // Not from this callback: the driver's own thread may be the one asking.
                _current?._reset.Set();
                return 1;
            case ResyncRequest:
            case LatenciesChanged:
                return 1;
            default:
                return 0; // SupportsTimeInfo included: plain bufferSwitch is enough
        }
    }

    private string errorMessage()
    {
        var text = stackalloc byte[128];
        new Span<byte>(text, 128).Clear();
        call(_driver, 6, (nint)text);
        return Marshal.PtrToStringAnsi((nint)text) ?? "";
    }

    public void Dispose()
    {
        if (_stop.IsSet)
            return;
        _stop.Set();
        _thread.Join();
        Interlocked.CompareExchange(ref _current, null, this);
        _stop.Dispose();
        _opened.Dispose();
        _reset.Dispose();
    }

    private void asio(int result, string operation)
    {
        if (result != AsioOk && result != AsioSuccess)
            throw new InvalidOperationException($"ASIO failed to {operation} ({result}): {errorMessage()}");
    }

    private static void check(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException($"Failed to {operation} (0x{result:X8}).");
    }

    private static void* vtable(nint self, int slot) => (*(void***)self)[slot];

    private static int call(nint self, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)vtable(self, slot))(self);

    private static int call<T>(nint self, int slot, T a) where T : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, int>)vtable(self, slot))(self, a);

    private static int call<T, U>(nint self, int slot, T a, U b) where T : unmanaged where U : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, int>)vtable(self, slot))(self, a, b);

    private static int call<T, U, V, W>(nint self, int slot, T a, U b, V c, W d)
        where T : unmanaged where U : unmanaged where V : unmanaged where W : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T, U, V, W, int>)vtable(self, slot))(self, a, b, c, d);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public int IsInput;
        public int Channel;
        public void* Buffer0;
        public void* Buffer1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int Group;
        public int Type;
        public fixed byte Name[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public delegate* unmanaged[Cdecl]<int, int, void> BufferSwitch;
        public delegate* unmanaged[Cdecl]<double, void> SampleRateChanged;
        public delegate* unmanaged[Cdecl]<int, int, nint, double*, int> Message;
        public delegate* unmanaged[Cdecl]<nint, int, int, nint> BufferSwitchTimeInfo;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
