using System.Collections.Immutable;
using Waddamburo.App.Cli;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Game;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Tools;

internal static class MovieViewer
{
    /// <summary>The viewer on a movie package folder (movie.json + textures) instead of an archive movie.</summary>
    public static int RunPackage(string directory, int windowWidth, int windowHeight, int? seekFrame, int? frameLimit,
        int? tickLimit, string? screenshotPath, ImmutableArray<CallbackInvocation> callbackInvocations, SdlKeyboardTimeline inputTimeline) =>
        run(LumenMovieContent.LoadPackage(directory, Presentation.GameMovieContent.DecodeImage), windowWidth, windowHeight, seekFrame,
            frameLimit, tickLimit, screenshotPath, callbackInvocations, inputTimeline, directory);

    public static int Run(
        string archivePath,
        string movieName,
        int windowWidth,
        int windowHeight,
        int? seekFrame,
        int? frameLimit,
        int? tickLimit,
        string? screenshotPath,
        ImmutableArray<CallbackInvocation> callbackInvocations,
        SdlKeyboardTimeline inputTimeline)
    {
        var archiveLength = new FileInfo(archivePath).Length;
        if (archiveLength > ParserLimits.Default.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Archive size {archiveLength} exceeds the configured {ParserLimits.Default.MaxFileBytes}-byte limit.");
        }
        var archiveBytes = File.ReadAllBytes(archivePath);
        var archive = DdpArchive.Open(archiveBytes);
        return run(LumenMovieContent.Load(archive.OpenMovie(movieName)), windowWidth, windowHeight, seekFrame, frameLimit, tickLimit,
            screenshotPath, callbackInvocations, inputTimeline);
    }

    private static int run(LumenMovieContent content, int windowWidth, int windowHeight, int? seekFrame, int? frameLimit,
        int? tickLimit, string? screenshotPath, ImmutableArray<CallbackInvocation> callbackInvocations, SdlKeyboardTimeline inputTimeline,
        string? package = null)
    {
        var plain = Environment.GetCommandLineArgs().Contains("--plain", StringComparer.Ordinal);
        foreach (var diagnostic in content.Diagnostics)
        {
            var output = diagnostic.Severity is DiagnosticSeverity.Error ? Console.Error : Console.Out;
            output.WriteLine($"{diagnostic.Severity} {diagnostic.Code} at 0x{diagnostic.Offset:X}: {diagnostic.Message}");
        }
        if (content.Diagnostics.Any(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error))
            throw new InvalidDataException("Movie has semantic errors and cannot be presented safely.");

        using var application = new SdlApplication(
            $"Waddamburo — {content.Name}",
            windowWidth,
            windowHeight,
            debugGpu: false,
            resizable: screenshotPath is null,
            highPixelDensity: screenshotPath is null);
        Console.WriteLine($"Renderer: {application.GpuDriver}");
        // Reloads happen on every step of a drag in the editor: a texture file is decoded once, and uploaded once.
        var decoded = new Dictionary<string, (int Width, int Height, byte[] Rgba)?>(StringComparer.Ordinal);
        var uploaded = new Dictionary<byte[], RenderTextureId>(ReferenceEqualityComparer.Instance);
        (int Width, int Height, byte[] Rgba)? decodeOnce(byte[] file)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
            return decoded.TryGetValue(key, out var image) ? image : decoded[key] = Presentation.GameMovieContent.DecodeImage(file);
        }
        RenderTextureId upload(LumenTextureContent texture)
        {
            var pixels = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(texture.Rgba8)!;
            return uploaded.TryGetValue(pixels, out var id) ? id
                : uploaded[pixels] = application.UploadRgba8(checked((uint)texture.Width), checked((uint)texture.Height), pixels);
        }
        if (package is not null)
            content = LumenMovieContent.LoadPackage(package, decodeOnce); // Through the cache, so a reload reuses everything.
        var textureIds = content.Textures.Select(upload).ToArray();
        var player = content.CreatePlayer(hostBinding: ViewerHostBinding.Instance);
        // Diagnostic: WADDAMBURO_FILL_PLACEHOLDERS=don1p,don2p draws those native fills as solid boxes (red, blue, green,
        // ...), 360 px square, so movies that hold host-drawn art (the Dons) show where it goes.
        var placeholders = new Dictionary<LumenNativeSurfaceKey, RenderTextureId>();
        uint[] colours = [0xFF3030FF, 0xFFFF6040, 0xFF40D040, 0xFF20D0F0];
        foreach (var (name, index) in (Environment.GetEnvironmentVariable("WADDAMBURO_FILL_PLACEHOLDERS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select((name, index) => (name, index)))
        {
            var rgba = new byte[8 * 8 * 4];
            for (var pixel = 0; pixel < 64; pixel++)
                BitConverter.TryWriteBytes(rgba.AsSpan(pixel * 4), colours[index % colours.Length]);
            var key = new LumenNativeSurfaceKey($"placeholder:{name}");
            placeholders[key] = application.UploadRgba8(8, 8, rgba);
            player.SetNativeFill(name, key, LumenNativeSurfacePlacement.Centered(360, 360));
        }
        if (seekFrame is int requestedFrame)
            player.Seek(requestedFrame);
        void invoke(CallbackInvocation invocation)
        {
            if (!player.TryInvokeCallback(invocation.Name, invocation.Arguments))
                throw new ArgumentException($"Lumen callback '{invocation.Name}' is not registered or could not complete.");
            Console.WriteLine($"Invoked Lumen callback: {invocation.Name}");
        }
        foreach (var invocation in callbackInvocations.Where(static invocation => invocation.Tick is null))
            invoke(invocation);
        // Dynamic text fields, in the bundled title font (the game uses the player's font).
        using var textFields = new Presentation.TextFieldTextures(application,
            BundledFonts.Extract(Path.Combine(Path.GetDirectoryName(ReleaseConsole.CrashFolder)!, "fonts")).Title);
        var readPath = Environment.GetEnvironmentVariable("WADDAMBURO_READ");
        object? lastRead = null;
        var debugger = frameLimit is null && tickLimit is null
            ? new InteractiveMovieDebugger(player)
            : null;
        // The movie inspector (tree, clip state, callbacks) over an interactive window; --plain leaves the bare movie.
        // --inspect[=clip] shows it in a --ticks / --screenshot run too, with that clip selected.
        var inspect = Environment.GetCommandLineArgs().FirstOrDefault(static each => each.StartsWith("--inspect", StringComparison.Ordinal));
        var document = package is null || plain ? null : new Waddamburo.Editor.MovieDocument(package);
        using var inspector = plain || (debugger is null && inspect is null) ? null
            : new Waddamburo.Editor.MovieInspectorSession(application, () => player, content.Name, gameData: package is null,
                select: inspect?.Split('=', 2) is [_, var clip] ? clip : null, document: document);
        // A package open in a window is reloaded when one of its files is saved (scripts, movie.json, textures): the
        // movie starts again from its first frame. A mistake is printed and the last good version keeps playing.
        DateTime stamp() => Directory.EnumerateFiles(package!, "*", SearchOption.AllDirectories).Max(File.GetLastWriteTimeUtc);
        var loaded = package is null || (debugger is null && inspector is null) ? default : stamp();
        void reload()
        {
            var now = stamp();
            if (now == loaded)
                return;
            loaded = now;
            try
            {
                var fresh = LumenMovieContent.LoadPackage(package!, decodeOnce);
                if (fresh.Diagnostics.FirstOrDefault(static each => each.Severity is DiagnosticSeverity.Error) is { } problem)
                    throw new InvalidDataException($"{problem.Code}: {problem.Message}");
                var freshPlayer = fresh.CreatePlayer(hostBinding: ViewerHostBinding.Instance);
                // ponytail: textures no longer used stay on the GPU until the viewer closes.
                textureIds = [.. fresh.Textures.Select(upload)];
                player = freshPlayer;
                if (debugger is not null)
                    debugger.Player = player;
            }
            catch (Exception exception) when (exception is FormatException or IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"Not reloaded: {exception.Message}");
            }
        }
        if (document is not null)
            document.Changed += reload; // An edit shows at once, not at the next check for saved files.
        RenderFrame createFrame(double interpolationFraction) => LumenRenderFrameAdapter.Compose(
                player.CreateRenderSnapshot((float)interpolationFraction),
                RenderColor.Black,
                index => index < textureIds.Length
                    ? textureIds[index]
                    : throw new InvalidDataException($"Render snapshot references missing texture {index}."),
                surface => placeholders.TryGetValue(surface, out var placeholder) ? placeholder : textFields.Resolve(surface));
        var simulationTick = 0;
        var treeEvery = int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_TREE_EVERY"), out var every) ? every : 0;
        var treeFilter = Environment.GetEnvironmentVariable("WADDAMBURO_TREE_FILTER")?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var result = application.Run(
            createFrame,
            keyboard =>
            {
                simulationTick++;
                if (package is not null && debugger is not null && simulationTick % 30 == 0)
                    reload();
                keyboard = inputTimeline.Apply(simulationTick, keyboard);
                foreach (var invocation in callbackInvocations.Where(invocation => invocation.Tick == simulationTick))
                    invoke(invocation);
                if (debugger is null)
                {
                    if (inspector?.TakeTick() != false)
                        player.Advance(LumenInputAdapter.CreateSnapshot(keyboard));
                    // Diagnostic: WADDAMBURO_TREE_EVERY=N prints the display list every N ticks (lines containing
                    // WADDAMBURO_TREE_FILTER, '|'-separated, when set).
                    if (treeEvery > 0 && simulationTick % treeEvery == 0)
                        foreach (var line in player.DescribeDisplayList().Where(line => treeFilter.Length == 0 || treeFilter.Any(line.Contains)))
                            Console.WriteLine($"[tree {simulationTick}] {line}");
                    // Diagnostic: WADDAMBURO_READ=main.isAllEnd prints that script value when it changes.
                    if (readPath is not null && player.ReadScriptValue(readPath) is var read && !Equals(read, lastRead))
                    {
                        Console.WriteLine($"[read] tick {simulationTick}: {readPath} = {read ?? "null"}");
                        lastRead = read;
                    }
                }
                else if (inspector?.TakeTick() != false)
                    debugger.Tick(keyboard);
            },
            frameLimit,
            tickLimit,
            screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture));
        if (!player.Callbacks.IsEmpty)
        {
            Console.WriteLine("Lumen callbacks: " + string.Join(", ", player.Callbacks.Select(callback =>
                $"{callback.Name}({string.Join(", ", callback.Parameters)})")));
        }
        foreach (var diagnostic in player.Diagnostics)
        {
            Console.WriteLine(
                $"{diagnostic.Severity} {diagnostic.Code} at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
        }
        if (result.DroppedTicks > 0)
            Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
        return result.RenderedFrames;
    }
}
