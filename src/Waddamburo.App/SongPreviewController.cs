using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

/// <summary>Debounces catalog previews and owns their decoder/mixer lifetime.</summary>
internal sealed class SongPreviewController : ISongPreviewController, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StopFade = TimeSpan.FromMilliseconds(30);

    private readonly object _gate = new();
    private readonly AudioEngine _audio;
    private readonly ICatalogAssetResolver _resolver;
    private Operation? _current;
    private bool _disposed;

    public SongPreviewController(AudioEngine audio, ICatalogAssetResolver resolver)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public void SetPreview(SongPreviewRequest? request)
    {
        Operation? operation = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stopCurrent();
            if (request is not null)
            {
                if (request.Audio.Provider != _resolver.Id)
                    throw new ArgumentException("The preview asset belongs to another catalog provider.", nameof(request));
                operation = new Operation(request);
                _current = operation;
            }
        }
        if (operation is not null)
        {
            lock (_gate)
            {
                if (_current == operation)
                    operation.Task = Task.Run(() => startAsync(operation));
            }
        }
    }

    private async Task startAsync(Operation operation)
    {
        BufferedAudioSource? source = null;
        try
        {
            await Task.Delay(Debounce, operation.Cancellation.Token).ConfigureAwait(false);
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
                if (_disposed || _current != operation || operation.Cancellation.IsCancellationRequested)
                    return;
                operation.Handle = _audio.Mixer.PlayStream(source, AudioBus.Preview);
                source = null;
            }
            Console.WriteLine(
                $"Song preview started at {operation.Request.Start.TotalSeconds:0.###}s for {operation.Request.Song}.");
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Song preview {operation.Request.Song} is unavailable: {exception.Message}");
        }
        finally
        {
            source?.Dispose();
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
            _audio.Mixer.Stop(handle, StopFade);
        _ = current.Task.ContinueWith(
            _ => current.Cancellation.Dispose(),
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

    private sealed class Operation(SongPreviewRequest request)
    {
        public SongPreviewRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public AudioPlaybackHandle? Handle { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
