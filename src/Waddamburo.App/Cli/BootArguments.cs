using System.Collections.Immutable;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Cli;

/// <summary>
/// The command line, parsed and checked as a whole: the window and run limits, the game folder (or a positional one),
/// the diagnostic tools' inputs, and the options of a normal boot. <see cref="Boot.Run"/> dispatches on it.
/// </summary>
internal sealed record BootArguments(string[] Raw)
{
    public int? FrameLimit { get; init; }
    public int? TickLimit { get; init; }
    public int? SeekFrame { get; init; }
    public (int Width, int Height) WindowSize { get; init; } = (1280, 720);
    public string? ScreenshotPath { get; init; }
    public ImmutableArray<CallbackInvocation> Callbacks { get; init; } = [];
    public SdlKeyboardTimeline InputTimeline { get; init; } = null!;
    public string? ArchivePath { get; init; }
    public string? MovieName { get; init; }
    /// <summary>--package=: a movie.json package in the viewer.</summary>
    public string? PackagePath { get; init; }

    /// <summary>--skin[=folder]: the movie editor on a whole skin (its home page); bare: the skin found from here.</summary>
    public string? SkinPath { get; init; }

    /// <summary>--lint-skin[=folder]: a skin's problems printed, exit 1 when one is an error (CI).</summary>
    public string? LintSkinPath { get; init; }
    /// <summary>--play=CHART: the bot plays the chart straight after start-up, then quits.</summary>
    public Waddamburo.App.Flow.DirectPlay? Play { get; init; }
    public string? ScenePath { get; init; }
    public string? GameDataRoot { get; init; }
    public string? AssetRoot { get; init; }
    public string? TjaRoot { get; init; }
    public string? FontPath { get; init; }
    public string? AudioPath { get; init; }
    public string? JinglePath { get; init; }
    public string? SoundRoot { get; init; }
    public string? DonRoot { get; init; }
    public string? StartSceneOption { get; init; }
    public StartScene StartScene { get; init; }
    public bool Login { get; init; }
    public bool Logout { get; init; }
    public string? ExportCharts { get; init; }
    /// <summary>Song sync from the console (see Sync.SyncCommands): the first --sync-* argument.</summary>
    public string? SyncCommand { get; init; }
    public bool Autoplay { get; init; }
    public bool RecordPerformance { get; init; }
    public BenchmarkMode Benchmark { get; init; }
    /// <summary>eng/flow-smoke.sh: one play mode started from Song Select (FlowSmokeRun).</summary>
    public FlowSmokeMode FlowSmoke { get; init; }
    public string? FlowSmokeSong { get; init; }
    public string? PreviewPath { get; init; }
    public string? SoundBankProbe { get; init; }
    public bool SyntheticSmoke { get; init; }
    /// <summary>The cabinet's countdown option (chassisinfo disable_countdowntimer); on by default.</summary>
    public bool Countdown { get; init; } = true;
    public string[] PositionalRoots { get; init; } = [];
    public bool EntrySongSelect { get; init; }
    /// <summary>The game itself (a game folder, or nothing else asked for), not a diagnostic tool.</summary>
    public bool NormalBoot { get; init; }

    public bool Has(string flag) => Raw.Contains(flag, StringComparer.Ordinal);

    public bool HasPrefix(string prefix) => Raw.Any(argument => argument.StartsWith(prefix, StringComparison.Ordinal));

    public string? Option(string prefix) => option(Raw, prefix);

    public static BootArguments Parse(string[] args)
    {
        var frameLimit = limit(args, "--frames=");
        var tickLimit = limit(args, "--ticks=");
        var screenshotPath = option(args, "--screenshot=");
        if (screenshotPath is not null)
        {
            // --play runs until its chart has ended (the screenshot is taken only at a limit given).
            if (frameLimit is null && tickLimit is null)
            {
                if (option(args, "--play=") is null) frameLimit = 1;
                else tickLimit = int.MaxValue;
            }
            if (frameLimit == 0)
                throw new ArgumentException("--screenshot requires at least one rendered frame.");
        }
        var parsed = new BootArguments(args)
        {
            FrameLimit = frameLimit,
            TickLimit = tickLimit,
            SeekFrame = limit(args, "--seek-frame="),
            WindowSize = windowSize(args),
            ScreenshotPath = screenshotPath,
            Callbacks = CallbackInvocation.Parse(args),
            InputTimeline = InputPulseParser.Parse(args),
            ArchivePath = option(args, "--archive="),
            MovieName = option(args, "--movie="),
            PackagePath = option(args, "--package="),
            SkinPath = option(args, "--skin=") ?? (args.Contains("--skin") ? "" : null),
            LintSkinPath = option(args, "--lint-skin=") ?? (args.Contains("--lint-skin") ? "" : null),
            Play = option(args, "--play=") is { } playChart
                ? new Waddamburo.App.Flow.DirectPlay(Path.GetFullPath(playChart),
                    Enum.Parse<Waddamburo.Catalog.TaikoCourse>(option(args, "--play-course=") ?? "oni", ignoreCase: true),
                    option(args, "--play-players=") == "2" ? 2 : 1)
                : null,
            ScenePath = option(args, "--scene="),
            GameDataRoot = option(args, "--game-data="),
            AssetRoot = option(args, "--asset-root="),
            TjaRoot = option(args, "--tja-root="),
            FontPath = option(args, "--font="),
            AudioPath = option(args, "--play-audio="),
            JinglePath = option(args, "--play-jingle="),
            SoundRoot = option(args, "--sound-root="),
            StartSceneOption = option(args, "--start-scene="),
            Login = args.Contains("--login", StringComparer.Ordinal),
            Logout = args.Contains("--logout", StringComparer.Ordinal),
            ExportCharts = option(args, "--export-charts="),
            SyncCommand = args.FirstOrDefault(static arg => arg.StartsWith("--sync-", StringComparison.Ordinal)),
            Autoplay = args.Contains("--autoplay", StringComparer.Ordinal),
            RecordPerformance = args.Contains("--benchmark-record", StringComparer.Ordinal),
            Benchmark = args.Contains("--benchmark", StringComparer.Ordinal) ? BenchmarkMode.Quit
                : args.Contains("--benchmark=restart", StringComparer.Ordinal) ? BenchmarkMode.Restart
                : args.Contains("--benchmark=paced", StringComparer.Ordinal) ? BenchmarkMode.Paced : BenchmarkMode.Off,
            FlowSmoke = option(args, "--flow-smoke=") is { } smokeMode
                ? Enum.TryParse<FlowSmokeMode>(smokeMode.Replace("-", ""), ignoreCase: true, out var parsedSmoke) && parsedSmoke != FlowSmokeMode.Off
                    ? parsedSmoke : throw new ArgumentException($"--flow-smoke: unknown mode '{smokeMode}'.")
                : FlowSmokeMode.Off,
            FlowSmokeSong = option(args, "--flow-smoke-song="),
            DonRoot = option(args, "--don-root="),
            PreviewPath = option(args, "--preview="),
            SoundBankProbe = option(args, "--probe-sound-bank="),
            SyntheticSmoke = args.Contains("--smoke", StringComparer.Ordinal),
            Countdown = !args.Contains("--no-countdown", StringComparer.Ordinal),
            EntrySongSelect = args.Contains("--entry-song-select", StringComparer.Ordinal),
        };
        parsed = parsed with { StartScene = StartSceneParser.Parse(parsed.StartSceneOption) };
        var positionalRoots = args.Where(static argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positionalRoots.Length > 1)
            throw new ArgumentException("Normal boot accepts at most one positional game-data directory.");
        if (parsed.GameDataRoot is not null && positionalRoots.Length != 0)
            throw new ArgumentException("Supply the game-data directory either positionally or with --game-data, not both.");
        parsed = parsed with { PositionalRoots = positionalRoots, GameDataRoot = parsed.GameDataRoot ?? positionalRoots.SingleOrDefault() };
        if (args.Contains("--preview", StringComparer.Ordinal))
        {
            if (parsed.PreviewPath is not null)
                throw new ArgumentException("Supply --preview or --preview=PATH, not both.");
            parsed = parsed with { PreviewPath = parsed.GameDataRoot ?? GameDataLayout.DefaultRoot(), GameDataRoot = null };
        }
        if (parsed.AudioPath is not null && parsed.JinglePath is not null)
            throw new ArgumentException("--play-audio and --play-jingle cannot be combined.");
        var hasDiagnosticContent = parsed.EntrySongSelect
            || parsed.ArchivePath is not null
            || parsed.PackagePath is not null
            || parsed.SkinPath is not null
            || parsed.LintSkinPath is not null
            || parsed.MovieName is not null
            || parsed.ScenePath is not null
            || parsed.AssetRoot is not null
            || parsed.TjaRoot is not null
            || parsed.AudioPath is not null
            || parsed.JinglePath is not null
            || parsed.SoundRoot is not null
            || parsed.DonRoot is not null
            || parsed.SoundBankProbe is not null
            || parsed.PreviewPath is not null;
        if (parsed.SyntheticSmoke && (parsed.GameDataRoot is not null || hasDiagnosticContent))
            throw new ArgumentException("--smoke cannot be combined with game data or diagnostic content.");
        return parsed with { NormalBoot = !parsed.SyntheticSmoke && (parsed.GameDataRoot is not null || !hasDiagnosticContent) };
    }

    private static (int Width, int Height) windowSize(string[] arguments)
    {
        const string prefix = "--window-size=";
        var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
        if (option is null)
            return (1280, 720);
        var value = option.AsSpan(prefix.Length);
        var separator = value.IndexOfAny('x', 'X');
        if (separator <= 0 || separator == value.Length - 1
            || !int.TryParse(value[..separator], out var width)
            || !int.TryParse(value[(separator + 1)..], out var height)
            || width <= 0 || height <= 0)
        {
            throw new ArgumentException("--window-size must use positive integer WIDTHxHEIGHT dimensions.");
        }
        return (width, height);
    }

    private static int? limit(string[] arguments, string prefix)
    {
        var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
        if (option is null)
            return null;
        return int.TryParse(option.AsSpan(prefix.Length), out var value) && value >= 0
            ? value
            : throw new ArgumentException($"{prefix[..^1]} must be a non-negative integer.");
    }

    private static string? option(string[] arguments, string prefix)
    {
        var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
        if (option is null)
            return null;
        var value = option[prefix.Length..];
        return value.Length > 0 ? value : throw new ArgumentException($"{prefix} requires a value.");
    }
}
