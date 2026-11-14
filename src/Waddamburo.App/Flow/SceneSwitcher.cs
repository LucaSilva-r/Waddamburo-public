using System.Diagnostics;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Which scene is on screen and how the game moves to another: <see cref="Show"/> (or the movies' own requests,
/// <see cref="ApplyPending"/>) leaves the active scene, loads the next from the catalog under the held last frame,
/// enters it and tidies memory; <see cref="FadeTo"/> does it under the black fade; <see cref="Prefetch"/> loads a scene
/// in the background ahead of its switch.
/// </summary>
internal sealed class SceneSwitcher(
    GameShell shell,
    ScenePresenter presenter,
    Func<SceneId, FlowScene> flowOf,
    Func<bool> covering,
    Func<RenderFrame> loadingFrame)
{
    private SceneId? _fadeTarget;
    private int _fadeStartTick;
    private long _liveAfterCollect;

    /// <summary>The scene on screen.</summary>
    public LumenGameSceneInstance Active { get; private set; } = null!;

    /// <summary>A gameplay lane is on screen (a song, the audio calibration, the benchmark): drums drive it, not the movies' controls.</summary>
    public bool OnLane => Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Calibration || Active.Id == FlowScenes.Benchmark;

    /// <summary>Starts decoding a scene's movies in the background; their textures go up between frames.</summary>
    public void Prefetch(SceneDefinition scene) => presenter.Prefetch(scene);

    /// <summary>The scene being prefetched, until one takes it.</summary>
    public SceneId? Prefetched => presenter.Prefetched;

    /// <summary>The prefetched scene is decoded and uploaded (its switch will not stall).</summary>
    public bool PrefetchReady => presenter.PrefetchReady;

    /// <summary>The active scene's textures, by the scene's texture index.</summary>
    public RenderTextureId[] Textures => presenter.Textures;

    /// <summary>Leaves the active scene and shows <paramref name="scene"/> (loaded fresh from the catalog).</summary>
    public void Show(SceneId scene)
    {
        switchScene(() => shell.Coordinator.TransitionToAsync(scene).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Showing {scene} at tick {shell.Tick}.");
    }

    /// <summary>Takes the transition a movie asked for (its scene request).</summary>
    public void ApplyPending()
    {
        shell.ReportDiagnostics();
        switchScene(() => shell.Coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Activated scene '{Active.Id}' at tick {shell.Tick}.");
    }

    /// <summary>The coordinator's scene becomes the active one and its flow enters it (at launch, and after each switch).</summary>
    public void Activate()
    {
        Active = shell.Coordinator.ActiveScene as LumenGameSceneInstance
            ?? throw new InvalidOperationException("The active scene is not a Lumen scene instance.");
        PerformanceRecorder.Scene = Active.Id.ToString();
        shell.Application.SetWindowTitle($"{Waddamburo.App.Hosting.SelfUpdate.WindowTitle} — {Active.Id}");
        presenter.Activate(Active);
        flowOf(Active.Id).Enter(Active.Id);
    }

    /// <summary>Fades to black (the intermission fade's "in", 1 s), then shows <paramref name="scene"/>.</summary>
    public void FadeTo(SceneId scene)
    {
        if (_fadeTarget is not null) return;
        // Results -> revival opens on the revival's own shutter.
        if (scene == FlowScenes.Retry)
        {
            Show(scene);
            return;
        }
        shell.Overlay.Show(FlowScenes.Fade).GotoLabel("in", play: true);
        _fadeTarget = scene;
        _fadeStartTick = shell.Tick;
    }

    /// <summary>True while a fade is under way; shows its scene once the screen is black.</summary>
    public bool FinishFade()
    {
        if (_fadeTarget is not { } target) return false;
        if (shell.Tick - _fadeStartTick < 60) return true;
        _fadeTarget = null;
        Show(target);
        shell.Overlay.Clear();
        return true;
    }

    public bool FadePending => _fadeTarget is not null;

    public void CancelFade()
    {
        _fadeTarget = null;
        shell.Overlay.Clear();
    }

    private void switchScene(Action transition)
    {
        var profile = Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";
        var from = Active.Id;
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetTotalAllocatedBytes();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        presenter.Hold(shell.Tick, black: shell.Overlay.IsShown || Active.Id == FlowScenes.Movie, loadingFrame);
        flowOf(Active.Id).Exit(Active.Id);
        presenter.ReleaseScene(Active.Id == FlowScenes.SongSelect || Active.Id == FlowScenes.Gameplay ? Active : null);
        var released = Stopwatch.GetTimestamp();
        presenter.Switching = true;
        try
        {
            transition();
        }
        finally
        {
            presenter.Switching = false;
        }
        var loaded = Stopwatch.GetTimestamp();
        Activate();
        var collect = Stopwatch.GetTimestamp();
        tidyMemory();
        shell.Application.DiscardElapsed();
        // Always: the log says where the game is (a report never has to guess the scene or mode).
        Console.WriteLine($"Scene: {from} -> {shell.DescribeScene()} at tick {shell.Tick}.");
        if (profile || PerformanceRecorder.Current is not null)
        {
            using var process = Process.GetCurrentProcess();
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Profile switch {from} -> {Active.Id}: "
                + $"exit {Stopwatch.GetElapsedTime(start, released).TotalMilliseconds:F0} ms, "
                + $"load {Stopwatch.GetElapsedTime(released, loaded).TotalMilliseconds:F0} ms, "
                + $"enter {Stopwatch.GetElapsedTime(loaded, collect).TotalMilliseconds:F0} ms, "
                + $"collect {Stopwatch.GetElapsedTime(collect).TotalMilliseconds:F0} ms, "
                + $"allocated {(GC.GetTotalAllocatedBytes() - allocated) / 1048576d:F1} MiB, "
                + $"GC {GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}, "
                + $"managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB, "
                + $"RSS {process.WorkingSet64 / 1048576d:F1} MiB (peak {process.PeakWorkingSet64 / 1048576d:F1}).");
            if (profile)
                Console.Error.WriteLine(line);
            PerformanceRecorder.Current?.Event(line["Profile ".Length..]);
        }
    }

    // The old scene's archives and decoded pixels are large-object garbage the GC neither compacts
    // nor returns on its own, so the heap only grew across songs. The loading frame is still up.
    // Only when there is much to give back: a collection costs 50-90 ms and most switches now free little.
    // Into a lane (songs, replays, training, calibration) only under a restart's black: the rainbow
    // keeps moving over a normal start, so the next switch out collects instead. On a lane the GC also
    // avoids blocking full collections of its own.
    // ponytail: 128 MiB grown since the last clean-up (garbage and all); tune if RSS creeps.
    private void tidyMemory()
    {
        System.Runtime.GCSettings.LatencyMode = OnLane
            ? System.Runtime.GCLatencyMode.SustainedLowLatency
            : System.Runtime.GCLatencyMode.Interactive;
        if ((!OnLane || covering()) && GC.GetTotalMemory(false) - _liveAfterCollect > 128L << 20)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            // The collection stops the audio mix thread too: queue past it (it took up to 75 ms traced).
            shell.Audio?.FillAhead(TimeSpan.FromMilliseconds(150));
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            _liveAfterCollect = GC.GetTotalMemory(false);
        }
        // Into a lane under the rainbow: a background collection instead (a few ms of pauses), which frees
        // Song Select's leftovers (the gameplay prefetch's decoded pixels) early in the song.
        else if (OnLane)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
    }
}
