using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

/// <summary>Finds the note-ons in a raw MIDI byte stream (running status, interleaved real-time bytes).</summary>
public struct MidiNoteParser
{
    private byte _status;
    private byte _note;
    private bool _hasNote;

    /// <summary>Takes the next byte; returns the note when it completes a note-on with a velocity above 0.</summary>
    public int? Feed(byte value)
    {
        if (value >= 0xF8)
            return null; // real-time: allowed anywhere, even inside a message
        if (value >= 0x80)
        {
            _status = value;
            _hasNote = false;
            return null;
        }
        // Only the two-byte channel messages are followed (program change, channel pressure and system data are not).
        if ((_status & 0xF0) is not (0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0))
            return null;
        if (!_hasNote)
        {
            _note = value;
            _hasNote = true;
            return null;
        }
        _hasNote = false;
        return (_status & 0xF0) == 0x90 && value > 0 ? _note : null;
    }
}

/// <summary>
/// Note-ons from every MIDI input device (a drum in MIDI mode, an electronic kit), stamped with SDL's
/// clock as they arrive on background threads; devices plugged in later are picked up.
/// </summary>
// ponytail: Linux reads the raw devices (/dev/snd/midiC*D*) and Windows uses winmm, to need no MIDI
// library; macOS has no input. A device another program holds open may stay silent here.
internal sealed unsafe class MidiInput : IDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);
    // Static: winmm's callback is a static function.
    private static readonly ConcurrentQueue<(int Note, ulong Timestamp)> Notes = new();

    private readonly CancellationTokenSource _stop = new();

    public MidiInput()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return;
        new Thread(guarded(OperatingSystem.IsLinux() ? scanLinux : scanWindows)) { IsBackground = true, Name = "MIDI scan" }.Start();
    }

    // An exception on a background thread would end the game: MIDI stops instead, and says why.
    private static ThreadStart guarded(Action body) => () =>
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Warning MIDI_INPUT: MIDI input stopped: {exception.Message}");
        }
    };

    /// <summary>The next note-on received, with its arrival time in SDL nanoseconds.</summary>
    public static bool TryTake(out (int Note, ulong Timestamp) note) => Notes.TryDequeue(out note);

    public void Dispose() => _stop.Cancel();

    private void scanLinux()
    {
        var open = new ConcurrentDictionary<string, bool>();
        do
        {
            string[] devices;
            try
            {
                devices = Directory.GetFiles("/dev/snd", "midiC*D*");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                devices = [];
            }
            foreach (var device in devices)
                if (open.TryAdd(device, true))
                    new Thread(guarded(() =>
                    {
                        try
                        {
                            readLinux(device);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            // Unplugged, or not ours to open: tried again at the next scan.
                        }
                        finally
                        {
                            open.TryRemove(device, out _);
                        }
                    })) { IsBackground = true, Name = "MIDI " + device }.Start();
        }
        while (!_stop.Token.WaitHandle.WaitOne(RescanInterval));
    }

    // Blocks in read until the device sends or goes away (the thread is a background one).
    private void readLinux(string device)
    {
        using var stream = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
        Console.WriteLine($"MIDI input: {device}");
        var parser = new MidiNoteParser();
        Span<byte> buffer = stackalloc byte[64];
        while (!_stop.IsCancellationRequested && stream.Read(buffer) is var count and > 0)
        {
            var now = SDL_GetTicksNS();
            foreach (var value in buffer[..count])
                if (parser.Feed(value) is { } note)
                    Notes.Enqueue((note, now));
        }
    }

    private const uint CallbackFunction = 0x30000, MimData = 0x3C3;

    // winmm has no hot-plug notice: every device is reopened when their number changes.
    private void scanWindows()
    {
        var handles = new List<IntPtr>();
        var known = uint.MaxValue;
        do
        {
            var count = midiInGetNumDevs();
            if (count == known)
                continue;
            known = count;
            foreach (var handle in handles)
            {
                _ = midiInStop(handle);
                _ = midiInClose(handle);
            }
            handles.Clear();
            for (uint device = 0; device < count; device++)
                if (midiInOpen(out var handle, device, &received, 0, CallbackFunction) == 0)
                {
                    _ = midiInStart(handle);
                    handles.Add(handle);
                    Console.WriteLine($"MIDI input: device {device}");
                }
        }
        while (!_stop.Token.WaitHandle.WaitOne(RescanInterval));
    }

    // Called by winmm on its own thread; a short message packs status, note and velocity into one word.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void received(IntPtr handle, uint message, nuint instance, nuint data, nuint time)
    {
        if (message == MimData && (data & 0xF0) == 0x90 && ((data >> 16) & 0xFF) > 0)
            Notes.Enqueue(((int)((data >> 8) & 0x7F), SDL_GetTicksNS()));
    }

    [DllImport("winmm.dll")]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint midiInOpen(out IntPtr handle, uint device,
        delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nuint, nuint, void> callback, nuint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint midiInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint midiInStop(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint midiInClose(IntPtr handle);
}
