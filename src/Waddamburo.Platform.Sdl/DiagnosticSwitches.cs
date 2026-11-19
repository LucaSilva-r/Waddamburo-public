namespace Waddamburo.Platform.Sdl;

/// <summary>
/// The diagnostic switches this project reads, one place for all of them (docs/development/diagnostics.md lists every
/// project's). Read live, as the call sites always did: Boot sets WADDAMBURO_RENDERER from config.cfg before the
/// renderer reads it.
/// </summary>
internal static class DiagnosticSwitches
{
    /// <summary>WADDAMBURO_GAMEPLAY_FRAME_PROFILE: 1: per-frame timings of gameplay frames to a file.</summary>
    public static bool GameplayFrameProfile => Environment.GetEnvironmentVariable("WADDAMBURO_GAMEPLAY_FRAME_PROFILE") == "1";

    /// <summary>WADDAMBURO_HITCH_TRACE: 1: why a slow frame was slow (input, update, render, GC, the chart clock's steps).</summary>
    public static bool HitchTrace => Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";

    /// <summary>WADDAMBURO_PRESENT_MODE: immediate or mailbox: the swap chain's present mode.</summary>
    public static string? PresentMode => Environment.GetEnvironmentVariable("WADDAMBURO_PRESENT_MODE");

    /// <summary>WADDAMBURO_PROFILE: 1: load and boot timings, full exception traces.</summary>
    public static bool Profile => Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";

    /// <summary>WADDAMBURO_RENDERER: The renderer (vulkan, gles, opengl, d3d11, metal); config.cfg's renderer sets it when unset.</summary>
    public static string? Renderer => Environment.GetEnvironmentVariable("WADDAMBURO_RENDERER");
}
