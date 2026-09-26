namespace Waddamburo.Platform.Sdl;

/// <summary>Elapsed main-thread work for one presented frame.</summary>
public readonly record struct SdlFrameMetrics(
    TimeSpan Interval,
    TimeSpan Input,
    TimeSpan Update,
    TimeSpan Draw,
    int SimulationTicks);
