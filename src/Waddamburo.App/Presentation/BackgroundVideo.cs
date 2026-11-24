using System.Collections.Concurrent;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A chart's background video (ChartVideo), shown by the song's own clock: frames decode on a worker a few ahead, and
/// a jump in time (a restart, practice, a replay's seek) seeks the decoder there.
/// </summary>
internal sealed class BackgroundVideo : IDisposable
{
    public static readonly LumenNativeSurfaceKey Surface = new("background-video");
    private const int QueuedFrames = 4;
    // A jump further than this (or any step back) seeks instead of decoding up to the new time.
    private static readonly TimeSpan SeekAhead = TimeSpan.FromSeconds(1.5);
    // A frame this far behind the song's time is dropped by the worker.
    private static readonly long LateTicks = TimeSpan.FromMilliseconds(50).Ticks;

    private readonly SdlApplication _application;
    private readonly NativeVideoDecoder _decoder;
    private readonly BlockingCollection<(byte[] Pixels, TimeSpan Time, int Generation)> _frames = new(QueuedFrames);
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private (byte[] Pixels, TimeSpan Time, int Generation)? _next;
    private volatile int _generation;
    private long _seekTicks = -1; // the worker seeks here before its next frame (-1: none)
    private TimeSpan? _shown;
    // The time asked for last (a jump from it is a seek) and the time the worker should be decoding towards.
    private TimeSpan? _asked;
    private long _targetTicks;

    private readonly VideoMask _mask;

    private BackgroundVideo(SdlApplication application, NativeVideoDecoder decoder, Waddamburo.Catalog.ChartVideo video)
    {
        _application = application;
        _decoder = decoder;
        Path = video.Path;
        Additive = video.Additive;
        HidesDancers = video.Additive || video.KeyColor is not null || video.LumaCurve is not null;
        Area = video.Area;
        _mask = new VideoMask((int)decoder.Width, (int)decoder.Height, video.KeyColor, video.KeyTolerance);
        LumaCurve = video.LumaCurve ?? 0;
        Texture = application.UploadRgba8(decoder.Width, decoder.Height, new byte[decoder.Width * decoder.Height * 4]);
        _worker = new Thread(decode) { IsBackground = true, Name = "Background video decoder" };
        _worker.Start();
    }

    /// <summary>Opens the video (null: it cannot be decoded; the song plays without it).</summary>
    public static BackgroundVideo? Open(SdlApplication application, Waddamburo.Catalog.ChartVideo video)
    {
        var path = video.Path;
        try
        {
            var decoder = new NativeVideoDecoder(path);
            Console.WriteLine($"Background video {System.IO.Path.GetFileName(path)}: {decoder.Width}x{decoder.Height}"
                + $"{(video.Additive ? ", added" : "")}{(video.KeyColor is { } key ? $", key #{key:X6}" : "")}"
                + $"{(video.LumaCurve is { } curve ? $", luma key {curve}" : "")}.");
            return new BackgroundVideo(application, decoder, video);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Background video {System.IO.Path.GetFileName(path)} not played: {exception.Message}");
            return null;
        }
    }

    public string Path { get; }

    /// <summary>Added onto the backdrops rather than drawn over them (ChartVideo.Additive).</summary>
    public bool Additive { get; }

    /// <summary>The video is seen through (added or keyed): it takes the dancers' place, who would show through it.</summary>
    public bool HidesDancers { get; }

    /// <summary>The luma key's curve, applied by the renderer (fs_luma); 0: none.</summary>
    public float LumaCurve { get; }

    /// <summary>The chart's own box for it (ChartVideo.Area), or null.</summary>
    public (float X, float Y, float Width, float Height)? Area { get; }

    public RenderTextureId Texture { get; }

    public float Width => _decoder.Width;

    public float Height => _decoder.Height;

    /// <summary>A frame is on the texture (before the video starts there is none to show).</summary>
    public bool Visible => _shown is not null;

    public RenderTextureId? Resolve(LumenNativeSurfaceKey surface) => surface == Surface && Visible ? Texture : null;

    /// <summary>Shows the newest frame due at <paramref name="time"/> (video time; negative: not started yet).</summary>
    public void Show(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            if (_shown is not null)
                seek(TimeSpan.Zero);
            (_shown, _asked) = (null, null);
            Interlocked.Exchange(ref _targetTicks, 0);
            return;
        }
        // A jump of the song's clock (a restart, practice, a replay's seek) seeks; a decoder running late does not: it
        // drops the late frames and catches up (seeking there would only make it later).
        if (_asked is { } asked ? time < asked - TimeSpan.FromMilliseconds(100) || time > asked + SeekAhead
            : time > SeekAhead)
            seek(time);
        _asked = time;
        Interlocked.Exchange(ref _targetTicks, time.Ticks);
        byte[]? newest = null;
        TimeSpan newestTime = default;
        while (true)
        {
            if (_next is null)
            {
                if (!_frames.TryTake(out var frame))
                    break;
                _next = frame;
            }
            var next = _next.Value;
            if (next.Generation != _generation)
            {
                _spare.Add(next.Pixels);
                _next = null;
                continue;
            }
            if (next.Time > time)
                break;
            if (newest is not null)
                _spare.Add(newest);
            (newest, newestTime) = (next.Pixels, next.Time);
            _next = null;
        }
        if (newest is null)
            return;
        _application.UpdateRgba8(Texture, _decoder.Width, _decoder.Height, newest);
        _spare.Add(newest);
        _shown = newestTime;
    }

    private void seek(TimeSpan time)
    {
        _generation++;
        Interlocked.Exchange(ref _seekTicks, time.Ticks);
        _shown = time;
    }

    private void decode()
    {
        try
        {
            var size = (int)(_decoder.Width * _decoder.Height * 4);
            var ended = false;
            while (!_stop.IsCancellationRequested)
            {
                // The generation is read after the seek: a seek raises it before it posts its time.
                if (Interlocked.Exchange(ref _seekTicks, -1) is var ticks and >= 0)
                {
                    _decoder.Seek(TimeSpan.FromTicks(ticks));
                    ended = false;
                }
                var generation = _generation;
                if (ended)
                {
                    // The last frame stays on screen; a seek back starts it again.
                    Thread.Sleep(20);
                    continue;
                }
                var pixels = _spare.TryTake(out var reused) ? reused : new byte[size];
                if (!_decoder.TryReadFrame(pixels, out var time))
                {
                    ended = true;
                    _spare.Add(pixels);
                    continue;
                }
                // Late already (the decoder fell behind): not masked nor queued, the next one may be on time.
                if (time.Ticks < Interlocked.Read(ref _targetTicks) - LateTicks)
                {
                    _spare.Add(pixels);
                    continue;
                }
                _mask.Apply(pixels);
                // Wait for room, but give up on the frame when a seek asks for another place.
                while (!_frames.TryAdd((pixels, time, generation), 20, _stop.Token))
                    if (Interlocked.Read(ref _seekTicks) >= 0)
                        break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Background video decoding stopped: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join();
        _decoder.Dispose();
        _application.ReleaseTexture(Texture);
        _frames.Dispose();
        _stop.Dispose();
    }
}
