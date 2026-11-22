using Waddamburo.App.Cli;
using Waddamburo.App.Hosting;
using Waddamburo.App.Tools;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App;

/// <summary>The game's start, shared by the desktop executable (Program.cs) and the mobile apps' activities.</summary>
public static class Boot
{
    /// <summary>Returned by <see cref="Run"/> when the game asked to restart and cannot start a new process (mobile):
    /// the caller runs it again.</summary>
    public const int RestartExitCode = 75;

    /// <summary>Where Waddamburo keeps its own files (settings, scores, caches) when they cannot live beside the game
    /// data: the app's private storage on a phone. Null on desktop (USRDIR/waddamburo).</summary>
    public static string? AppHome { get; set; }

    /// <summary>Where the files a player opens themselves go (config.cfg, crash logs, screenshots) when
    /// <see cref="AppHome"/> is out of their reach: a phone's own folder beside Green. Null: AppHome's (or USRDIR/waddamburo).</summary>
    public static string? UserHome { get; set; }

    /// <summary>Opens the app's files in the system's file screen (a phone: its document provider); null where there is none.</summary>
    public static Action? OpenGameFiles { get; set; }

    /// <summary>The custom TJA folder when the app keeps its own (the phone's Custom Songs); null: USRDIR/custom_songs.</summary>
    public static string? TjaFolder { get; set; }

    /// <summary>Other releases' folders the app keeps (the phone's Arcade, Wii and Wii U subfolders), listed with
    /// config.cfg's stock_folders.</summary>
    public static IReadOnlyList<string> StockFolders { get; set; } = [];

    /// <summary>This device's name as others list it (a phone sets its model).</summary>
    public static string DeviceName { get; set; } = Environment.MachineName;


    // The normal boot's start (the game folder, its settings, the update check), before the game opens its window: a
    // failure there shows the error screen in a window of its own (people launch the game without a console).
    private static bool s_startingUp;

    public static int Run(string[] args)
    {
        s_startingUp = false;
        try
        {
            // TaikOnline's scorer: stdin to stdout, before anything that touches the console, updates or game data.
            if (args is ["--rescore-dan"])
                return Rescore.RunCourses(Console.OpenStandardInput(), Console.OpenStandardOutput());
            if (args is ["--rescore"])
                return Rescore.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
            ReleaseConsole.Begin();
            if (SelfUpdate.HandOff(ref args))
                return 0;
            // --import-green GREEN SOURCE...: the phone setup's import, on a PC (testing, or a dump downloaded there).
            if (args is ["--import-green", var green, .. var sources] && sources.Length > 0)
            {
                var import = new GreenImport(green);
                foreach (var source in sources.Where(static source => !source.StartsWith("--", StringComparison.Ordinal)))
                {
                    using var stream = File.OpenRead(source);
                    import.Import(stream);
                    Console.WriteLine($"{source}: {import.Files} files so far");
                }
                Console.WriteLine($"{GreenData.Missing(green).Count} of {GreenData.FileCount} release files missing");
                return 0;
            }
            if (args.Contains("--setup-preview", StringComparer.Ordinal) || args.Any(static arg => arg.StartsWith("--setup-preview=", StringComparison.Ordinal)))
                return Sync.SetupPreview.Run(new BootArguments(args).Option("--setup-preview="));
            var a = BootArguments.Parse(args);
            if (a.PreviewPath is not null)
            {
                if (a.GameDataRoot is not null || a.ArchivePath is not null || a.MovieName is not null || a.ScenePath is not null
                    || a.AssetRoot is not null || a.TjaRoot is not null || a.AudioPath is not null || a.JinglePath is not null
                    || a.SoundRoot is not null || a.DonRoot is not null || a.SoundBankProbe is not null || a.EntrySongSelect
                    || a.StartSceneOption is not null || a.SeekFrame is not null || !a.Callbacks.IsEmpty)
                    throw new ArgumentException("--preview cannot be combined with game or other content options.");
                ReleaseConsole.Show(true);
                FilePreview.Run(a.PreviewPath, a.WindowSize.Width, a.WindowSize.Height, a.FrameLimit, a.TickLimit, a.ScreenshotPath, a.InputTimeline);
                return 0;
            }
            return a.NormalBoot ? startGame(a) : runDiagnostic(a);
        }
        catch (Exception exception)
        {
            if (s_startingUp)
            {
                Console.Error.WriteLine(exception.Message);
                if (CrashScreen.ShowAlone(exception, ReleaseConsole.WriteCrashFile(exception)))
                    return 1;
            }
            ReleaseConsole.Fail(exception);
            return 1;
        }
    }

    // The game: its folder and settings (and the update), the console tools that need them, then the game itself.
    private static int startGame(BootArguments a)
    {
        if (a.ArchivePath is not null || a.MovieName is not null || a.ScenePath is not null
            || a.AssetRoot is not null || a.TjaRoot is not null || a.AudioPath is not null
            || a.SoundRoot is not null || a.DonRoot is not null || a.EntrySongSelect)
        {
            throw new ArgumentException("--game-data cannot be combined with diagnostic content options.");
        }
        if (a.SeekFrame is not null || !a.Callbacks.IsEmpty)
            throw new ArgumentException("--seek-frame and --invoke are unavailable during normal boot.");

        var consoleTool = a.Login || a.Logout || a.ExportCharts is not null || a.SyncCommand is not null
            || a.HasPrefix("--online-test") || a.HasPrefix("--upscale-textures");
        s_startingUp = !consoleTool && a.ScreenshotPath is null && a.FrameLimit is null && a.TickLimit is null;
        // The update comes first, before anything that could fail (a release that can't start must still be replaced
        // by the next): the settings are read for it if they can be, and read again below to report what's wrong.
        GameDataLayout? early = null;
        Waddamburo.Game.Flow.ArcadeSettings? earlyArcade = null;
        try
        {
            early = GameDataLayout.Resolve(a.GameDataRoot ?? GameDataLayout.DefaultRoot(), a.FontPath);
            earlyArcade = Waddamburo.Game.Flow.ArcadeSettings.LoadOrCreate(
                Path.Combine(early.Shared, Waddamburo.Game.Flow.ArcadeSettings.FileName));
            Strings.Use(earlyArcade.Language);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
        if (s_startingUp && (earlyArcade?.AutoUpdate ?? true)
            && SelfUpdate.UpdateNow(a.Raw, a.WindowSize.Width, a.WindowSize.Height, earlyArcade?.Fullscreen ?? false))
        {
            return 0;
        }
        var layout = early ?? GameDataLayout.Resolve(a.GameDataRoot ?? GameDataLayout.DefaultRoot(), a.FontPath);
        ReleaseConsole.CrashFolder = Path.Combine(layout.Shared, "logs");
        ReleaseConsole.LogToFile();
        Console.WriteLine($"Game data: {layout.Root}");
        Console.WriteLine($"Title font: {layout.FontPath}");
        Waddamburo.Platform.Sdl.Text.NativeVerticalTextRasterizer.FallbackFonts = layout.FallbackFonts;
        // Cabinet settings (free play, credits, songs per session) beside the game data.
        var arcadePath = Path.Combine(layout.Shared, Waddamburo.Game.Flow.ArcadeSettings.FileName);
        var arcade = earlyArcade ?? Waddamburo.Game.Flow.ArcadeSettings.LoadOrCreate(arcadePath);
        // The cache defaults to this PC's user cache (a game on a USB stick must not decode onto it).
        layout.MoveGameCacheTo(layout.Cache(arcade.CacheFolder));
        Console.WriteLine($"Cache: {layout.Cache(arcade.CacheFolder)}");
        Waddamburo.Platform.Sdl.Media.VgmstreamCli.CacheDirectory = layout.AudioCache(arcade.CacheFolder);
        Waddamburo.Platform.Sdl.Media.VgmstreamCli.GameFolder = layout.Root;
        Waddamburo.App.Audio.SongLoudness.CacheFile = Path.Combine(layout.Cache(arcade.CacheFolder), "loudness.tsv");
        // The online test reports on the console, whatever the settings say.
        if (a.HasPrefix("--online-test"))
            ReleaseConsole.OpenForTool();
        else
            ReleaseConsole.Show(arcade.ShowConsole);
        Strings.Use(arcade.Language);
        Waddamburo.Game.SongSelect.SongSelectCatalogView.Japanese = Strings.Language.StartsWith("ja", StringComparison.Ordinal);
        Console.WriteLine($"Language: {Strings.Language}");
        // ponytail: the config reaches BgfxSupport through its env var; the env var itself still wins.
        if (arcade.Renderer is not null && DiagnosticSwitches.Renderer is null)
            Environment.SetEnvironmentVariable("WADDAMBURO_RENDERER", arcade.Renderer);
        Console.WriteLine(arcade.Home ? $"Settings: {arcadePath} (home mode: free play, endless songs)."
            : $"Cabinet settings: {arcadePath} (arcade, {(arcade.FreePlay ? "free play" : "coin mode")}, "
                + $"{arcade.SongsPerSession} songs per session).");
        // Keep the startup screen available through account loading and window creation. Once the game's
        // handlers take over, BootChecks and GameShell.Run handle failures before they reach the outer catch.
        // Fills the texture upscale cache ahead of play, with no game window (see TextureCacheBuilder).
        if (a.Raw.FirstOrDefault(static arg => arg.StartsWith("--upscale-textures", StringComparison.Ordinal)) is { } upscale)
            return TextureCacheBuilder.Run(layout.UpscaleCache(arcade.CacheFolder), layout.LumenRoot, upscale.Split('=') is [_, var count]
                ? int.Parse(count, System.Globalization.CultureInfo.InvariantCulture)
                : Math.Max(1, Environment.ProcessorCount - 2));
        // One-off: every stock and Nijiiro chart, hashed, for the server to import as ranked (see ChartExport).
        if (a.ExportCharts is not null)
            return ChartExport.Run(Path.GetFullPath(Path.Combine(layout.LumenRoot, "..", "..")), arcade, a.ExportCharts);
        // Song sync: pairs and toggles in the home; libraries mirrored from other devices beside them (a phone
        // sets its own, with its Green folder).
        Sync.SyncWire.Folder = Path.Combine(layout.CacheHome, "sync"); // stable home: a nightly shares the pairs
        Sync.ReceivedLibraries.Root ??= Path.Combine(layout.CacheHome, "received");
        if (a.SyncCommand is not null)
            return Sync.SyncCommands.Run(a.Raw, Path.Combine(layout.Root, "data"),
                () => Gameplay.SongLibraries.Load(Path.Combine(layout.Root, "data"), arcade, layout.TjaRoot));
        var accounts = Waddamburo.Services.Scores.AccountBook.Load(layout.Home);
        // Phones: a newer release keeps this build off TaikOnline until it is installed (no self-update there).
        Waddamburo.App.Online.UpdateGate.Check();
        if (a.Login || a.Logout)
            return AccountCommands.Run(arcade, accounts, a.Login);
        if (a.Option("--online-test=") is { } onlineRoom)
            return OnlineTest.Run(arcade, accounts, onlineRoom, Path.Combine(layout.Shared, "logs"));
        if (a.Has("--online-test"))
            return OnlineTest.Run(arcade, accounts, null, Path.Combine(layout.Shared, "logs"));
        Console.WriteLine(!arcade.Home
            ? arcade is { Server: not null, CabinetToken: not null }
                ? $"Scores: cabinet mode, players log in with a code from {arcade.Server} (cabinet {Waddamburo.App.Online.CabinetPairing.CabinetId})."
                : "Scores: arcade without a cabinet token (not saved)."
            : $"Scores: kept on this PC; {accounts.Accounts.Count} account(s)"
                + (accounts.Default is { } first ? $", {first.Name} joins by default" : "")
                + (arcade.Server is null ? ", offline." : $", uploaded to {arcade.Server}."));
        var exitCode = GameShell.Run(new GameOptions(
            layout.LumenRoot,
            layout.DonRoot,
            a.WindowSize.Width,
            a.WindowSize.Height,
            a.FrameLimit,
            a.TickLimit,
            a.ScreenshotPath,
            a.InputTimeline,
            layout.TjaRoot,
            layout.FontPath,
            a.JinglePath,
            layout.SoundRoot,
            a.Countdown,
            a.StartScene,
            arcade,
            arcadePath,
            accounts,
            Path.Combine(layout.Home, Waddamburo.Services.Scores.ScoreStore.FileName),
            a.Autoplay,
            arcade.Fullscreen,
            layout.ServiceFontPath,
            a.RecordPerformance,
            a.Benchmark,
            a.FlowSmoke,
            a.FlowSmokeSong));
        // Relaunched only now: the old instance has let go of its files (the texture cache, the scores).
        if (GameShell.RestartRequested && PlatformInfo.IsMobile)
            return RestartExitCode; // no second process on a phone: the activity boots again
        if (GameShell.RestartRequested)
            SelfUpdate.Relaunch([.. a.Raw.Where(static argument => !argument.StartsWith("--benchmark", StringComparison.Ordinal)), .. GameShell.RestartArguments]);
        return exitCode; // 1 after the boot's error screen
    }

    // The diagnostic tools: the sound bank probe, Entry/Song Select from loose content, the scene and movie viewers, and
    // the synthetic window smoke.
    private static int runDiagnostic(BootArguments a)
    {
        // Diagnostic runs always show their output.
        ReleaseConsole.Show(true);
        if (a.SoundBankProbe is not null)
        {
            if (a.ArchivePath is not null || a.MovieName is not null || a.ScenePath is not null
                || a.AssetRoot is not null || a.TjaRoot is not null || a.AudioPath is not null
                || a.JinglePath is not null || a.SoundRoot is not null || a.DonRoot is not null || a.EntrySongSelect
                || a.PositionalRoots.Length != 0 || a.StartSceneOption is not null)
            {
                throw new ArgumentException("--probe-sound-bank cannot be combined with game or content options.");
            }
            SoundBankProbe.Run(a.SoundBankProbe, a.WindowSize.Width, a.WindowSize.Height, a.FrameLimit, a.TickLimit, a.ScreenshotPath, a.InputTimeline);
            return 0;
        }
        if (a.EntrySongSelect)
        {
            if (a.AudioPath is not null)
                throw new ArgumentException("--play-audio cannot be combined with --entry-song-select.");
            if (a.AssetRoot is null)
                throw new ArgumentException("--entry-song-select requires --asset-root.");
            if (a.TjaRoot is null || a.FontPath is null)
                throw new ArgumentException("--entry-song-select requires --tja-root and --font.");
            if (a.ArchivePath is not null || a.MovieName is not null || a.ScenePath is not null)
                throw new ArgumentException("--entry-song-select cannot be combined with movie or scene inputs.");
            if (a.SeekFrame is not null || !a.Callbacks.IsEmpty)
                throw new ArgumentException("--seek-frame and --invoke are unavailable for --entry-song-select.");
            GameShell.Run(new GameOptions(
                a.AssetRoot,
                a.DonRoot,
                a.WindowSize.Width,
                a.WindowSize.Height,
                a.FrameLimit,
                a.TickLimit,
                a.ScreenshotPath,
                a.InputTimeline,
                a.TjaRoot,
                a.FontPath,
                a.JinglePath,
                a.SoundRoot,
                a.Countdown,
                a.StartScene,
                Autoplay: a.Autoplay));
            return 0;
        }
        if (a.StartSceneOption is not null)
            throw new ArgumentException("--start-scene requires normal boot or --entry-song-select.");
        if (a.SoundRoot is not null)
            throw new ArgumentException("--sound-root currently requires --entry-song-select.");
        if (a.DonRoot is not null)
            throw new ArgumentException("--don-root currently requires --entry-song-select.");
        if (a.ScenePath is not null || a.AssetRoot is not null)
        {
            if (a.AudioPath is not null || a.JinglePath is not null)
                throw new ArgumentException("Audio diagnostics cannot be combined with --scene.");
            if (a.ScenePath is null || a.AssetRoot is null)
                throw new ArgumentException("--scene and --asset-root must be supplied together.");
            if (a.ArchivePath is not null || a.MovieName is not null)
                throw new ArgumentException("Scene options cannot be combined with --archive or --movie.");
            if (a.SeekFrame is not null)
                throw new ArgumentException("--seek-frame currently requires a single --archive/--movie input.");
            if (!a.Callbacks.IsEmpty)
                throw new ArgumentException("--invoke currently requires a single --archive/--movie input.");
            SceneViewer.Run(
                a.ScenePath,
                a.AssetRoot,
                a.WindowSize.Width,
                a.WindowSize.Height,
                a.FrameLimit,
                a.TickLimit,
                a.ScreenshotPath,
                a.InputTimeline);
            return 0;
        }
        if (a.ArchivePath is not null || a.MovieName is not null)
        {
            if (a.AudioPath is not null || a.JinglePath is not null)
                throw new ArgumentException("Audio diagnostics cannot be combined with --archive/--movie.");
            if (a.ArchivePath is null || a.MovieName is null)
                throw new ArgumentException("--archive and --movie must be supplied together.");
            MovieViewer.Run(
                a.ArchivePath,
                a.MovieName,
                a.WindowSize.Width,
                a.WindowSize.Height,
                a.SeekFrame,
                a.FrameLimit,
                a.TickLimit,
                a.ScreenshotPath,
                a.Callbacks,
                a.InputTimeline);
            return 0;
        }
        if (a.SeekFrame is not null)
            throw new ArgumentException("--seek-frame requires --archive and --movie.");
        if (!a.Callbacks.IsEmpty)
            throw new ArgumentException("--invoke requires --archive and --movie.");
        if (!a.InputTimeline.IsEmpty)
            throw new ArgumentException("--press requires --archive/--movie or --scene/--asset-root.");
        using var application = new SdlApplication(
            "Waddamburo",
            a.WindowSize.Width,
            a.WindowSize.Height,
            debugGpu: false,
            resizable: a.ScreenshotPath is null,
            highPixelDensity: a.ScreenshotPath is null);
        Console.WriteLine($"Renderer: {application.GpuDriver}");
        using var audioDevice = a.AudioPath is null && a.JinglePath is null ? null : new SdlAudioDevice();
        using var music = a.AudioPath is null ? null : new StreamingMusicPlayer(audioDevice!, a.AudioPath);
        using var audioEngine = a.JinglePath is null ? null : new AudioEngine(audioDevice!);
        if (music is not null)
        {
            music.Play();
            Console.WriteLine(
                $"Audio: {Path.GetFileName(a.AudioPath)}, {music.Info.SampleRate} Hz, {music.Info.Channels} channels");
            Console.WriteLine(
                $"SDL audio: {audioDevice!.Driver}, " +
                $"{audioDevice.HardwareFormat.SampleRate} Hz, " +
                $"{audioDevice.HardwareFormat.Channels} channels, " +
                $"{audioDevice.HardwareBufferFrames} buffer frames");
        }
        if (audioEngine is not null)
        {
            var handle = audioEngine.PlayOneShot(a.JinglePath!, AudioBus.MenuSound);
            Console.WriteLine($"Menu jingle: {Path.GetFileName(a.JinglePath)}, playback {handle.Value}");
            Console.WriteLine(
                $"SDL audio: {audioDevice!.Driver}, " +
                $"{audioDevice.HardwareFormat.SampleRate} Hz, " +
                $"{audioDevice.HardwareFormat.Channels} channels, " +
                $"{audioDevice.HardwareBufferFrames} buffer frames");
        }
        var checkerboard = createCheckerboard();
        var texture = application.UploadRgba8(8, 8, checkerboard);
        var frame = new RenderFrame(
            RenderColor.Black,
            [RenderQuad.FromRectangles(
                texture,
                new RenderRectangle(0.25f, 0.18f, 0.5f, 0.64f),
                RenderRectangle.Full,
                RenderColor.White,
                RenderColor.Transparent,
                RenderSampling.Nearest)]);
        var result = application.Run(
            _ => frame,
            static () => { },
            a.FrameLimit,
            a.TickLimit,
            a.ScreenshotPath is null ? null : capture => ScreenshotWriter.Write(a.ScreenshotPath, capture));
        reportTiming(result);
        return 0;
    }

    private static byte[] createCheckerboard()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var bright = ((x / 2) + (y / 2)) % 2 == 0;
                var offset = (y * 8 + x) * 4;
                pixels[offset] = bright ? (byte)246 : (byte)32;
                pixels[offset + 1] = bright ? (byte)156 : (byte)92;
                pixels[offset + 2] = bright ? (byte)58 : (byte)154;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    private static void reportTiming(SdlRunResult result)
    {
        if (result.DroppedTicks > 0)
            Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
    }
}
