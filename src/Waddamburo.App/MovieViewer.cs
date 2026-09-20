using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats;
using Waddamburo.Game;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using System.Collections.Immutable;

internal static class MovieViewer
{
    public static int Run(
        string archivePath,
        string movieName,
        int windowWidth,
        int windowHeight,
        int? seekFrame,
        int? frameLimit,
        int? tickLimit,
        string? screenshotPath,
        ImmutableArray<CallbackInvocation> callbackInvocations)
    {
        var archiveLength = new FileInfo(archivePath).Length;
        if (archiveLength > ParserLimits.Default.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Archive size {archiveLength} exceeds the configured {ParserLimits.Default.MaxFileBytes}-byte limit.");
        }
        var archiveBytes = File.ReadAllBytes(archivePath);
        var archive = DdpArchive.Open(archiveBytes);
        var content = LumenMovieContent.Load(archive.OpenMovie(movieName));
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
            debugGpu: false);
        Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
        var textureIds = content.Textures
            .Select(texture => application.UploadRgba8(
                checked((uint)texture.Width),
                checked((uint)texture.Height),
                texture.Rgba8.AsSpan()))
            .ToArray();
        var player = content.CreatePlayer(hostBinding: ViewerHostBinding.Instance);
        if (seekFrame is int requestedFrame)
            player.Seek(requestedFrame);
        foreach (var invocation in callbackInvocations)
        {
            if (!player.TryInvokeCallback(invocation.Name, invocation.Arguments))
                throw new ArgumentException($"Lumen callback '{invocation.Name}' is not registered or could not complete.");
            Console.WriteLine($"Invoked Lumen callback: {invocation.Name}");
        }
        var debugger = frameLimit is null && tickLimit is null
            ? new InteractiveMovieDebugger(player)
            : null;
        RenderFrame createFrame(double interpolationFraction) => LumenRenderFrameAdapter.Compose(
                player.CreateRenderSnapshot((float)interpolationFraction),
                RenderColor.WaddamburoBlue,
                index => index < textureIds.Length
                    ? textureIds[index]
                    : throw new InvalidDataException($"Render snapshot references missing texture {index}."));
        var result = application.Run(
            createFrame,
            keyboard =>
            {
                if (debugger is null)
                    player.Advance(LumenInputAdapter.CreateSnapshot(keyboard));
                else
                    debugger.Tick(keyboard);
            },
            frameLimit,
            tickLimit,
            screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture));
        if (!player.CallbackNames.IsEmpty)
            Console.WriteLine($"Lumen callbacks: {string.Join(", ", player.CallbackNames)}");
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
