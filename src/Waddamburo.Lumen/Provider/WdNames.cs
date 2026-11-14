using System.Collections.Immutable;

namespace Waddamburo.Lumen.Provider;

/// <summary>List: a state with a value per place in a list (Wd.get(name, index)); the list's length is a state of its own, named in the description.</summary>
public enum WdKind { State, Event, List }

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
        new("play.branch", WdKind.State, "text", "play", "The route the lane is on: normal, expert or master; no value when the chart has no routes.", "expert"),
        new("play.roll", WdKind.State, "number", "play", "Hits on the drumroll under way; no value when there is none.", "24"),
        new("play.balloon", WdKind.State, "number", "play", "Hits the balloon under way still needs; no value when there is none (a kusudama is play.kusudama).", "5"),
        new("play.kusudama", WdKind.State, "number", "play", "Hits the kusudama under way still needs; no value when there is none.", "120"),
        new("play.beat", WdKind.State, "number", "play", "Beats since the chart's first timing point, following its tempo changes (fractional, told every tick; negative before it, still while the song stands still). A movie's frame from it, say start + Math.floor(beat * 40) % 40, plays one loop a beat in time with the song.", "17.25"),
        new("play.time", WdKind.State, "number", "play", "The song's time in seconds from the chart's start (negative during the lead-in).", "42.5"),
        new("play.duration", WdKind.State, "number", "play", "The chart's length in seconds.", "128.4"),
        new("play.bpm", WdKind.State, "number", "play", "The tempo at this point of the song, beats a minute.", "154"),
        new("play.meter", WdKind.State, "number", "play", "Beats in this measure (the time signature's upper number).", "4"),
        new("play.measure", WdKind.State, "number", "play", "Which measure the song is in, from 0 at the first bar line; -1 before it.", "12"),
        new("play.scroll", WdKind.State, "number", "play", "The chart's scroll speed here (its own changes, not the player's speed option, which is player.speed).", "1.25"),
        new("play.rollHits", WdKind.State, "number", "play", "Drumroll hits of this play so far.", "86"),
        new("play.balloonHits", WdKind.State, "number", "play", "Balloon and kusudama hits of this play so far.", "40"),
        new("play.notes", WdKind.State, "number", "play", "Notes of the chart on the route being played (a section not reached follows it), rolls and balloons not counted.", "612"),
        new("play.notesLeft", WdKind.State, "number", "play", "Of play.notes, those not judged yet.", "240"),
        new("play.nextNoteBeats", WdKind.State, "number", "play", "Beats until the next note on the route (rolls and balloons not counted); no value when none is left.", "0.5"),
        new("play.nextNoteSeconds", WdKind.State, "number", "play", "Seconds until the next note on the route; no value when none is left.", "0.19"),
        new("play.nextNoteKind", WdKind.State, "text", "play", "The next note: don, ka, bigDon or bigKa; no value when none is left.", "ka"),
        new("play.level", WdKind.State, "number", "play", "The stars of the course being played; no value when the song has none.", "8"),
        new("play.progress", WdKind.State, "number", "play", "How far through the song, 0 to 1, in hundredths.", "0.4"),
        new("dan.rank", WdKind.State, "number", "play", "The dan course's rank, 0 for the lowest; no value outside a course.", "9"),
        new("dan.name", WdKind.State, "text", "play", "The dan course's name, as its series writes it.", "六段"),
        new("dan.song", WdKind.State, "number", "play", "Which song of the dan course is playing, from 1.", "2"),
        new("dan.notesLeft", WdKind.State, "number", "play", "Notes of the dan course not judged yet.", "1240"),
        new("dan.exam.count", WdKind.State, "number", "play", "How many requirements the dan course's panel shows: the length of the dan.exam lists.", "3"),
        new("dan.exam.type", WdKind.List, "text", "play", "A requirement's kind: soul_gauge, goods, oks, bads, combo, hits, score or rolls. oks and bads must stay under their target; the others must reach it.", "bads"),
        new("dan.exam.target", WdKind.List, "number", "play", "What the requirement asks for (this song's, where each song has its own).", "10"),
        new("dan.exam.gold", WdKind.List, "number", "play", "The requirement's target for a gold pass; 0 when it has none.", "5"),
        new("dan.exam.value", WdKind.List, "number", "play", "Where the play stands on the requirement.", "3"),
        new("dan.exam.possible", WdKind.List, "bool", "play", "The requirement can still be met.", "true"),
        new("song.title", WdKind.State, "text", "play", "The song's title.", "Senbonzakura"),
        new("song.artist", WdKind.State, "text", "play", "Who made the song; no value when the library does not say.", "Kurousa-P"),
        new("song.genre", WdKind.State, "text", "play", "The song's genre: jpop, anime, vocaloid, doyo (children and folk), variety, classic, game or namco.", "vocaloid"),
        new("song.source", WdKind.State, "text", "play", "Where the song comes from: stock, nijiiro, tja, osuLazer or osuStable.", "tja"),
        new("song.stage", WdKind.State, "number", "play", "Which song of the credit or dan course this is, from 1; no value where the game shows no stage number (home).", "2"),
        new("song.finalStage", WdKind.State, "bool", "play", "This is the credit's last song.", "false"),
        new("song.subtitle", WdKind.State, "text", "play", "The song's subtitle; no value when it has none.", "Kurousa-P feat. Hatsune Miku"),
        new("player.name", WdKind.State, "text", "play", "The name of this drum's player.", "Don-chan"),
        new("player.side", WdKind.State, "number", "play", "This drum: 0 the left one, 1 the right one.", "0"),
        new("player.autoplay", WdKind.State, "bool", "play", "This drum is played by the bot (autoplay, the attract demo, AI battle's AI-don), not a person.", "false"),
        new("player.replay", WdKind.State, "bool", "play", "This play is a recorded one being watched again.", "false"),
        new("player.speed", WdKind.State, "number", "play", "The notes' scroll speed option, 1 for none.", "1.5"),
        new("player.doron", WdKind.State, "bool", "play", "The vanishing notes option is on.", "false"),
        new("player.abekobe", WdKind.State, "bool", "play", "The swapped notes option is on.", "false"),
        new("player.random", WdKind.State, "number", "play", "The shuffled notes option: 0 off, 1 a little, 2 a lot.", "0"),
        new("player.shinuchi", WdKind.State, "bool", "play", "The true-score option is on.", "false"),
        new("player.tone", WdKind.State, "number", "play", "This drum's sound, 0 to 5: taiko, festival, dogs and cats, deluxe taiko, drum kit, tambourine.", "0"),
        new("player.support", WdKind.State, "bool", "play", "The helping-hand option (papa-mama support) is on. Always false for now: the engine does not have it yet.", "false"),
        new("note.judged", WdKind.Event, "result: text (good, ok, bad), big: bool, kind: text (don, ka)", "play", "A note of this drum was hit or missed.", "good, false, don"),
        new("score.added", WdKind.Event, "points: number", "play", "Points were just scored on this drum (a big note's two halves come as one).", "960"),
        new("combo.milestone", WdKind.Event, "combo: number", "play", "The combo reached a hundred.", "100"),
        new("roll.hit", WdKind.Event, "part: text (don, ka), big: bool", "play", "A hit landed on this drum's drumroll.", "don, false"),
        new("balloon.popped", WdKind.Event, "", "play", "This drum's balloon burst.", ""),
        new("balloon.started", WdKind.Event, "hits: number, goGo: bool", "play", "A balloon came under way on this drum, needing that many hits; goGo: it starts in Go-Go time.", "20, false"),
        new("balloon.ended", WdKind.Event, "popped: bool", "play", "This drum's balloon is over: burst, or its time ran out.", "true"),
        new("kusudama.started", WdKind.Event, "hits: number, goGo: bool", "play", "A kusudama came under way on this drum, needing that many of its hits (with two drums, each adds its own to the one ball); goGo: it starts in Go-Go time.", "40, false"),
        new("kusudama.ended", WdKind.Event, "popped: bool", "play", "This drum's kusudama is over: burst, or its time ran out.", "false"),
        new("beat.passed", WdKind.Event, "beat: number", "play", "The song reached a whole beat (play.beat's whole part), once each going forward.", "18"),
        new("measure.passed", WdKind.Event, "measure: number", "play", "The song reached a bar line (play.measure's new value), once each going forward.", "13"),
        new("drum.hit", WdKind.Event, "side: text (left, right), part: text (don, ka)", "play", "This drum was struck, note or not.", "left, don"),
        new("song.ended", WdKind.Event, "cleared: bool, fullCombo: bool", "play", "The song is over and its result is known.", "true, false"),
    ];

    private static readonly ImmutableDictionary<string, WdName> ByName = All.ToImmutableDictionary(static name => name.Name, StringComparer.Ordinal);

    public static WdName? Find(string name) => ByName.GetValueOrDefault(name);
}
