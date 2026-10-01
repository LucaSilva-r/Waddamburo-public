using Waddamburo.App.Scenes;

namespace Waddamburo.App.Flow;

/// <summary>
/// Reloads Song Select behind a quick plain rainbow (Don-chan): it covers the screen, the change runs
/// (another library, grouping or order, search results), Song Select reloads with its music playing on,
/// and the rainbow opens. The game takes no input meanwhile.
/// </summary>
internal sealed class SongSelectReload(GameShell shell)
{
    // ponytail: three intermission ticks per game tick; tune if it feels rushed or slow.
    private const int Speed = 3;

    private enum Stage { None, Covering, Revealing }
    private Stage _stage;
    private Action? _change;

    public bool Busy => _stage != Stage.None;

    /// <summary>Covers the screen, then runs <paramref name="change"/> and reloads Song Select.</summary>
    public void Start(Action change)
    {
        if (Busy)
            return;
        _change = change;
        // The movies decode and upload on worker threads while the rainbow covers (the reload's cost).
        _scene = FlowScenes.SongSelectScene(
            shell.JoinedSides.Count > 0 ? [.. shell.JoinedSides] : [shell.Hosts.PlayerSide], shell.Hosts.Waiwai);
        shell.Prefetch(_scene);
        shell.Overlay.Show(FlowScenes.Rainbow).GotoLabel(RainbowTransitionComposition.PlainCoverLabel, play: true);
        shell.Overlay.Speed = Speed;
        _stage = Stage.Covering;
        _startTick = shell.Tick;
    }

    private int _startTick;
    private Waddamburo.Game.Scenes.SceneDefinition? _scene;

    // Covered this long (from the cover's start) without the prefetch: finish it the slow way (most of
    // it is decoded by then; a slow reload measured 160 ms).
    private const int PrefetchLimitTicks = 60;

    /// <summary>Drives the reload; true while it runs (the tick's input is taken).</summary>
    public bool Tick()
    {
        if (_stage == Stage.None)
            return false;
        if (shell.Overlay.Player is { IsPlaying: true })
            return true;
        if (_stage == Stage.Covering && !shell.PrefetchReady && shell.Tick - _startTick < PrefetchLimitTicks)
            return true;
        if (_stage == Stage.Covering)
        {
            if (shell.Hosts.SongSelect is { } leaving)
                leaving.Reloading = true;
            _change?.Invoke();
            _change = null;
            shell.Catalog.Replace(_scene!);
            shell.Show(FlowScenes.SongSelect);
            shell.Overlay.Player?.GotoLabel(RainbowTransitionComposition.PlainRevealLabel, play: true);
            _stage = Stage.Revealing;
            return true;
        }
        shell.Overlay.Clear();
        shell.Overlay.Speed = 1;
        _stage = Stage.None;
        return true;
    }
}
