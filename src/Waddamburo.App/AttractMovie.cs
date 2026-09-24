using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Waddamburo.Formats.Audio;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

/// <summary>
/// One attract CM (data/movie/attract_cm_###.pam) played into attract/movie.lm's video fill.
/// Frames decode on a worker thread and are shown by the CM's own audio clock.
/// </summary>
internal sealed partial class AttractMovie : IDisposable
{
    public static readonly LumenNativeSurfaceKey Surface = new("attract-movie");
    private const int QueuedFrames = 4;

    private readonly SdlApplication _application;
    private readonly AudioEngine? _audio;
    private readonly NativeVideoDecoder _decoder;
    private readonly BlockingCollection<(byte[] Pixels, TimeSpan Time)> _frames = new(QueuedFrames);
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private readonly AudioStreamTransport? _transport;
    private readonly TimeSpan _lead;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _duration;
    private (byte[] Pixels, TimeSpan Time)? _next;
    private volatile bool _decoded;

    public AttractMovie(SdlApplication application, AudioEngine? audio, string path)
    {
        _application = application;
        _audio = audio;
        _decoder = new NativeVideoDecoder(path);
        _duration = _decoder.Duration ?? TimeSpan.Zero;
        Texture = application.UploadRgba8(_decoder.Width, _decoder.Height, new byte[_decoder.Width * _decoder.Height * 4]);
        _worker = new Thread(decode) { IsBackground = true, Name = "Attract movie decoder" };
        _worker.Start();
        if (audio is not null)
        {
            PamfAudio? track;
            using (var file = File.OpenRead(path))
                track = PamfAudio.Extract(file);
            if (track is not null)
            {
                _lead = track.Lead;
                var source = new BufferedAudioSource(new MemoryStream(track.Riff), audio.Mixer.Format);
                source.Ready.GetAwaiter().GetResult();
                _transport = audio.PlayTransport(new ScheduledAudioSource(source, TimeSpan.Zero, _duration + _lead));
            }
        }
        Console.WriteLine($"Attract movie {Path.GetFileName(path)}: {_decoder.Width}x{_decoder.Height}, {_duration.TotalSeconds:0.##} s.");
    }

    public RenderTextureId Texture { get; }

    public bool Finished { get; private set; }

    public RenderTextureId? Resolve(LumenNativeSurfaceKey surface) => surface == Surface ? Texture : null;

    /// <summary>Shows the newest frame due by the movie clock.</summary>
    public void Advance()
    {
        var now = (_transport is not null ? _audio!.GetPosition(_transport) : _clock.Elapsed) - _lead;
        byte[]? shown = null;
        while (true)
        {
            if (_next is null)
            {
                if (!_frames.TryTake(out var frame))
                    break;
                _next = frame;
            }
            if (_next.Value.Time > now)
                break;
            if (shown is not null)
                _spare.Add(shown);
            shown = _next.Value.Pixels;
            _next = null;
        }
        if (shown is not null)
        {
            _application.UpdateRgba8(Texture, _decoder.Width, _decoder.Height, shown);
            _spare.Add(shown);
        }
        Finished = _decoded && _next is null && _frames.Count == 0 && now >= _duration;
    }

    private void decode()
    {
        try
        {
            var size = (int)(_decoder.Width * _decoder.Height * 4);
            while (!_stop.IsCancellationRequested)
            {
                var pixels = _spare.TryTake(out var reused) ? reused : new byte[size];
                if (!_decoder.TryReadFrame(pixels, out var time))
                    break;
                _frames.Add((pixels, time), _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            Console.Error.WriteLine($"Attract movie decoding stopped: {exception.Message}");
        }
        _decoded = true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join();
        if (_transport is not null)
            _audio!.Mixer.Stop(_transport.Handle, TimeSpan.FromMilliseconds(20));
        _decoder.Dispose();
        _application.ReleaseTexture(Texture);
        _frames.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// The CMs the game can pick: attract_cm_###.pam, nonzero ids in order, or the 000 fallback
    /// when there are none. ponytail: the cabinet only plays ids its server permitted
    /// (updates/.../moviepermission.bin); with no server every CM is permitted, as the local
    /// server's default does.
    /// </summary>
    public static string[] Discover(string movieDirectory)
    {
        if (!Directory.Exists(movieDirectory))
            return [];
        var movies = Directory.EnumerateFiles(movieDirectory, "attract_cm_*.pam")
            .Select(path => (path, match: CmName().Match(Path.GetFileName(path))))
            .Where(entry => entry.match.Success)
            .OrderBy(entry => int.Parse(entry.match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();
        var permitted = movies.Where(entry => entry.match.Groups[1].Value != "000").Select(entry => entry.path).ToArray();
        return permitted.Length != 0 ? permitted : movies.Select(entry => entry.path).ToArray();
    }

    [GeneratedRegex("^attract_cm_([0-9]{3})\\.pam$", RegexOptions.IgnoreCase)]
    private static partial Regex CmName();
}
