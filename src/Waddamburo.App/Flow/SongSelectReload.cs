using Waddamburo.App.Scenes;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Reloads Song Select behind a quick plain rainbow (Don-chan): it covers the screen, the change runs
/// (another library, grouping or order, search results), Song Select reloads with its music playing on,
/// and the rainbow opens. The game takes no input meanwhile. It can also leave Song Select under it (a
/// second player joining goes back to the entry).
/// </summary>
internal sealed class SongSelectReload(GameShell shell)
{
    // ponytail: three intermission ticks per game tick; tune if it feels rushed or slow.
    private const int Speed = 3;

    private enum Stage { None, Covering, Revealing }
    private Stage _stage;
    private Action? _change;
    private long _started;
    private Waddamburo.Game.Flow.SceneId? _next;

    public bool Busy => _stage != Stage.None;

    /// <summary>
    /// Covers the screen, then runs <paramref name="change"/> and reloads Song Select (or shows
    /// <paramref name="next"/> instead).
    /// </summary>
    public void Start(Action change, Waddamburo.Game.Flow.SceneId? next = null)
    {
        // A scene switch still under way would make the reload's restart throw (flow not Active).
        if (Busy || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active)
            return;
        _change = change;
        _next = next;
        // The movies decode and upload on worker threads while the rainbow covers (the reload's cost).
        _scene = FlowScenes.SongSelectScene(
            shell.JoinedSides.Count > 0 ? [.. shell.JoinedSides] : [shell.Hosts.PlayerSide], shell.Hosts.Waiwai);
        if (next is null)
            shell.Prefetch(_scene);
        shell.Overlay.Show(FlowScenes.Rainbow).GotoLabel(RainbowTransitionComposition.PlainCoverLabel, play: true);
        shell.Overlay.Speed = Speed;
        _stage = Stage.Covering;
        _started = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private Waddamburo.Game.Scenes.SceneDefinition? _scene;

    public IEnumerable<RenderQuad> Quads()
    {
        if (_stage != Stage.Covering || _next is not null || shell.PrefetchReady
            || System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds < .75) yield break;
        yield return shell.Painter.Rect(390, 620, 500, 52, new RenderColor(0, 0, 0, .75f));
        var dots = new string('.', shell.Tick / 20 % 4);
        yield return shell.Painter.Text(Strings.T("menu.loading_song_select") + dots, 640, 646, 480, 30);
    }

    /// <summary>Drives the reload; true while it runs (the tick's input is taken).</summary>
    public bool Tick()
    {
        if (_stage == Stage.None)
            return false;
        if (shell.Overlay.Player is { IsPlaying: true })
            return true;
        // A first-use menu layout can take longer than the cover. Never fall back to
        // waiting for its decoder on the window thread; keep rendering and polling events.
        if (_stage == Stage.Covering && _next is null && !shell.PrefetchReady)
            return true;
        if (_stage == Stage.Covering && _next is { } next)
        {
            _change?.Invoke();
            _change = null;
            shell.Show(next);
            shell.Overlay.Player?.GotoLabel(RainbowTransitionComposition.PlainRevealLabel, play: true);
            _stage = Stage.Revealing;
            return true;
        }
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
