using System.Collections.Immutable;

namespace Waddamburo.Lumen.Provider;

public enum WdKind { State, Event }

/// <summary>One name of the provider: what a movie may read with <c>Wd.get</c> or listen to with <c>Wd.on</c>.</summary>
/// <param name="Type">number, bool, text; an event's arguments in order.</param>
/// <param name="Scope">When it has a value (movie-provider-design.md, Scopes): app, session, or a screen.</param>
/// <param name="Sample">A plausible value (an event: its arguments, comma separated), for previews and documentation.</param>
public sealed record WdName(string Name, WdKind Kind, string Type, string Scope, string Description, string Sample = "");

/// <summary>
/// Every name the engine publishes to movies: the one list the runtime checks reads against, and the one the editor,
/// the language server and the documentation show. A name is a promise to whoever makes a skin: add, never rename.
/// </summary>
public static class WdNames
{
    public const int Version = 1;

    public static ImmutableArray<WdName> All { get; } =
    [
        new("play.score", WdKind.State, "number", "play", "This drum's score.", "123450"),
        new("play.combo", WdKind.State, "number", "play", "Notes hit in a row; 0 after a miss.", "57"),
        new("play.maxCombo", WdKind.State, "number", "play", "The longest combo of this play so far.", "112"),
        new("play.gauge", WdKind.State, "number", "play", "The soul gauge, 0 (empty) to 1 (full).", "0.65"),
        new("play.gaugeClearLine", WdKind.State, "number", "play", "Where on the gauge the clear line is, 0 to 1.", "0.8"),
        new("play.gaugeCleared", WdKind.State, "bool", "play", "The gauge is at or past its clear line.", "false"),
        new("play.gaugeFull", WdKind.State, "bool", "play", "The gauge is full.", "false"),
        new("play.good", WdKind.State, "number", "play", "Notes hit GOOD.", "98"),
        new("play.ok", WdKind.State, "number", "play", "Notes hit OK.", "12"),
        new("play.bad", WdKind.State, "number", "play", "Notes missed.", "3"),
        new("play.accuracy", WdKind.State, "number", "play", "0 to 1: GOOD counts whole, OK half, of the notes judged so far; 1 before the first.", "0.92"),
        new("play.course", WdKind.State, "text", "play", "easy, normal, hard, oni or ura.", "oni"),
        new("play.goGo", WdKind.State, "bool", "play", "Go-Go Time is on.", "true"),
        new("play.roll", WdKind.State, "number", "play", "Hits on the drumroll under way; no value when there is none.", "24"),
        new("play.balloon", WdKind.State, "number", "play", "Hits the balloon under way still needs; no value when there is none.", "5"),
        new("play.progress", WdKind.State, "number", "play", "How far through the song, 0 to 1, in hundredths.", "0.4"),
        new("note.judged", WdKind.Event, "result: text (good, ok, bad), big: bool", "play", "A note of this drum was hit or missed.", "good, false"),
        new("combo.milestone", WdKind.Event, "combo: number", "play", "The combo reached a hundred.", "100"),
        new("song.ended", WdKind.Event, "cleared: bool, fullCombo: bool", "play", "The song is over and its result is known.", "true, false"),
    ];

    private static readonly ImmutableDictionary<string, WdName> ByName = All.ToImmutableDictionary(static name => name.Name, StringComparer.Ordinal);

    public static WdName? Find(string name) => ByName.GetValueOrDefault(name);
}
