using Waddamburo.App.Gameplay;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>Owns the single Song Select music slot selected by the authored movie.</summary>
internal sealed class SongPreviewController : ISongPreviewController, IDisposable
{
    private static readonly TimeSpan HoverDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OutgoingFade = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PreviewFadeIn = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BackgroundFadeIn = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CompletionPoll = TimeSpan.FromMilliseconds(20);

    private readonly object _gate = new();
    private readonly AudioEngine _audio;
    private readonly CatalogAssetRouter _resolver;
    private readonly AudioClip? _normalBackground;
    private readonly AudioClip? _waiwaiBackground;
    private bool _waiwai; // which song select's music is the background
    private AudioClip? _background => _waiwai ? _waiwaiBackground : _normalBackground;
    private Operation? _current;
    private bool _disposed;

    public SongPreviewController(
        AudioEngine audio,
        CatalogAssetRouter resolver,
        string? backgroundPath = null,
        string? waiwaiBackgroundPath = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _normalBackground = backgroundPath is null ? null : AudioClip.Load(backgroundPath, audio.Mixer.Format);
        _waiwaiBackground = waiwaiBackgroundPath is null ? null : AudioClip.Load(waiwaiBackgroundPath, audio.Mixer.Format);
    }

    /// <summary>Song Select's music: JINGLE_GENRE, or JINGLE_WAIGENRE in Waiwai's song select.</summary>
    public void StartBackground(bool waiwai = false)
    {
        lock (_gate)
        {
            if (_waiwai != waiwai)
            {
                _waiwai = waiwai;
                stopCurrent(); // the other select's music must not keep playing
                _current = null;
            }
        }
        replace(null, TimeSpan.Zero);
    }

    public void SetPreview(SongPreviewRequest? request) =>
        replace(request, request is null ? TimeSpan.Zero : HoverDebounce);

    private void replace(SongPreviewRequest? request, TimeSpan delay)
    {
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (request is not null && !_resolver.Resolves(request.Audio))
                throw new ArgumentException("The preview asset belongs to another catalog provider.", nameof(request));
            if (request is null
                && _current is { Request: null } background
                && (background.Delay == TimeSpan.Zero
                    || background.Handle is { } handle && _audio.Mixer.IsPlaying(handle)))
            {
                return;
            }

            stopCurrent();
            operation = new Operation(request, delay);
            _current = operation;
            operation.Task = Task.Run(() => startAsync(operation));
        }
    }

    private async Task startAsync(Operation operation)
    {
        BufferedAudioSource? source = null;
        var restoreBackground = false;
        try
        {
            if (operation.Delay > TimeSpan.Zero)
                await Task.Delay(operation.Delay, operation.Cancellation.Token).ConfigureAwait(false);

            if (operation.Request is null)
            {
                if (_background is null)
                {
                    finish(operation);
                    return;
                }

                lock (_gate)
                {
                    if (!isCurrent(operation))
                        return;
                    _audio.Mixer.SetBusVolume(AudioBus.Bgm, 0f);
                    operation.Handle = _audio.Mixer.Play(_background, AudioBus.Bgm, loop: true);
                    _audio.Mixer.FadeBusVolume(AudioBus.Bgm, 1f, BackgroundFadeIn);
                }
                Console.WriteLine("Song Select background restarted.");
            }
            else
            {
                var input = await _resolver
                    .OpenReadAsync(operation.Request.Audio, operation.Cancellation.Token)
                    .ConfigureAwait(false);
                try
                {
                    source = new BufferedAudioSource(input, _audio.Mixer.Format, operation.Request.Start);
                }
                catch
                {
                    input.Dispose();
                    throw;
                }

                lock (_gate)
                {
                    if (!isCurrent(operation))
                        return;
                    _audio.Mixer.SetBusVolume(AudioBus.Preview, 0f);
                    operation.Handle = _audio.Mixer.PlayStream(source, AudioBus.Preview);
                    _audio.Mixer.FadeBusVolume(AudioBus.Preview, 1f, PreviewFadeIn);
                    source = null;
                }
                Console.WriteLine(
                    $"Song preview started at {operation.Request.Start.TotalSeconds:0.###}s for {operation.Request.Song}.");
            }

            while (operation.Handle is { } handle && _audio.Mixer.IsPlaying(handle))
                await Task.Delay(CompletionPoll, operation.Cancellation.Token).ConfigureAwait(false);
            restoreBackground = operation.Request is not null && finish(operation);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var description = operation.Request is null
                ? "Song Select background"
                : $"Song preview {operation.Request.Song}";
            Console.Error.WriteLine($"{description} is unavailable: {exception.Message}");
            var mayContinue = finish(operation);
            restoreBackground = operation.Request is not null && mayContinue;
        }
        finally
        {
            source?.Dispose();
        }

        if (restoreBackground)
        {
            try
            {
                replace(null, TimeSpan.Zero);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private bool isCurrent(Operation operation) =>
        !_disposed && _current == operation && !operation.Cancellation.IsCancellationRequested;

    private bool finish(Operation operation)
    {
        lock (_gate)
        {
            if (_current != operation)
                return false;
            _current = null;
            operation.DisposeCancellation();
            return !_disposed;
        }
    }

    private void stopCurrent()
    {
        var current = _current;
        _current = null;
        if (current is null)
            return;
        current.Cancellation.Cancel();
        if (current.Handle is { } handle)
            _audio.Mixer.Stop(handle, OutgoingFade);
        _ = current.Task.ContinueWith(
            _ => current.DisposeCancellation(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            stopCurrent();
        }
    }

    private sealed class Operation(SongPreviewRequest? request, TimeSpan delay)
    {
        public SongPreviewRequest? Request { get; } = request;
        public TimeSpan Delay { get; } = delay;
        public CancellationTokenSource Cancellation { get; } = new();
        public AudioPlaybackHandle? Handle { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
        private int _cancellationDisposed;

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
                Cancellation.Dispose();
        }
    }
}
