using Waddamburo.Platform.Sdl;

try
{
    var frameLimit = parseFrameLimit(args);
    using var application = new SdlApplication("Waddamburo", 1280, 720, debugGpu: false);
    Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
    application.Run(frameLimit);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int? parseFrameLimit(string[] arguments)
{
    const string prefix = "--frames=";
    var option = arguments.SingleOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
    if (option is null)
        return null;
    return int.TryParse(option.AsSpan(prefix.Length), out var value) && value >= 0
        ? value
        : throw new ArgumentException("--frames must be a non-negative integer.");
}
