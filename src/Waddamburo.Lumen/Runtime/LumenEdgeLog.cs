using System.Globalization;

namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// Development record of everything that crosses a movie's edge (see docs/development/free-movies-plan.md):
/// one tab-separated line per crossing, <c>tick, movie, kind, text</c>. <c>WADDAMBURO_EDGE_LOG=file</c> turns it
/// on; a replacement movie is checked by comparing its lines with the original's over the same session.
/// Kinds: <c>export</c> (a callback the movie registers), <c>in</c> (the host calls one), <c>out</c> (the movie
/// calls the host), <c>command</c>, <c>load</c> (loadClip), <c>root</c> (the host moves the root timeline),
/// <c>call</c> / <c>goto</c> (the host calls a script method / moves a named clip), and <c>use</c>: the host
/// reads, writes or addresses something inside the movie by name, written when its result changes only
/// (hosts poll these every tick); <c>host</c>: a crossing outside any movie (Don-chan's motions and camera, source
/// <c>don</c>, tick -1).
/// </summary>
public static class LumenEdgeLog
{
    private static readonly Lock Gate = new();

    /// <summary>Where the lines go; null (the default without the variable) records nothing.</summary>
    public static TextWriter? Writer { get; set; } =
        Environment.GetEnvironmentVariable("WADDAMBURO_EDGE_LOG") is { Length: > 0 } path
            ? new StreamWriter(path) { AutoFlush = true }
            : null;

    internal static bool Enabled => Writer is not null;

    /// <summary>
    /// A crossing outside any movie (Don-chan's model: <c>don</c>), as a line of its own source with no tick (the
    /// engine has no global one): compared in order with the other lines of that source.
    /// </summary>
    public static void Host(string source, string text)
    {
        if (Enabled) Write(-1, source, "host", text);
    }

    internal static void Write(long tick, string movie, string kind, string text)
    {
        // Players of one scene may load on different threads; lines of one movie stay in order.
        lock (Gate)
            Writer?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{tick}\t{movie}\t{kind}\t{text}"));
    }
}
