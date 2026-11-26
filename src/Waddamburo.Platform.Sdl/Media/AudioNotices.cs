namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Something about the audio output the player should hear of (the game shows it as a toast).</summary>
public enum AudioProblem
{
    /// <summary>The chosen playback device isn't connected: the default plays (detail: its name).</summary>
    DeviceMissing,
    /// <summary>The chosen ASIO driver isn't installed: the first one plays (detail: its name).</summary>
    AsioDriverMissing,
    /// <summary>The ASIO driver has no such outputs: 1-2 play (detail: the outputs asked for).</summary>
    AsioOutputsMissing,
    /// <summary>The exclusive device couldn't keep up at its minimum period: its default plays (detail: frames, ms).</summary>
    ExclusivePeriodFallback,
}

/// <summary>The outputs' problems, raised on whatever thread found them (the audio thread too).</summary>
public static class AudioNotices
{
    public static event Action<AudioProblem, string>? Raised;

    internal static void Raise(AudioProblem problem, string detail) => Raised?.Invoke(problem, detail);
}
