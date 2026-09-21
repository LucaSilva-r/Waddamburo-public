using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Scenes;

public interface IGameSceneInstance : IAsyncDisposable
{
    SceneId Id { get; }
}

public interface IGameSceneLoader
{
    ValueTask<IGameSceneInstance> LoadAsync(SceneDefinition definition, CancellationToken cancellationToken);
}

/// <summary>Resolves, loads, and atomically swaps product-owned scene instances.</summary>
public sealed class GameFlowCoordinator : IAsyncDisposable
{
    private readonly SceneCatalog _catalog;
    private readonly IGameSceneLoader _loader;
    private int _operationActive;

    public GameFlowCoordinator(
        SceneCatalog catalog,
        IGameSceneLoader loader,
        GameFlowSession? flow = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        Flow = flow ?? new GameFlowSession();
    }

    public GameFlowSession Flow { get; }

    public IGameSceneInstance? ActiveScene { get; private set; }

    public async ValueTask StartAsync(SceneId initialScene, CancellationToken cancellationToken = default)
    {
        enterOperation();
        try
        {
            Flow.Start(initialScene);
            await loadAndActivateAsync(_catalog.GetDefinition(initialScene), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaveOperation();
        }
    }

    public async ValueTask ApplyPendingTransitionAsync(CancellationToken cancellationToken = default)
    {
        enterOperation();
        try
        {
            if (Flow.State != GameFlowState.TransitionPending || Flow.PendingTransition is not { } request)
                throw new InvalidOperationException("Game flow has no pending scene transition.");
            var source = Flow.CurrentScene ?? throw new InvalidOperationException("Game flow has no current scene.");
            if (!_catalog.TryResolve(source, request, out var definition))
            {
                var failure = new KeyNotFoundException($"Scene '{source}' has no route for Lumen request {request}.");
                Flow.Fail(failure);
                throw failure;
            }

            Flow.LoadTransitionTarget(definition!.Id);
            await loadAndActivateAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaveOperation();
        }
    }

    public async ValueTask RestartAsync(CancellationToken cancellationToken = default)
    {
        enterOperation();
        try
        {
            Flow.RestartCurrentScene();
            var definition = _catalog.GetDefinition(Flow.LoadingScene!);
            await loadAndActivateAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaveOperation();
        }
    }

    /// <summary>Loads a product-selected scene without manufacturing an authored Lumen request.</summary>
    public async ValueTask TransitionToAsync(SceneId target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        enterOperation();
        try
        {
            var definition = _catalog.GetDefinition(target);
            Flow.LoadScene(target);
            await loadAndActivateAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaveOperation();
        }
    }

    public async ValueTask StopAsync()
    {
        enterOperation();
        try
        {
            var previous = ActiveScene;
            ActiveScene = null;
            Flow.Stop();
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            leaveOperation();
        }
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async ValueTask loadAndActivateAsync(
        SceneDefinition definition,
        CancellationToken cancellationToken)
    {
        IGameSceneInstance? loaded = null;
        try
        {
            loaded = await _loader.LoadAsync(definition, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Scene loader returned null for '{definition.Id}'.");
            if (loaded.Id != definition.Id)
                throw new InvalidOperationException($"Scene loader returned '{loaded.Id}' while loading '{definition.Id}'.");

            var previous = ActiveScene;
            Flow.ActivateLoadedScene();
            ActiveScene = loaded;
            loaded = null;
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (loaded is not null)
                await loaded.DisposeAsync().ConfigureAwait(false);
            Flow.CancelLoading();
            throw;
        }
        catch (Exception exception)
        {
            if (loaded is not null)
                await loaded.DisposeAsync().ConfigureAwait(false);
            Flow.Fail(exception);
            throw;
        }
    }

    private void enterOperation()
    {
        if (Interlocked.Exchange(ref _operationActive, 1) != 0)
            throw new InvalidOperationException("Another game-flow operation is already active.");
    }

    private void leaveOperation() => Volatile.Write(ref _operationActive, 0);
}
