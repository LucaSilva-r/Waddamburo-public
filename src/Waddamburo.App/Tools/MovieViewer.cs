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
        run(LumenMovieContent.LoadPackage(directory, Presentation.GameMovieContent.DecodePng), windowWidth, windowHeight, seekFrame,
            frameLimit, tickLimit, screenshotPath, callbackInvocations, inputTimeline);

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
        int? tickLimit, string? screenshotPath, ImmutableArray<CallbackInvocation> callbackInvocations, SdlKeyboardTimeline inputTimeline)
    {
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
        var textureIds = content.Textures
            .Select(texture => application.UploadRgba8(
                checked((uint)texture.Width),
                checked((uint)texture.Height),
                texture.Rgba8.AsSpan()))
            .ToArray();
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
        var readPath = Environment.GetEnvironmentVariable("WADDAMBURO_READ");
        object? lastRead = null;
        var debugger = frameLimit is null && tickLimit is null
            ? new InteractiveMovieDebugger(player)
            : null;
        RenderFrame createFrame(double interpolationFraction) => LumenRenderFrameAdapter.Compose(
                player.CreateRenderSnapshot((float)interpolationFraction),
                RenderColor.Black,
                index => index < textureIds.Length
                    ? textureIds[index]
                    : throw new InvalidDataException($"Render snapshot references missing texture {index}."),
                surface => placeholders.TryGetValue(surface, out var placeholder) ? placeholder : null);
        var simulationTick = 0;
        var treeEvery = int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_TREE_EVERY"), out var every) ? every : 0;
        var treeFilter = Environment.GetEnvironmentVariable("WADDAMBURO_TREE_FILTER")?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var result = application.Run(
            createFrame,
            keyboard =>
            {
                simulationTick++;
                keyboard = inputTimeline.Apply(simulationTick, keyboard);
                foreach (var invocation in callbackInvocations.Where(invocation => invocation.Tick == simulationTick))
                    invoke(invocation);
                if (debugger is null)
                {
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
                else
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
