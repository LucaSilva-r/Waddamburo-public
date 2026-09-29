using System.Collections.Concurrent;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Live upscaling while the game runs: every uploaded movie texture is queued, newest first, and
/// upscaled one at a time on a few CPU threads (<see cref="UpscaleTool.LiveThreads"/>), holding during
/// gameplay. Cached results arrive at once; each replaces its texture in place (UVs are normalised).
/// A batch run (--upscale-textures) fills the cache ahead with more threads.
/// </summary>
internal sealed class TextureUpscaler : IDisposable
{
    private readonly record struct Job(RenderTextureId Texture, UpscaleSource Source);
    private readonly record struct Result(RenderTextureId Texture, UpscaledTexture Upscaled);

    // Newest first: the scene on screen before the ones left behind (those still fill the cache).
    // ponytail: queued jobs keep their pixels alive (a few hundred MB after many scenes); re-decode
    // from the archive if memory matters.
    private readonly ConcurrentStack<Job> _jobs = new();
    private readonly ConcurrentStack<Job> _misses = new();
    private readonly ConcurrentQueue<Result> _done = new();
    private readonly SemaphoreSlim _jobWake = new(0);
    private readonly SemaphoreSlim _missWake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly UpscaleTool _tool;
    private readonly Thread _loader;
    private readonly Thread _upscaler;
    private volatile bool _paused;

    public TextureUpscaler(UpscaleTool tool)
    {
        _tool = tool;
        // Two threads: cached results load at once, even during gameplay, while only the
        // upscaling of misses holds (an upscale caught mid-way must not keep the cache waiting).
        _loader = new Thread(load) { IsBackground = true, Name = "Upscaled texture loader", Priority = ThreadPriority.BelowNormal };
        _upscaler = new Thread(upscale) { IsBackground = true, Name = "Texture upscaler", Priority = ThreadPriority.BelowNormal };
        _loader.Start();
        _upscaler.Start();
    }

    /// <summary>Holds new upscaling (gameplay: the CPU belongs to the song); cached textures still load.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value) _missWake.Release();
        }
    }

    /// <summary>Queues a freshly uploaded texture; <paramref name="rgba"/> must not change afterwards.</summary>
    public void Enqueue(RenderTextureId texture, uint width, uint height, byte[] rgba)
    {
        // Tiny textures (dots, flat fills) gain nothing; upscaled ones arrived that way from the cache.
        if (width * height < 256 || _tool.IsUpscaled(rgba))
            return;
        _jobs.Push(new Job(texture, new UpscaleSource(width, height, rgba)));
        _jobWake.Release();
    }

    /// <summary>
    /// Swaps finished textures in (main thread, once per frame), up to about 16 MB of BC7 a frame:
    /// a few big textures in one frame stall it.
    /// </summary>
    public void Apply(SdlApplication application)
    {
        const long Budget = 16 << 20;
        long bytes = 0;
        while (bytes < Budget && _done.TryDequeue(out var result))
        {
            var (texture, upscaled) = result;
            if (application.SupportsBc7)
                application.TryReplaceBc7(texture, upscaled.Width, upscaled.Height, upscaled.Bc7.Span);
            else
                application.TryReplaceRgba8(texture, upscaled.Width, upscaled.Height,
                    Bc7.Decode(upscaled.Bc7.Span, (int)upscaled.Width, (int)upscaled.Height));
            bytes += upscaled.Bc7.Length;
        }
    }

    // Cache hits go straight to the swap queue; misses go to the upscaler.
    private void load()
    {
        while (wait(_jobWake))
            while (!_stop.IsCancellationRequested && _jobs.TryPop(out var job))
                guard(() =>
                {
                    if (_tool.Load(UpscaleTool.Key(job.Source)) is { } upscaled)
                        _done.Enqueue(new Result(job.Texture, upscaled));
                    else
                    {
                        _misses.Push(job);
                        _missWake.Release();
                    }
                });
    }

    private void upscale()
    {
        while (wait(_missWake))
            while (!_paused && !_stop.IsCancellationRequested && _misses.TryPop(out var job))
                guard(() =>
                {
                    var key = UpscaleTool.Key(job.Source);
                    if (!_tool.IsCached(key))
                        _tool.Upscale(job.Source, UpscaleTool.LiveThreads, () => _paused && !_stop.IsCancellationRequested);
                    if (_tool.Load(key) is { } upscaled)
                        _done.Enqueue(new Result(job.Texture, upscaled));
                });
    }

    private bool wait(SemaphoreSlim wake)
    {
        try
        {
            wake.Wait(_stop.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Background threads: a failing texture is reported and keeps its original.
    private static void guard(Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Texture upscale failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loader.Join(TimeSpan.FromSeconds(2));
        _upscaler.Join(TimeSpan.FromSeconds(2));
        _tool.Dispose();
    }
}
