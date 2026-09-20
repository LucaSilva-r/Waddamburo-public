namespace Waddamburo.Game.Flow;

public enum GameFlowState
{
    Stopped,
    Loading,
    Active,
    TransitionPending,
    Failed,
}

/// <summary>
/// Owns scene lifecycle independently of any authored movie. Lumen requests a
/// transition; the composition root resolves that request to a product scene and
/// explicitly completes its load before activation.
/// </summary>
public sealed class GameFlowSession : ISceneTransitionSink
{
    public GameFlowState State { get; private set; } = GameFlowState.Stopped;

    public SceneId? CurrentScene { get; private set; }

    public SceneId? LoadingScene { get; private set; }

    public LumenSceneRequest? PendingTransition { get; private set; }

    public Exception? Failure { get; private set; }

    public void Start(SceneId initialScene)
    {
        ArgumentNullException.ThrowIfNull(initialScene);
        requireState(GameFlowState.Stopped);
        beginLoad(initialScene);
    }

    public void ActivateLoadedScene()
    {
        requireState(GameFlowState.Loading);
        CurrentScene = LoadingScene ?? throw new InvalidOperationException("No scene is being loaded.");
        LoadingScene = null;
        Failure = null;
        State = GameFlowState.Active;
    }

    public bool TryRequestTransition(LumenSceneRequest request)
    {
        if (State != GameFlowState.Active)
            return false;

        PendingTransition = request;
        State = GameFlowState.TransitionPending;
        return true;
    }

    public void LoadTransitionTarget(SceneId scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        requireState(GameFlowState.TransitionPending);
        PendingTransition = null;
        beginLoad(scene);
    }

    public void RestartCurrentScene()
    {
        requireState(GameFlowState.Active);
        beginLoad(CurrentScene ?? throw new InvalidOperationException("No active scene exists."));
    }

    public void CancelLoading()
    {
        requireState(GameFlowState.Loading);
        LoadingScene = null;
        Failure = null;
        State = CurrentScene is null ? GameFlowState.Stopped : GameFlowState.Active;
    }

    public void Fail(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        LoadingScene = null;
        PendingTransition = null;
        Failure = failure;
        State = GameFlowState.Failed;
    }

    public void Stop()
    {
        CurrentScene = null;
        LoadingScene = null;
        PendingTransition = null;
        Failure = null;
        State = GameFlowState.Stopped;
    }

    private void beginLoad(SceneId scene)
    {
        LoadingScene = scene;
        PendingTransition = null;
        Failure = null;
        State = GameFlowState.Loading;
    }

    private void requireState(GameFlowState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Game flow is {State}; expected {expected}.");
    }
}
