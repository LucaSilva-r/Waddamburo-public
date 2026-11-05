using System.Diagnostics;
using Waddamburo.App.Scenes;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// The fixed benchmark route (--benchmark, Settings > Run Benchmark), recorded by the performance report:
/// the attract loop (its demo songs picked the same way every run) for <see cref="AttractTime"/>, Song Select
/// scrolled by script, the built-in stress song (<see cref="BenchmarkFlow"/>) for one player, then its first
/// <see cref="TwoPlayerTime"/> for two (two Don-chans), the two players' results, then the game quits (or starts
/// again normally, when run from the settings). Each step's start is an event in the report.
/// </summary>
internal sealed class BenchmarkRun(GameShell shell, BenchmarkFlow song, bool restartAfter)
{
    // WADDAMBURO_BENCHMARK_ATTRACT=<seconds> shortens the attract step (testing the route).
    private static readonly TimeSpan AttractTime = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_BENCHMARK_ATTRACT"), out var seconds) ? seconds : 180);
    private static readonly TimeSpan TwoPlayerTime = TimeSpan.FromSeconds(75), ResultsTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ScrollStep = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ScrollRight = TimeSpan.FromSeconds(20), ScrollLeft = TimeSpan.FromSeconds(10);
    // Song Select settles (its list, previews) before the scrolling starts; it is given up on after this long.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4), GiveUp = TimeSpan.FromSeconds(60);

    private enum Step { Attract, LoadingSongSelect, SongSelect, LoadingSong, Song, TwoPlayerSong, Results, Done }

    private Step _step = Step.Attract;
    private readonly Stopwatch _inStep = Stopwatch.StartNew();
    private TimeSpan _nextScroll;

    /// <summary>One tick: moves the route on.</summary>
    public void Tick()
    {
        switch (_step)
        {
            // As Entry does in play: Song Select loads in the background, then the switch is quick. An attract
            // screen change takes the loading slot back (it loads Entry), so the request repeats until ready.
            case Step.Attract when _inStep.Elapsed >= AttractTime && shell.Active.Id != FlowScenes.Gameplay:
                begin(Step.LoadingSongSelect);
                shell.Scenes.Prefetch(FlowScenes.SongSelectScene([0]));
                break;
            case Step.LoadingSongSelect when shell.Active.Id == FlowScenes.Gameplay:
                break;
            case Step.LoadingSongSelect when shell.Scenes.Prefetched != FlowScenes.SongSelect && _inStep.Elapsed <= GiveUp:
                shell.Scenes.Prefetch(FlowScenes.SongSelectScene([0]));
                break;
            case Step.LoadingSongSelect when shell.Scenes.PrefetchReady || _inStep.Elapsed > GiveUp:
                begin(Step.SongSelect);
                shell.ResetPlayers();
                shell.JoinPlayer(0);
                shell.Scenes.Show(FlowScenes.SongSelect);
                break;
            case Step.SongSelect:
                var since = _inStep.Elapsed - Settle;
                if (shell.Active.Id != FlowScenes.SongSelect)
                {
                    if (_inStep.Elapsed > GiveUp)
                        startSong();
                    break;
                }
                if (since >= ScrollRight + ScrollLeft)
                {
                    // As Song Select does for a picked song: its lane loads in the background first.
                    begin(Step.LoadingSong);
                    song.TwoPlayers = false;
                    shell.Scenes.Prefetch(song.Scene());
                }
                else if (since >= _nextScroll)
                {
                    shell.Application.InjectPress(since < ScrollRight ? SdlKeyboardKey.Right : SdlKeyboardKey.Left);
                    _nextScroll += ScrollStep;
                }
                break;
            case Step.LoadingSong when shell.Scenes.Prefetched != FlowScenes.Benchmark && _inStep.Elapsed <= GiveUp:
                shell.Scenes.Prefetch(song.Scene());
                break;
            case Step.LoadingSong when shell.Scenes.PrefetchReady || _inStep.Elapsed > GiveUp:
                startSong();
                break;
            case Step.Song when song.Done:
                begin(Step.TwoPlayerSong);
                shell.JoinPlayer(1);
                song.TwoPlayers = true;
                song.Limit = TwoPlayerTime;
                shell.Catalog.Replace(song.Scene());
                shell.Scenes.Show(FlowScenes.Benchmark);
                break;
            case Step.TwoPlayerSong when song.Done:
                begin(Step.Results);
                shell.Catalog.Replace(FlowScenes.Results);
                shell.Scenes.Show(FlowScenes.Result);
                break;
            case Step.Results when _inStep.Elapsed >= ResultsTime:
                begin(Step.Done);
                if (restartAfter)
                    shell.RequestRestart();
                else
                    shell.Application.RequestQuit();
                break;
        }
    }

    private void startSong()
    {
        begin(Step.Song);
        song.TwoPlayers = false;
        shell.Catalog.Replace(song.Scene());
        shell.Scenes.Show(FlowScenes.Benchmark);
    }

    private void begin(Step step)
    {
        _step = step;
        _inStep.Restart();
        _nextScroll = TimeSpan.Zero;
        PerformanceRecorder.Current?.Event($"benchmark: {step}");
        Console.WriteLine($"Benchmark: {step}.");
    }
}
