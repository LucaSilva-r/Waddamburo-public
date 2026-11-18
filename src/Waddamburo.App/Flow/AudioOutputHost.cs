using Waddamburo.Game.Flow;
using Waddamburo.Game.Online;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// The audio output the settings ask for (shared, WASAPI exclusive, ASIO), with its fallbacks, its live switch, and the
/// toasts when it fails or falls back.
/// </summary>
internal sealed class AudioOutputHost(GameShell shell) : IDisposable
{
    // The output's failure (unplugged, the driver gone) was toasted once.
    private bool _failureShown;

    /// <summary>The open output (null: none, headless or not needed).</summary>
    public IAudioOutput? Device { get; private set; }

    /// <summary>The audio output settings the running output opened with (after any fallback).</summary>
    public ArcadeSettings Running { get; private set; } = new();

    /// <summary>Output problems toast from now on; opens the first output when one is <paramref name="needed"/>.</summary>
    public IAudioOutput? Start(bool needed)
    {
        AudioNotices.Raised += problem;
        return Device = needed ? open() : null;
    }

    /// <summary>
    /// Opens the new output in place of the running one (the caller has stopped everything). False when the new output runs
    /// at another sample rate than the mix: the caller restarts.
    /// </summary>
    public bool Switch(AudioEngine audio)
    {
        var previous = Running;
        var switched = audio.SwitchOutput(() => open(previous));
        Device = audio.Output;
        _failureShown = false;
        if (switched)
            Console.WriteLine("Audio: output switched.");
        else
        {
            Console.WriteLine("Audio: the new output runs at another sample rate, restarting.");
            shell.Sync.Notices.AddLocal(Strings.T("audio.rate_restart"), NoticeSeverity.Warning);
        }
        return switched;
    }

    /// <summary>Each tick: an output that stopped (unplugged, the driver gone) is toasted once.</summary>
    public void CheckFailure(AudioEngine? audio)
    {
        if (_failureShown || audio?.Failure is not { } failure)
            return;
        _failureShown = true;
        Console.Error.WriteLine($"Audio: the output stopped. {failure.Message}");
        shell.Sync.Notices.AddLocal(Strings.T("audio.output_failed", failure.Message), NoticeSeverity.Warning);
    }

    // An output problem the player should hear of, as a toast (from any thread: the notice board locks).
    private void problem(AudioProblem problem, string detail) => shell.Sync.Notices.AddLocal(problem switch
    {
        AudioProblem.DeviceMissing => Strings.T("audio.device_missing", detail),
        AudioProblem.AsioDriverMissing => Strings.T("audio.asio_driver_missing", detail),
        AudioProblem.AsioOutputsMissing => Strings.T("audio.asio_outputs_missing", detail),
        _ => detail.Split('|') is [var frames, var ms] ? Strings.T("audio.period_fallback", frames, ms) : detail,
    }, NoticeSeverity.Warning);

    // Opens the output the settings ask for. When it fails: the one that ran before (a live switch; the settings' audio
    // go back to it, saved), else shared output; a toast says which and why.
    private IAudioOutput open(ArcadeSettings? previous = null)
    {
        Home.HomeMenu.OutputInUse = null;
        var arcade = shell.Arcade;
        if (tryOpen(arcade, out var output, out var problem))
        {
            Running = arcade;
            return output;
        }
        var mode = Strings.T(arcade.AudioOutput == AudioOutputMode.Asio ? "settings.audio_output.asio" : "settings.audio_output.exclusive");
        if (previous is not null && previous.AudioOutputSettings != arcade.AudioOutputSettings && tryOpen(previous, out output, out _))
        {
            shell.Arcade = arcade with
            {
                AudioOutput = previous.AudioOutput, AudioDevice = previous.AudioDevice, AudioBufferFrames = previous.AudioBufferFrames,
                AudioExclusivePeriod = previous.AudioExclusivePeriod, AsioDriver = previous.AsioDriver,
                AsioBufferFrames = previous.AsioBufferFrames, AsioOutputPair = previous.AsioOutputPair,
            };
            if (shell.Options.ArcadePath is { } path)
                try
                {
                    ArcadeSettings.SaveMenuSettings(path, shell.Arcade);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
                }
            Running = previous;
            shell.Sync.Notices.AddLocal(Strings.T("audio.restored", mode, problem), NoticeSeverity.Warning);
            return output;
        }
        shell.Sync.Notices.AddLocal(Strings.T("audio.fallback_shared", mode, problem), NoticeSeverity.Warning);
        Running = arcade with { AudioOutput = AudioOutputMode.Shared };
        return new SdlAudioDevice(requestedBufferFrames: arcade.AudioBufferFrames, deviceName: arcade.AudioDevice);
    }

    // Opens the output settings asks for; false (with why) when exclusive or ASIO output can't open. Shared opens or throws.
    private static bool tryOpen(ArcadeSettings settings, out IAudioOutput output, out string problem)
    {
        problem = "";
        if (settings.AudioOutput == AudioOutputMode.Asio && OperatingSystem.IsWindows())
        {
            try
            {
                var asio = new AsioOutput(settings.AsioDriver, settings.AsioBufferFrames, settings.AsioOutputPair);
                Home.HomeMenu.AsioOutputs = asio.OutputCount;
                var (setting, driver, frames, rate) = (settings.AsioBufferFrames, settings.AsioDriver, asio.HardwareBufferFrames, asio.Format.SampleRate);
                Home.HomeMenu.OutputInUse = () => (AudioOutputMode.Asio, setting, driver, frames, rate);
                Console.WriteLine($"Audio: {asio.Driver}, {asio.HardwareBufferFrames} frames "
                    + $"({1000.0 * asio.HardwareBufferFrames / asio.Format.SampleRate:0.##} ms) at {asio.Format.SampleRate} Hz, "
                    + $"output latency {1000.0 * asio.OutputLatencyFrames / asio.Format.SampleRate:0.#} ms, "
                    + $"outputs {asio.OutputPair * 2 + 1}-{asio.OutputPair * 2 + 2} of {asio.OutputCount}.");
                output = asio;
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                Console.Error.WriteLine($"Audio: ASIO unavailable. {exception.Message}");
                (output, problem) = (null!, exception.Message);
                return false;
            }
        }
        if (settings.AudioOutput == AudioOutputMode.Exclusive && OperatingSystem.IsWindows())
        {
            try
            {
                var exclusive = new WasapiExclusiveOutput(periodFrames: settings.AudioExclusivePeriod, deviceName: settings.AudioDevice);
                var (setting, device, frames, rate) = (settings.AudioExclusivePeriod, settings.AudioDevice, exclusive.HardwareBufferFrames, exclusive.Format.SampleRate);
                Home.HomeMenu.OutputInUse = () => (AudioOutputMode.Exclusive, setting, device, frames, rate);
                Console.WriteLine($"Audio: WASAPI exclusive, {exclusive.HardwareBufferFrames} frames "
                    + $"({1000.0 * exclusive.HardwareBufferFrames / exclusive.Format.SampleRate:0.##} ms), {exclusive.Driver}.");
                output = exclusive;
                return true;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine($"Audio: exclusive output unavailable. {exception.Message}");
                (output, problem) = (null!, exception.Message);
                return false;
            }
        }
        output = new SdlAudioDevice(requestedBufferFrames: settings.AudioBufferFrames, deviceName: settings.AudioDevice);
        return true;
    }

    public void Dispose()
    {
        Device?.Dispose();
        AudioNotices.Raised -= problem;
    }
}
