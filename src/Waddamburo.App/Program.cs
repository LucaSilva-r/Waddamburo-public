using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

try
{
    var frameLimit = parseFrameLimit(args);
    var tickLimit = parseLimit(args, "--ticks=");
    var seekFrame = parseLimit(args, "--seek-frame=");
    var windowSize = parseWindowSize(args);
    var screenshotPath = parseOption(args, "--screenshot=");
    var callbackInvocations = CallbackInvocation.Parse(args);
    var inputTimeline = InputPulseParser.Parse(args);
    if (screenshotPath is not null)
    {
        if (frameLimit is null && tickLimit is null)
            frameLimit = 1;
        if (frameLimit == 0)
            throw new ArgumentException("--screenshot requires at least one rendered frame.");
    }
    var archivePath = parseOption(args, "--archive=");
    var movieName = parseOption(args, "--movie=");
    var scenePath = parseOption(args, "--scene=");
    var assetRoot = parseOption(args, "--asset-root=");
    if (scenePath is not null || assetRoot is not null)
    {
        if (scenePath is null || assetRoot is null)
            throw new ArgumentException("--scene and --asset-root must be supplied together.");
        if (archivePath is not null || movieName is not null)
            throw new ArgumentException("Scene options cannot be combined with --archive or --movie.");
        if (seekFrame is not null)
            throw new ArgumentException("--seek-frame currently requires a single --archive/--movie input.");
        if (!callbackInvocations.IsEmpty)
            throw new ArgumentException("--invoke currently requires a single --archive/--movie input.");
        SceneViewer.Run(
            scenePath,
            assetRoot,
            windowSize.Width,
            windowSize.Height,
            frameLimit,
            tickLimit,
            screenshotPath,
            inputTimeline);
        return 0;
    }
    if (archivePath is not null || movieName is not null)
    {
        if (archivePath is null || movieName is null)
            throw new ArgumentException("--archive and --movie must be supplied together.");
        MovieViewer.Run(
            archivePath,
            movieName,
            windowSize.Width,
            windowSize.Height,
            seekFrame,
            frameLimit,
            tickLimit,
            screenshotPath,
            callbackInvocations,
            inputTimeline);
        return 0;
    }
    if (seekFrame is not null)
        throw new ArgumentException("--seek-frame requires --archive and --movie.");
    if (!callbackInvocations.IsEmpty)
        throw new ArgumentException("--invoke requires --archive and --movie.");
    if (!inputTimeline.IsEmpty)
        throw new ArgumentException("--press requires --archive/--movie or --scene/--asset-root.");
    using var application = new SdlApplication(
        "Waddamburo",
        windowSize.Width,
        windowSize.Height,
        debugGpu: false,
        resizable: screenshotPath is null,
        highPixelDensity: screenshotPath is null);
    Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
    var checkerboard = createCheckerboard();
    var texture = application.UploadRgba8(8, 8, checkerboard);
    var frame = new RenderFrame(
        RenderColor.WaddamburoBlue,
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
        frameLimit,
        tickLimit,
        screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture));
    reportTiming(result);
    return 0;
}

catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static byte[] createCheckerboard()
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

static int? parseFrameLimit(string[] arguments)
    => parseLimit(arguments, "--frames=");

static (int Width, int Height) parseWindowSize(string[] arguments)
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

static int? parseLimit(string[] arguments, string prefix)
{
    var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
    if (option is null)
        return null;
    return int.TryParse(option.AsSpan(prefix.Length), out var value) && value >= 0
        ? value
        : throw new ArgumentException($"{prefix[..^1]} must be a non-negative integer.");
}

static void reportTiming(SdlRunResult result)
{
    if (result.DroppedTicks > 0)
        Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
}

static string? parseOption(string[] arguments, string prefix)
{
    var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
    if (option is null)
        return null;
    var value = option[prefix.Length..];
    return value.Length > 0 ? value : throw new ArgumentException($"{prefix} requires a value.");
}
