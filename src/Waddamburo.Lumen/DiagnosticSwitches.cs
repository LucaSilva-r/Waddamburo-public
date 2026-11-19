namespace Waddamburo.Lumen;

/// <summary>
/// The diagnostic switches this project reads, one place for all of them (docs/development/diagnostics.md lists every
/// project's). Read live, as the call sites always did: Boot sets WADDAMBURO_RENDERER from config.cfg before the
/// renderer reads it.
/// </summary>
internal static class DiagnosticSwitches
{
    /// <summary>WADDAMBURO_HOST_TRACE: 1: every movie-to-game host call on the console.</summary>
    public static bool HostTrace => Environment.GetEnvironmentVariable("WADDAMBURO_HOST_TRACE") == "1";
}
