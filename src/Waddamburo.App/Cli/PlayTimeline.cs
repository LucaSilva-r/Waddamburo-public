using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.App.Cli;

/// <summary>
/// The <c>timeline</c> object of each --rescore result: the whole play as the engine judges it, for
/// TaikOnline's play page and replay player (so the website never re-implements the rules). Times in ms:
/// <c>windows</c> (great, good, miss), <c>notes</c> (time, kind, hand, result, offset, judged_at),
/// <c>long_notes</c> (start, end, kind, required hits, the times of the hits that counted), <c>inputs</c>
/// (time, action 0 left Don .. 3 right Ka, what it hit: note, strong, roll, pop, capped or none, and the note's
/// index when it judged one), <c>bars</c> and <c>gogo</c> (time, on). The chart is the one played (after its options).
/// </summary>
internal static class PlayTimeline
{
    private static readonly string[] Kinds = ["don", "ka", "big_don", "big_ka"];
    private static readonly string[] LongKinds = ["roll", "big_roll", "balloon", "kusudama"];

    /// <summary>Writes <paramref name="play"/>'s timeline as properties of the object <paramref name="writer"/> is in.</summary>
    public static void Write(Rescore.ChartCache charts, Rescore.StoredPlay play, Utf8JsonWriter writer)
    {
        var stored = Rescore.Chart(charts, play.Notes, play.Source);
        var chart = TaikoPlayOptions.FromBits(play.Options).Apply(stored.Chart, play.Seed ?? 0);
        var windows = TaikoGameplayPresentation.WindowsFor(stored.Chart, stored.Course);
        var replay = TaikoReplay.Decode(play.Replay);
        // As PlayRescore and ReviewTimeline play it: one solo lane, inputs in time order, then past the chart's end.
        var session = new TaikoJudgementSession(chart, windows, TaikoGameplayPresentation.StrongSecondHitWindow);
        var judged = new TaikoNoteJudgement?[chart.NoteCount];
        var judgedAt = new TimeSpan[chart.NoteCount];
        var longHits = chart.LongNotes.Select(static _ => new List<TimeSpan>()).ToArray();
        int? hitNote = null;
        session.Judged += judgement =>
        {
            if (judgement.StrongHitCompleted)
                return;
            judged[judgement.NoteIndex] = judgement;
            judgedAt[judgement.NoteIndex] = judgement.TimedOut ? judgement.HitObject.StartTime + windows.Miss : session.CurrentTime;
            if (!judgement.TimedOut)
                hitNote = judgement.NoteIndex;
        };
        session.LongNoteHit += progress => longHits[progress.NoteIndex].Add(session.CurrentTime);

        writer.WriteStartObject("windows");
        writer.WriteNumber("great", ms(windows.Great));
        writer.WriteNumber("good", ms(windows.Good));
        writer.WriteNumber("miss", ms(windows.Miss));
        writer.WriteEndObject();

        writer.WriteStartArray("inputs");
        foreach (var input in replay.Inputs.OrderBy(static input => input.Time))
        {
            hitNote = null;
            var result = session.SubmitInput(input.Action, input.Time < session.CurrentTime ? session.CurrentTime : input.Time);
            writer.WriteStartObject();
            writer.WriteNumber("t", ms(input.Time));
            writer.WriteNumber("action", (int)input.Action);
            writer.WriteString("hit", result switch
            {
                TaikoInputResult.Judged => "note",
                TaikoInputResult.StrongHitCompleted => "strong",
                TaikoInputResult.LongNoteHit => "roll",
                TaikoInputResult.BalloonPopped => "pop",
                TaikoInputResult.RollHitCapped => "capped",
                _ => "none",
            });
            if (result == TaikoInputResult.Judged && hitNote is { } note)
                writer.WriteNumber("note", note);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        var end = chart.HitObjects.Select(static note => note.StartTime).Concat(chart.LongNotes.Select(static note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max() + windows.Miss + TimeSpan.FromSeconds(1);
        session.AdvanceTo(end > session.CurrentTime ? end : session.CurrentTime);

        writer.WriteNumber("duration", ms(end));
        writer.WriteStartArray("notes");
        for (var index = 0; index < chart.NoteCount; index++)
        {
            var note = chart.HitObjects[index];
            writer.WriteStartObject();
            writer.WriteNumber("t", ms(note.StartTime));
            writer.WriteString("kind", Kinds[(int)note.Kind]);
            if (note.IsHand)
                writer.WriteBoolean("hand", true);
            if (judged[index] is { } judgement)
            {
                writer.WriteString("result", judgement.Result switch
                {
                    TaikoHitResult.Great => "great",
                    TaikoHitResult.Good => "good",
                    _ => "miss",
                });
                if (judgement.Offset is { } offset)
                    writer.WriteNumber("offset", ms(offset));
                writer.WriteNumber("judged_at", ms(judgedAt[index]));
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("long_notes");
        for (var index = 0; index < chart.LongNotes.Length; index++)
        {
            var note = chart.LongNotes[index];
            writer.WriteStartObject();
            writer.WriteNumber("start", ms(note.StartTime));
            writer.WriteNumber("end", ms(note.EndTime));
            writer.WriteString("kind", LongKinds[(int)note.Kind]);
            writer.WriteNumber("required", note.RequiredHits);
            writer.WriteStartArray("hits");
            foreach (var hit in longHits[index])
                writer.WriteNumberValue(ms(hit));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("bars");
        foreach (var bar in chart.BarLines.Where(static bar => bar.IsVisible))
            writer.WriteNumberValue(ms(bar.Time));
        writer.WriteEndArray();
        writer.WriteStartArray("gogo");
        foreach (var point in chart.EffectPoints)
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", ms(point.Time));
            writer.WriteBoolean("on", point.IsGoGo);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static double ms(TimeSpan time) => Math.Round(time.TotalMilliseconds, 2);
}
