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
    private readonly AudioClip? _aiBattleBackground;
    private int _select; // which song select's music is the background: 0 normal, 1 Waiwai, 2 AI battle
    private AudioClip? _background => _select switch { 1 => _waiwaiBackground, 2 => _aiBattleBackground, _ => _normalBackground };
    private Operation? _current;
    private bool _disposed;
    // A song was chosen: what plays goes on and nothing replaces it, until Silence (or Song Select's music again).
    private bool _held;

    public SongPreviewController(
        AudioEngine audio,
        CatalogAssetRouter resolver,
        string? backgroundPath = null,
        string? waiwaiBackgroundPath = null,
        string? aiBattleBackgroundPath = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _normalBackground = backgroundPath is null ? null : AudioClip.Load(backgroundPath, audio.Mixer.Format);
        _waiwaiBackground = waiwaiBackgroundPath is null ? null : AudioClip.Load(waiwaiBackgroundPath, audio.Mixer.Format);
        _aiBattleBackground = aiBattleBackgroundPath is null ? null : AudioClip.Load(aiBattleBackgroundPath, audio.Mixer.Format);
    }

    /// <summary>Song Select's music: JINGLE_GENRE, JINGLE_WAIGENRE in Waiwai's, JINGLE_AIBTL_GENRE in AI battle's (traced).</summary>
    public void StartBackground(bool waiwai = false, bool aiBattle = false)
    {
        var select = aiBattle ? 2 : waiwai ? 1 : 0;
        lock (_gate)
        {
            _held = false;
            if (_select != select)
            {
                _select = select;
                stopCurrent(); // the other select's music must not keep playing
                _current = null;
            }
        }
        replace(null, TimeSpan.Zero);
    }

    public void Silence()
    {
        lock (_gate)
        {
            _held = false;
            stopCurrent();
        }
    }

    public void Hold()
    {
        lock (_gate)
            _held = true;
    }

    public void SetPreview(SongPreviewRequest? request) =>
        replace(request, request is null ? TimeSpan.Zero : HoverDebounce);

    private void replace(SongPreviewRequest? request, TimeSpan delay)
    {
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Held (a song chosen): the music under the rainbow stays as it is; only a looping preview starts again.
            if (_held && (request is null || _current is not null))
                return;
            if (request is not null && !_resolver.Resolves(request.Audio))
                throw new ArgumentException("The preview asset belongs to another catalog provider.", nameof(request));
            if (request is null
                && _current is { Request: null } background
                && (background.Handle is not { } handle || _audio.Mixer.IsPlaying(handle)))
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
        var again = false;
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
                var volume = SongLoudness.Volume(_resolver, operation.Request.Audio, _audio.Mixer.Format,
                    operation.Cancellation.Token);
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
                    operation.Handle = _audio.Mixer.PlayStream(source, AudioBus.Preview, volume);
                    _audio.Mixer.FadeBusVolume(AudioBus.Preview, 1f, PreviewFadeIn);
                    source = null;
                }
                Console.WriteLine(
                    $"Song preview started at {operation.Request.Start.TotalSeconds:0.###}s for {operation.Request.Song}.");
            }

            var playing = System.Diagnostics.Stopwatch.StartNew();
            while (operation.Handle is { } handle && _audio.Mixer.IsPlaying(handle))
                await Task.Delay(CompletionPoll, operation.Cancellation.Token).ConfigureAwait(false);
            // A preview that reaches the song's end starts again from its preview point (#18: it used to hand back to
            // Song Select's music), as long as the song stays under the cursor.
            // One that ends at once (a preview point past the audio's end) gives the music back instead of spinning.
            var ended = operation.Request is not null && finish(operation);
            again = ended && playing.Elapsed >= TimeSpan.FromSeconds(1);
            restoreBackground = ended && !again;
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

        if (restoreBackground || again)
        {
            try
            {
                replace(again ? operation.Request : null, TimeSpan.Zero);
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
