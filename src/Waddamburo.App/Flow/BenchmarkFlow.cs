using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Flow;

/// <summary>
/// The benchmark's song: the built-in stress chart (Benchmark/stress.tja: dense streams, big notes, rolls,
/// balloons, Go-Go, scroll and BPM changes, branches) on a lane of its own, the bot drumming it, over a click
/// track (no music file to ship). Like the audio calibration, it needs no song library, so every machine plays
/// the same thing. <see cref="Done"/> once the chart has ended.
/// </summary>
internal sealed class BenchmarkFlow(GameShell shell) : FlowScene(shell)
{
    // The chart's BPM (stress.tja's BPM: header); the click only keeps the clock, its beat need not follow changes.
    private static readonly TimeSpan Beat = TimeSpan.FromMinutes(1d / 180);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(3);

    private PlayableChart? _chart;
    private GameplayAutoplay? _bot;
    private GameplayTimeline? _timeline;
    private AudioStreamTransport? _transport;
    private readonly Stopwatch _clock = new(); // without audio

    /// <summary>The chart has ended (and a little after), or <see cref="Limit"/> has passed.</summary>
    public bool Done { get; private set; }

    /// <summary>Both lanes play it (two Don-chans, two lanes' movies): set before <see cref="Scene"/>.</summary>
    public bool TwoPlayers { get; set; }

    /// <summary>Chart time it stops at, when shorter than the chart.</summary>
    public TimeSpan Limit { get; set; } = TimeSpan.MaxValue;

    /// <summary>The lane's scene: the regular gameplay's parts, picked the same way every run.</summary>
    public SceneDefinition Scene() =>
        GameplaySceneComposition.Create(FlowScenes.Benchmark, new Random(1), Shell.EnsoLayout, null, 0, twoPlayers: TwoPlayers);

    /// <summary>The stress chart, as the TJA reader reads it.</summary>
    public static PlayableChart Chart()
    {
        using var stream = typeof(BenchmarkFlow).Assembly.GetManifestResourceStream("Waddamburo.Benchmark.stress.tja")
            ?? throw new InvalidDataException("The benchmark chart is missing from the build.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TjaCatalogProvider.ReadChart(bytes.ToArray(),
            new ChartKey(new SongKey(SongSourceKind.Tja, "waddamburo-benchmark"), "oni"), TaikoCourse.Oni);
    }

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Gameplay;

    public override void Enter(SceneId scene)
    {
        Done = false;
        // One player and two report apart (the shell named the scene just before).
        PerformanceRecorder.Scene = TwoPlayers ? "benchmark-2p" : "benchmark-1p";
        _chart = Chart();
        _timeline = new GameplayTimeline(_chart.AuthoredOffset, Beat * 8);
        PlayableChart[] charts = TwoPlayers ? [_chart, _chart] : [_chart];
        Shell.Gameplay.Start(charts, Shell.Active, [.. charts.Select(static _ => TaikoCourse.Oni)]);
        _bot = new GameplayAutoplay(charts, 0);
        _clock.Restart();
        if (Shell.Audio is { } audio)
        {
            var clicks = new ClickTrack(audio.Mixer.Format, Beat, ClickTrack.Tone(audio.Mixer.Format, 2000), ClickTrack.Tone(audio.Mixer.Format, 1000));
            _transport = audio.PlayTransport(new ScheduledAudioSource(clicks, TimeSpan.Zero, TimeSpan.Zero), AudioBus.Metronome);
        }
        Console.WriteLine($"Benchmark song started ({(TwoPlayers ? 2 : 1)}P): {_chart.NoteCount} notes, {_chart.Duration.TotalSeconds:F0} s.");
    }

    public override void Exit(SceneId scene)
    {
        if (_transport is { } transport)
            Shell.Audio?.Mixer.Stop(transport.Handle);
        _transport = null;
        Shell.Gameplay.Stop();
    }

    private TimeSpan chartTime() => _timeline!.ChartTime(_transport is { } transport && Shell.Audio is { } audio
        ? audio.GetPosition(transport) : _clock.Elapsed);

    public override void Advance(LumenInputSnapshot input)
    {
        var frames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(frames);
    }

    public override void Tick(FlowInput input)
    {
        if (Shell.Overlay.Player is { IsPlaying: false })
            Shell.Overlay.Clear();
    }

    public override LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        Shell.Gameplay.CreateSnapshot(chartTime(), interpolation);

    // The bot's presses only: the player's keys do nothing here.
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        if (_chart is null)
            return;
        var time = chartTime();
        Shell.Gameplay.Advance(_bot!.Apply(new SdlKeyboardSnapshot([], null, keys.Timestamp), time), time);
        if (time > _chart.Duration + After || time > Limit)
            Done = true;
    }
}
