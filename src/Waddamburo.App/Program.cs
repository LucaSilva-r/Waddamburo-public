using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
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
    var gameDataRoot = parseOption(args, "--game-data=");
    var assetRoot = parseOption(args, "--asset-root=");
    var tjaRoot = parseOption(args, "--tja-root=");
    var fontPath = parseOption(args, "--font=");
    var audioPath = parseOption(args, "--play-audio=");
    var jinglePath = parseOption(args, "--play-jingle=");
    var soundRoot = parseOption(args, "--sound-root=");
    var donRoot = parseOption(args, "--don-root=");
    var soundBankProbe = parseOption(args, "--probe-sound-bank=");
    var positionalRoots = args.Where(static argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (positionalRoots.Length > 1)
        throw new ArgumentException("Normal boot accepts at most one positional game-data directory.");
    if (gameDataRoot is not null && positionalRoots.Length != 0)
        throw new ArgumentException("Supply the game-data directory either positionally or with --game-data, not both.");
    gameDataRoot ??= positionalRoots.SingleOrDefault();
    if (audioPath is not null && jinglePath is not null)
        throw new ArgumentException("--play-audio and --play-jingle cannot be combined.");
    var entrySongSelect = args.Contains("--entry-song-select", StringComparer.Ordinal);
    var hasDiagnosticContent = entrySongSelect
        || archivePath is not null
        || movieName is not null
        || scenePath is not null
        || assetRoot is not null
        || tjaRoot is not null
        || audioPath is not null
        || jinglePath is not null
        || soundRoot is not null
        || donRoot is not null
        || soundBankProbe is not null;
    var normalBoot = gameDataRoot is not null || !hasDiagnosticContent;
    if (normalBoot)
    {
        if (archivePath is not null || movieName is not null || scenePath is not null
            || assetRoot is not null || tjaRoot is not null || audioPath is not null
            || soundRoot is not null || donRoot is not null || entrySongSelect)
        {
            throw new ArgumentException("--game-data cannot be combined with diagnostic content options.");
        }
        if (seekFrame is not null || !callbackInvocations.IsEmpty)
            throw new ArgumentException("--seek-frame and --invoke are unavailable during normal boot.");

        var layout = GameDataLayout.Resolve(gameDataRoot ?? Directory.GetCurrentDirectory(), fontPath);
        Console.WriteLine($"Game data: {layout.Root}");
        Console.WriteLine($"Title font: {layout.FontPath}");
        EntrySongSelectFlow.Run(
            layout.LumenRoot,
            layout.DonRoot,
            windowSize.Width,
            windowSize.Height,
            frameLimit,
            tickLimit,
            screenshotPath,
            inputTimeline,
            layout.TjaRoot,
            layout.FontPath,
            jinglePath,
            layout.SoundRoot);
        return 0;
    }
    if (soundBankProbe is not null)
    {
        if (archivePath is not null || movieName is not null || scenePath is not null
            || assetRoot is not null || tjaRoot is not null || audioPath is not null
            || jinglePath is not null || soundRoot is not null || donRoot is not null || entrySongSelect
            || positionalRoots.Length != 0)
        {
            throw new ArgumentException("--probe-sound-bank cannot be combined with game or content options.");
        }
        SoundBankProbe.Run(soundBankProbe, windowSize.Width, windowSize.Height);
        return 0;
    }
    if (entrySongSelect)
    {
        if (audioPath is not null)
            throw new ArgumentException("--play-audio cannot be combined with --entry-song-select.");
        if (assetRoot is null)
            throw new ArgumentException("--entry-song-select requires --asset-root.");
        if (tjaRoot is null || fontPath is null)
            throw new ArgumentException("--entry-song-select requires --tja-root and --font.");
        if (archivePath is not null || movieName is not null || scenePath is not null)
            throw new ArgumentException("--entry-song-select cannot be combined with movie or scene inputs.");
        if (seekFrame is not null || !callbackInvocations.IsEmpty)
            throw new ArgumentException("--seek-frame and --invoke are unavailable for --entry-song-select.");
        EntrySongSelectFlow.Run(
            assetRoot,
            donRoot,
            windowSize.Width,
            windowSize.Height,
            frameLimit,
            tickLimit,
            screenshotPath,
            inputTimeline,
            tjaRoot,
            fontPath,
            jinglePath,
            soundRoot);
        return 0;
    }
    if (soundRoot is not null)
        throw new ArgumentException("--sound-root currently requires --entry-song-select.");
    if (donRoot is not null)
        throw new ArgumentException("--don-root currently requires --entry-song-select.");
    if (scenePath is not null || assetRoot is not null)
    {
        if (audioPath is not null || jinglePath is not null)
            throw new ArgumentException("Audio diagnostics cannot be combined with --scene.");
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
        if (audioPath is not null || jinglePath is not null)
            throw new ArgumentException("Audio diagnostics cannot be combined with --archive/--movie.");
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
    using var audioDevice = audioPath is null && jinglePath is null ? null : new SdlAudioDevice();
    using var music = audioPath is null ? null : new StreamingMusicPlayer(audioDevice!, audioPath);
    using var audioEngine = jinglePath is null ? null : new AudioEngine(audioDevice!);
    if (music is not null)
    {
        music.Play();
        Console.WriteLine(
            $"Audio: {Path.GetFileName(audioPath)}, {music.Info.SampleRate} Hz, {music.Info.Channels} channels");
        Console.WriteLine(
            $"SDL audio: {audioDevice!.Driver}, " +
            $"{audioDevice.HardwareFormat.SampleRate} Hz, " +
            $"{audioDevice.HardwareFormat.Channels} channels, " +
            $"{audioDevice.HardwareBufferFrames} buffer frames");
    }
    if (audioEngine is not null)
    {
        var handle = audioEngine.PlayOneShot(jinglePath!, AudioBus.MenuSound);
        Console.WriteLine($"Menu jingle: {Path.GetFileName(jinglePath)}, playback {handle.Value}");
        Console.WriteLine(
            $"SDL audio: {audioDevice!.Driver}, " +
            $"{audioDevice.HardwareFormat.SampleRate} Hz, " +
            $"{audioDevice.HardwareFormat.Channels} channels, " +
            $"{audioDevice.HardwareBufferFrames} buffer frames");
    }
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
