using System.Collections.Immutable;

namespace Waddamburo.Lumen.Provider;

public enum WdKind { State, Event }

/// <summary>One name of the provider: what a movie may read with <c>Wd.get</c> or listen to with <c>Wd.on</c>.</summary>
/// <param name="Type">number, bool, text; an event's arguments in order.</param>
/// <param name="Scope">When it has a value (movie-provider-design.md, Scopes): app, session, or a screen.</param>
public sealed record WdName(string Name, WdKind Kind, string Type, string Scope, string Description);

/// <summary>
/// Every name the engine publishes to movies: the one list the runtime checks reads against, and the one the editor,
/// the language server and the documentation show. A name is a promise to whoever makes a skin: add, never rename.
/// </summary>
public static class WdNames
{
    public const int Version = 1;

    public static ImmutableArray<WdName> All { get; } =
    [
        new("play.score", WdKind.State, "number", "play", "This drum's score."),
        new("play.combo", WdKind.State, "number", "play", "Notes hit in a row; 0 after a miss."),
        new("play.maxCombo", WdKind.State, "number", "play", "The longest combo of this play so far."),
        new("play.gauge", WdKind.State, "number", "play", "The soul gauge, 0 (empty) to 1 (full)."),
        new("play.gaugeClearLine", WdKind.State, "number", "play", "Where on the gauge the clear line is, 0 to 1."),
        new("play.gaugeCleared", WdKind.State, "bool", "play", "The gauge is at or past its clear line."),
        new("play.gaugeFull", WdKind.State, "bool", "play", "The gauge is full."),
        new("play.good", WdKind.State, "number", "play", "Notes hit GOOD."),
        new("play.ok", WdKind.State, "number", "play", "Notes hit OK."),
        new("play.bad", WdKind.State, "number", "play", "Notes missed."),
        new("play.accuracy", WdKind.State, "number", "play", "0 to 1: GOOD counts whole, OK half, of the notes judged so far; 1 before the first."),
        new("play.course", WdKind.State, "text", "play", "easy, normal, hard, oni or ura."),
        new("note.judged", WdKind.Event, "result: text (good, ok, bad), big: bool", "play", "A note of this drum was hit or missed."),
        new("combo.milestone", WdKind.Event, "combo: number", "play", "The combo reached a hundred."),
    ];

    private static readonly ImmutableDictionary<string, WdName> ByName = All.ToImmutableDictionary(static name => name.Name, StringComparer.Ordinal);

    public static WdName? Find(string name) => ByName.GetValueOrDefault(name);
}
