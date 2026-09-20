using System.Collections.Immutable;
using System.Globalization;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Game;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

internal static class SceneViewer
{
    private const int MaxSceneLayers = 256;

    public static int Run(
        string scenePath,
        string assetRoot,
        int windowWidth,
        int windowHeight,
        int? frameLimit,
        int? tickLimit,
        string? screenshotPath)
    {
        var entries = readScene(scenePath);
        var root = Path.GetFullPath(assetRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Asset root does not exist: {root}");

        var archives = new Dictionary<string, DdpArchive>(StringComparer.Ordinal);
        var loaded = ImmutableArray.CreateBuilder<LoadedLayer>(entries.Length);
        uint textureOffset = 0;
        foreach (var entry in entries)
        {
            var archivePath = resolveArchive(root, entry.ArchivePath);
            if (!archives.TryGetValue(archivePath, out var archive))
            {
                var archiveLength = new FileInfo(archivePath).Length;
                if (archiveLength > ParserLimits.Default.MaxFileBytes)
                {
                    throw new InvalidDataException(
                        $"Archive size {archiveLength} exceeds the configured {ParserLimits.Default.MaxFileBytes}-byte limit: {entry.ArchivePath}");
                }
                archive = DdpArchive.Open(File.ReadAllBytes(archivePath));
                archives.Add(archivePath, archive);
            }

            var content = LumenMovieContent.Load(archive.OpenMovie(entry.MovieName));
            foreach (var diagnostic in content.Diagnostics)
            {
                var output = diagnostic.Severity is DiagnosticSeverity.Error ? Console.Error : Console.Out;
                output.WriteLine(
                    $"{entry.MovieName}: {diagnostic.Severity} {diagnostic.Code} at 0x{diagnostic.Offset:X}: {diagnostic.Message}");
            }
            if (content.Diagnostics.Any(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error))
                throw new InvalidDataException($"Scene movie '{entry.MovieName}' has semantic errors.");

            var textureCount = checked((uint)content.Textures.Length);
            loaded.Add(new LoadedLayer(entry, content, textureOffset, textureCount));
            textureOffset = checked(textureOffset + textureCount);
        }

        using var application = new SdlApplication(
            $"Waddamburo — {Path.GetFileNameWithoutExtension(scenePath)}",
            windowWidth,
            windowHeight,
            debugGpu: false);
        Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
        var textureIds = loaded
            .SelectMany(layer => layer.Content.Textures)
            .Select(texture => application.UploadRgba8(
                checked((uint)texture.Width),
                checked((uint)texture.Height),
                texture.Rgba8.AsSpan()))
            .ToArray();
        var scene = new LumenScenePlayer(
            1280,
            720,
            loaded.Select(layer => new LumenSceneLayer(
                layer.Content.CreatePlayer(hostBinding: ViewerHostBinding.Instance),
                layer.Entry.Transform,
                layer.TextureOffset,
                layer.TextureCount)));
        RenderFrame createFrame(double interpolationFraction) => LumenRenderFrameAdapter.Compose(
                scene.CreateRenderSnapshot((float)interpolationFraction),
                RenderColor.WaddamburoBlue,
                index => index < textureIds.Length
                    ? textureIds[index]
                    : throw new InvalidDataException($"Scene snapshot references missing texture {index}."));
        var result = application.Run(
            createFrame,
            scene.Advance,
            frameLimit,
            tickLimit,
            screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture));
        for (var index = 0; index < scene.Layers.Length; index++)
        {
            var callbacks = scene.Layers[index].Player.CallbackNames;
            if (!callbacks.IsEmpty)
                Console.WriteLine($"{loaded[index].Entry.MovieName}: Lumen callbacks: {string.Join(", ", callbacks)}");
            foreach (var diagnostic in scene.Layers[index].Player.Diagnostics)
            {
                Console.WriteLine(
                    $"{loaded[index].Entry.MovieName}: {diagnostic.Severity} {diagnostic.Code} "
                    + $"at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
            }
        }
        if (result.DroppedTicks > 0)
            Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
        return result.RenderedFrames;
    }

    private static ImmutableArray<SceneEntry> readScene(string scenePath)
    {
        var entries = ImmutableArray.CreateBuilder<SceneEntry>();
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(scenePath))
        {
            lineNumber++;
            var comment = rawLine.IndexOf('#');
            var line = (comment >= 0 ? rawLine[..comment] : rawLine).Trim();
            if (line.Length == 0)
                continue;
            if (entries.Count >= MaxSceneLayers)
                throw new InvalidDataException($"Scene exceeds the {MaxSceneLayers}-layer limit.");

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is not (2 or 4 or 5))
            {
                throw new InvalidDataException(
                    $"{scenePath}:{lineNumber}: expected <archive> <movie> [x y [scale]].");
            }
            var x = fields.Length >= 4 ? parseFinite(fields[2], scenePath, lineNumber) : 0f;
            var y = fields.Length >= 4 ? parseFinite(fields[3], scenePath, lineNumber) : 0f;
            var scale = fields.Length == 5 ? parseFinite(fields[4], scenePath, lineNumber) : 1f;
            if (scale == 0)
                throw new InvalidDataException($"{scenePath}:{lineNumber}: scale must be non-zero.");
            entries.Add(new SceneEntry(
                fields[0],
                fields[1],
                new LumenMatrix(scale, 0, 0, scale, x, y)));
        }
        if (entries.Count == 0)
            throw new InvalidDataException("Scene contains no movie layers.");
        return entries.ToImmutable();
    }

    private static string resolveArchive(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Scene archive path must be relative: {relativePath}");
        var candidate = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Scene archive path escapes the asset root: {relativePath}");
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Scene archive was not found: {relativePath}", candidate);
        return candidate;
    }

    private static float parseFinite(string value, string scenePath, int lineNumber)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || !float.IsFinite(result))
        {
            throw new InvalidDataException($"{scenePath}:{lineNumber}: '{value}' is not a finite number.");
        }
        return result;
    }

    private sealed record SceneEntry(string ArchivePath, string MovieName, LumenMatrix Transform);

    private sealed record LoadedLayer(
        SceneEntry Entry,
        LumenMovieContent Content,
        uint TextureOffset,
        uint TextureCount);
}
