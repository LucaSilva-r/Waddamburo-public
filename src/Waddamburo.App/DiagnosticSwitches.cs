namespace Waddamburo.App;

/// <summary>
/// The diagnostic switches this project reads, one place for all of them (docs/development/diagnostics.md lists every
/// project's). Read live, as the call sites always did: Boot sets WADDAMBURO_RENDERER from config.cfg before the
/// renderer reads it.
/// </summary>
internal static class DiagnosticSwitches
{
    /// <summary>WADDAMBURO_AI_TRACE: AI battle: each section's decisions and scores on the console.</summary>
    public static bool AiTrace => Environment.GetEnvironmentVariable("WADDAMBURO_AI_TRACE") == "1";

    /// <summary>WADDAMBURO_BENCHMARK_ATTRACT: --benchmark: seconds of attract loop before the song (default 180).</summary>
    public static string? BenchmarkAttract => Environment.GetEnvironmentVariable("WADDAMBURO_BENCHMARK_ATTRACT");

    /// <summary>WADDAMBURO_BOOT_FAIL: Boot fails at the named check (BootChecks), to see its error screen.</summary>
    public static string? BootFail => Environment.GetEnvironmentVariable("WADDAMBURO_BOOT_FAIL");

    /// <summary>WADDAMBURO_CRASH_AT: The game throws at this tick, to see its crash screen.</summary>
    public static string? CrashAt => Environment.GetEnvironmentVariable("WADDAMBURO_CRASH_AT");

    /// <summary>WADDAMBURO_CUSTOM_TJA: 1: the custom TJA library in arcade mode too.</summary>
    public static bool CustomTja => Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1";

    /// <summary>WADDAMBURO_DECODE_TEXTURES: 1: scene textures decoded to RGBA instead of kept compressed.</summary>
    public static string? DecodeTextures => Environment.GetEnvironmentVariable("WADDAMBURO_DECODE_TEXTURES");

    /// <summary>WADDAMBURO_DON_COSTUME: Don-chan's costume at start (slot:id list).</summary>
    public static string? DonCostume => Environment.GetEnvironmentVariable("WADDAMBURO_DON_COSTUME");

    /// <summary>WADDAMBURO_DON_PUCHI: This puchi chara beside player 1 at start.</summary>
    public static string? DonPuchi => Environment.GetEnvironmentVariable("WADDAMBURO_DON_PUCHI");

    /// <summary>WADDAMBURO_DRUM_TRACE: 1: every drum hit's judgement on the console.</summary>
    public static bool DrumTrace => Environment.GetEnvironmentVariable("WADDAMBURO_DRUM_TRACE") == "1";

    /// <summary>WADDAMBURO_DUMP_TREE: The active scene's display lists printed at this tick.</summary>
    public static string? DumpTree => Environment.GetEnvironmentVariable("WADDAMBURO_DUMP_TREE");

    /// <summary>WADDAMBURO_FILL_PLACEHOLDERS: Movie viewer: native fills shown as placeholders (name=index,...).</summary>
    public static string? FillPlaceholders => Environment.GetEnvironmentVariable("WADDAMBURO_FILL_PLACEHOLDERS");

    /// <summary>WADDAMBURO_HITCH_TRACE: 1: why a slow frame was slow (input, update, render, GC, the chart clock's steps).</summary>
    public static bool HitchTrace => Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";

    /// <summary>WADDAMBURO_INPUT_TRACE: 1: presses printed in --press format (KEY@tick).</summary>
    public static bool InputTrace => Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";

    /// <summary>WADDAMBURO_NAME: Player 1's name board shows this name.</summary>
    public static string? Name => Environment.GetEnvironmentVariable("WADDAMBURO_NAME");

    /// <summary>WADDAMBURO_ONLINE_ECHO: Online play alone: your own hits come back as the partner's lane, this many ms later.</summary>
    public static string? OnlineEcho => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_ECHO");

    /// <summary>WADDAMBURO_ONLINE_LAN: 1: LAN addresses offered to strangers too.</summary>
    public static bool OnlineLan => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_LAN") == "1";

    /// <summary>WADDAMBURO_ONLINE_RELAY_ONLY: 1: online play through the server's relay only, no direct link.</summary>
    public static bool OnlineRelayOnly => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_RELAY_ONLY") == "1";

    /// <summary>WADDAMBURO_ONLINE_SERVER: The online rooms' server instead of the settings' one.</summary>
    public static string? OnlineServer => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_SERVER");

    /// <summary>WADDAMBURO_ONLINE_TOKEN: The online rooms' login token instead of the player's.</summary>
    public static string? OnlineToken => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_TOKEN");

    /// <summary>WADDAMBURO_PROFILE: 1: load and boot timings, full exception traces.</summary>
    public static bool Profile => Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";

    /// <summary>WADDAMBURO_READ: Movie viewer: a script value read and printed (path).</summary>
    public static string? Read => Environment.GetEnvironmentVariable("WADDAMBURO_READ");

    /// <summary>WADDAMBURO_REJECT_CARDS: 1: every paired card is rejected (the red band).</summary>
    public static bool RejectCards => Environment.GetEnvironmentVariable("WADDAMBURO_REJECT_CARDS") == "1";

    /// <summary>WADDAMBURO_RENDERER: The renderer (vulkan, gles, opengl, d3d11, metal); config.cfg's renderer sets it when unset.</summary>
    public static string? Renderer => Environment.GetEnvironmentVariable("WADDAMBURO_RENDERER");

    /// <summary>WADDAMBURO_PRELOAD: 1: every screen and jingle loads at boot and stays in memory (a console's slow SD card).</summary>
    public static bool Preload => Environment.GetEnvironmentVariable("WADDAMBURO_PRELOAD") == "1";

    /// <summary>WADDAMBURO_RESULT_PLAYERS: 2: --start-scene results show the diagnostic result for both players.</summary>
    public static string? ResultPlayers => Environment.GetEnvironmentVariable("WADDAMBURO_RESULT_PLAYERS");

    /// <summary>WADDAMBURO_SETUP_BOTH: 1: the home player setup opens for both drums.</summary>
    public static bool SetupBoth => Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_BOTH") == "1";

    /// <summary>WADDAMBURO_SETUP_TRACE: 1: the home player setup's columns on the console.</summary>
    public static bool SetupTrace => Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_TRACE") == "1";

    /// <summary>WADDAMBURO_TICK_STATS: 1: the game logic's average time and allocation per tick, every 600 ticks.</summary>
    public static bool TickStats => Environment.GetEnvironmentVariable("WADDAMBURO_TICK_STATS") == "1";

    /// <summary>WADDAMBURO_TITLE: Player 1's title plate (plate:text).</summary>
    public static string? Title => Environment.GetEnvironmentVariable("WADDAMBURO_TITLE");

    /// <summary>WADDAMBURO_TREE_EVERY: Movie viewer: the display list printed every this many ticks.</summary>
    public static string? TreeEvery => Environment.GetEnvironmentVariable("WADDAMBURO_TREE_EVERY");

    /// <summary>WADDAMBURO_TREE_FILTER: Movie viewer: only display-list lines with these parts (a|b).</summary>
    public static string? TreeFilter => Environment.GetEnvironmentVariable("WADDAMBURO_TREE_FILTER");

    /// <summary>WADDAMBURO_UPDATE_URL: The release feed the self-update reads instead of GitHub's.</summary>
    public static string? UpdateUrl => Environment.GetEnvironmentVariable("WADDAMBURO_UPDATE_URL");

    /// <summary>WADDAMBURO_UPSCALE_MODEL: The upscaler's model folder.</summary>
    public static string? UpscaleModel => Environment.GetEnvironmentVariable("WADDAMBURO_UPSCALE_MODEL");

    /// <summary>WADDAMBURO_WAIWAI_RARE: 1: every Waiwai song gets its rare note.</summary>
    public static bool WaiwaiRare => Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RARE") == "1";

    /// <summary>WADDAMBURO_WAIWAI_RESULT: --start-scene=waiwai-result: segments,duet%,rare notes.</summary>
    public static string? WaiwaiResult => Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RESULT");
}
