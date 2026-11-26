namespace Waddamburo.Skins.Green;

/// <summary>The diagnostic switches Green's skin reads (docs/development/diagnostics.md lists every project's).</summary>
internal static class DiagnosticSwitches
{
    /// <summary>WADDAMBURO_ICON_GALLERY: 1: Song Select shows every option icon on its boards.</summary>
    public static bool IconGallery => Environment.GetEnvironmentVariable("WADDAMBURO_ICON_GALLERY") == "1";

    /// <summary>WADDAMBURO_NO_GENRE_PATCH: 1: Song Select's movie runs without the genre patch.</summary>
    public static bool NoGenrePatch => Environment.GetEnvironmentVariable("WADDAMBURO_NO_GENRE_PATCH") == "1";

    /// <summary>WADDAMBURO_SCROLL_TRACE: 1: Song Select's scroll steps on the console.</summary>
    public static bool ScrollTrace => Environment.GetEnvironmentVariable("WADDAMBURO_SCROLL_TRACE") == "1";
}
