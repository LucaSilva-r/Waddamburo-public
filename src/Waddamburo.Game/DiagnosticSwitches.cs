namespace Waddamburo.Game;

/// <summary>
/// The diagnostic switches this project reads, one place for all of them (docs/development/diagnostics.md lists every
/// project's). Read live, as the call sites always did: Boot sets WADDAMBURO_RENDERER from config.cfg before the
/// renderer reads it.
/// </summary>
internal static class DiagnosticSwitches
{
    /// <summary>WADDAMBURO_PROFILE: 1: load and boot timings, full exception traces.</summary>
    public static bool Profile => Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";

}
