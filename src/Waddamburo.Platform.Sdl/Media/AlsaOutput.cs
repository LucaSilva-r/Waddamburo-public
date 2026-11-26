using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// Linux: plays straight to an ALSA PCM (audio_output = exclusive), for cabinets without PipeWire or PulseAudio. Blocking
/// writes keep four periods queued. SDL's ALSA backend keeps only one or two, and some DSP-backed PCMs (the Qualcomm
/// q6 HDMI output) then read ahead of the writer and play gaps without reporting an underrun.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class AlsaOutput : IPullAudioOutput
{
    private const string Library = "libasound.so.2";
    private const int Playback = 0; // SND_PCM_STREAM_PLAYBACK
    private const int FormatS16 = 2; // SND_PCM_FORMAT_S16_LE
    private const int AccessInterleaved = 3; // SND_PCM_ACCESS_RW_INTERLEAVED
    private const int Periods = 4;

    private readonly nint _pcm;
    private readonly Thread _thread;
    private Action<Span<float>>? _render;
    private volatile bool _stopping;
    private long _submittedFrames, _queuedFrames;

    // The last listing's labels by device name (the settings show them; the name is what is saved).
    private static Dictionary<string, string> s_labels = [];

    /// <summary>
    /// The playback PCMs of every sound card, as plughw names (the card for the game alone, converted to its format),
    /// from /proc/asound: no ALSA call, so a card PipeWire holds is listed too (opening it then fails, busy).
    /// </summary>
    public static string[] PlaybackDevices()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            // /proc/asound/cards: " 0 [Generic        ]: HDA-Intel - HD-Audio Generic"; pcm: "03-00: name : name : playback 1".
            var cards = File.ReadLines("/proc/asound/cards")
                .Select(static line => System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+) \[(\S+)\s*\]: .* - (.+)$"))
                .Where(static match => match.Success)
                .ToDictionary(match => int.Parse(match.Groups[1].Value, invariant),
                    static match => (Id: match.Groups[2].Value, Name: match.Groups[3].Value.Trim()));
            var labels = new Dictionary<string, string>();
            foreach (var line in File.ReadLines("/proc/asound/pcm").Where(static line => line.Contains(": playback", StringComparison.Ordinal)))
            {
                var fields = line.Split(':');
                var numbers = fields[0].Split('-');
                if (numbers.Length != 2 || !cards.TryGetValue(int.Parse(numbers[0], invariant), out var card))
                    continue;
                var device = int.Parse(numbers[1], invariant);
                labels[$"plughw:CARD={card.Id},DEV={device}"] = $"{card.Name}: {fields[1].Replace("(*)", "", StringComparison.Ordinal).Trim()} ({device})";
            }
            s_labels = labels;
            return [.. labels.Keys];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>The device's label from the last listing ("HD-Audio Generic: ALCS1200A Analog (0)"), else its name.</summary>
    public static string Label(string device) => s_labels.GetValueOrDefault(device, device);

    /// <summary>
    /// Opens the ALSA PCM <paramref name="deviceName"/> (null = "default"), with <paramref name="periodFrames"/> per period:
    /// 0 = the device minimum (ALSA rounds the request up), -1 = 256.
    /// </summary>
    public AlsaOutput(int sampleRate = SdlAudioDevice.DefaultSampleRate, int channels = SdlAudioDevice.DefaultChannels,
        int periodFrames = 0, string? deviceName = null)
    {
        Format = HardwareFormat = new SdlAudioFormat(sampleRate, channels);
        Driver = $"alsa ({deviceName ?? "default"})";
        try
        {
            var name = Encoding.UTF8.GetBytes((deviceName ?? "default") + "\0");
            nint pcm;
            fixed (byte* text = name)
                check(snd_pcm_open(&pcm, text, Playback, 0), "open");
            _pcm = pcm;
            // ponytail: unlike WASAPI, no fallback when the minimum can't keep up; the player raises the period.
            var period = periodFrames switch { 0 => 16, < 0 => 256, _ => periodFrames };
            check(snd_pcm_set_params(_pcm, FormatS16, AccessInterleaved, (uint)channels, (uint)sampleRate, 1,
                (uint)(1_000_000L * period * Periods / sampleRate)), "configure");
            nuint buffer, actualPeriod;
            check(snd_pcm_get_params(_pcm, &buffer, &actualPeriod), "read the parameters");
            HardwareBufferFrames = (int)actualPeriod;
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException($"ALSA is not installed ({exception.Message}).", exception);
        }
        catch
        {
            if (_pcm != 0)
                _ = snd_pcm_close(_pcm);
            throw;
        }
        _thread = new Thread(run) { IsBackground = true, Name = "ALSA output", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public SdlAudioFormat Format { get; }
    public SdlAudioFormat HardwareFormat { get; }
    public int HardwareBufferFrames { get; }
    public string Driver { get; }
    public ulong SubmittedFrames => (ulong)Interlocked.Read(ref _submittedFrames);

    // The device's whole delay less the one period GetPosition adds as latency.
    public ulong QueuedFrames => (ulong)Math.Max(0, Interlocked.Read(ref _queuedFrames) - HardwareBufferFrames);

    public Exception? Failure { get; private set; }

    public void Start(Action<Span<float>> render) => Volatile.Write(ref _render, render);

    private void run()
    {
        var channels = Format.Channels;
        var frames = HardwareBufferFrames;
        var mix = new float[frames * channels];
        var pcm = new short[frames * channels];
        try
        {
            while (!_stopping)
            {
                if (Volatile.Read(ref _render) is { } render)
                    render(mix);
                else
                    mix.AsSpan().Clear();
                for (var i = 0; i < mix.Length; i++)
                    pcm[i] = (short)(Math.Clamp(mix[i], -1f, 1f) * short.MaxValue);
                fixed (short* data = pcm)
                {
                    var written = 0;
                    while (written < frames && !_stopping)
                    {
                        var result = (long)snd_pcm_writei(_pcm, data + written * channels, (nuint)(frames - written));
                        if (result < 0)
                            check(snd_pcm_recover(_pcm, (int)result, 1), "recover from an underrun");
                        else
                            written += (int)result;
                    }
                }
                Interlocked.Add(ref _submittedFrames, frames);
                nint delay;
                if (snd_pcm_delay(_pcm, &delay) == 0)
                    Interlocked.Exchange(ref _queuedFrames, delay);
            }
        }
        catch (InvalidOperationException exception)
        {
            Failure = exception;
        }
    }

    private static void check(int result, string what)
    {
        if (result < 0)
            throw new InvalidOperationException($"ALSA could not {what}: {Marshal.PtrToStringUTF8(snd_strerror(result))}.");
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        _thread.Join();
        _ = snd_pcm_close(_pcm);
    }

    [DllImport(Library)] private static extern int snd_pcm_open(nint* pcm, byte* name, int stream, int mode);
    [DllImport(Library)] private static extern int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate,
        int softResample, uint latencyMicroseconds);
    [DllImport(Library)] private static extern int snd_pcm_get_params(nint pcm, nuint* bufferSize, nuint* periodSize);
    [DllImport(Library)] private static extern nint snd_pcm_writei(nint pcm, void* buffer, nuint frames);
    [DllImport(Library)] private static extern int snd_pcm_recover(nint pcm, int error, int silent);
    [DllImport(Library)] private static extern int snd_pcm_delay(nint pcm, nint* delay);
    [DllImport(Library)] private static extern int snd_pcm_close(nint pcm);
    [DllImport(Library)] private static extern nint snd_strerror(int error);
}
