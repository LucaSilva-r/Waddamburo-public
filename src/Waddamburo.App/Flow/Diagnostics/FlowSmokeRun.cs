using Waddamburo.Game.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.App.Flow.Diagnostics;

/// <summary>--flow-smoke=&lt;mode&gt;: which play eng/flow-smoke.sh starts.</summary>
/// Demo only seeds the random picks and leaves the attract to start its demo play.
internal enum FlowSmokeMode { Off, Normal, TwoPlayers, Waiwai, AiBattle, Dan, Review, Training, Demo }

/// <summary>
/// A refactor safety net (eng/flow-smoke.sh, docs/development/app-refactor.md): from Song Select, sets the credit
/// up for one play mode the way the entry and Song Select would, then files the play request Song Select would, so
/// the real rainbow, gameplay, results and credit-end flows run. No menu presses: library order cannot break it.
/// The lanes play themselves (dev autoplay, or the autoplay review); the play's result line goes to the log.
/// </summary>
internal sealed class FlowSmokeRun(GameShell shell, GameplayFlow gameplay, SongSelectFlow songSelect, FlowSmokeMode mode, string? title)
{
    // Song Select settles (its list, the dan catalogue) before the request, which goes at a fixed tick so a slow
    // load (the dan catalogue on a fresh folder) doesn't move the whole run.
    private const int SettleTicks = 120, RequestTick = 600, GiveUpTicks = 3600;

    private int _readyTicks;
    private bool _requested, _practising;

    /// <summary>The play's mode, before the scene starts: the lanes play themselves.</summary>
    public static ArcadeSettings Configure(ArcadeSettings arcade, FlowSmokeMode mode) => arcade with
    {
        Home = true,
        // The review modes review an autoplay; the others play it live (dev autoplay never saves).
        Autoplay = mode is FlowSmokeMode.Review or FlowSmokeMode.Training,
        DevAutoplay = mode is not (FlowSmokeMode.Review or FlowSmokeMode.Training),
        // The window may lose focus while the runs go on (the user moving it): a pause would hold it forever.
        DevNoFocusPause = true,
    };

    /// <summary>One tick.</summary>
    public void Tick()
    {
        if (mode == FlowSmokeMode.Demo)
            return;
        if (_requested)
        {
            // Training starts from the review, once revealed (the practice key does it in play).
            if (mode == FlowSmokeMode.Training && !_practising)
                _practising = gameplay.TryStartPractice();
            return;
        }
        if (shell.Active.Id != FlowScenes.SongSelect || shell.Hosts.SongSelect is null
            || mode == FlowSmokeMode.Dan && !shell.Dan.Ready)
        {
            if (shell.Tick > GiveUpTicks)
                fail("Song Select (and the dan catalogue) never got ready");
            return;
        }
        if (++_readyTicks < SettleTicks || shell.Tick < RequestTick)
            return;
        _requested = true;
        if (mode == FlowSmokeMode.Dan)
        {
            if (shell.Dan.Courses(shell.Dan.Series) is not [var course, ..])
            {
                fail("no playable dan course");
                return;
            }
            Console.WriteLine($"Smoke: dan course {course.Course.Name}.");
            songSelect.PlayCourse(course);
            return;
        }
        var two = mode is FlowSmokeMode.TwoPlayers or FlowSmokeMode.Waiwai;
        if (two)
        {
            shell.Players.Join(0);
            shell.Players.Join(1);
            shell.Credit.Waiwai = mode == FlowSmokeMode.Waiwai;
        }
        var songs = shell.SongCatalog.Categories.SelectMany(static category => category.Songs)
            .Where(song => mode != FlowSmokeMode.Waiwai || song.Descriptor.WaiwaiComposition is not null)
            .Where(static song => song.Descriptor.Charts.Any(static chart => chart.Course is not null))
            .ToArray();
        var picked = songs.FirstOrDefault(song => title is not null
                && song.Descriptor.Title.Primary.Contains(title, StringComparison.OrdinalIgnoreCase))
            ?? songs.FirstOrDefault();
        if (picked is null)
        {
            fail("no song to play");
            return;
        }
        var chart = picked.Descriptor.Charts.Where(static chart => chart.Course is not null)
            .OrderBy(static chart => chart.Course!.Value == TaikoCourse.Oni ? 0 : 1)
            .ThenByDescending(static chart => chart.Course!.Value).First();
        PlayerChartRequest player(LocalPlayerSlot slot) => new(slot, chart.Key, chart.ChartAsset, chart.Course!.Value);
        var request = new PlayRequest(shell.SongCatalog.Revision, picked.Descriptor.Key, picked.Descriptor.AudioAsset,
            two ? [player(LocalPlayerSlot.PlayerOne), player(LocalPlayerSlot.PlayerTwo)] : [player(LocalPlayerSlot.PlayerOne)])
        {
            Seed = 20261118,
        };
        Console.WriteLine($"Smoke: {mode} on '{picked.Descriptor.Title.Primary}' ({chart.Course}).");
        if (!shell.PlayRequests.TryRequestPlay(request))
            fail("the play request was refused");
    }

    private void fail(string why)
    {
        _requested = true;
        Console.Error.WriteLine($"Smoke failed: {why}.");
        shell.Application.RequestQuit();
    }
}
