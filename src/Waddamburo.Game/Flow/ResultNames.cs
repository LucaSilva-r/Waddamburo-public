using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Flow;

/// <summary>
/// The provider's result names (WdNames scope result, dan) for a results movie of ours, whichever frontend shows it:
/// each play under its drum's number, a course's totals, requirements and songs.
/// </summary>
public static class ResultNames
{
    private static LumenHostValue n(double value) => LumenHostValue.FromNumber(value);

    /// <summary>A song's results: each play (one, or both drums') under its drum, with the bests before it.</summary>
    public static void Publish(LumenPlayer movie, IReadOnlyList<TaikoPlayResult> plays, CreditState credit)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(plays);
        ArgumentNullException.ThrowIfNull(credit);
        if (!movie.UsesProvider)
            return;
        for (var index = 0; index < plays.Count; index++)
        {
            var play = plays[index];
            var drum = (plays.Count == 2 ? index : credit.PlayerSide) + 1;
            void tell(string name, LumenHostValue value) => movie.Publish(name, value, drum);
            tell("result.course", LumenHostValue.FromString(play.Course.ToString().ToLowerInvariant()));
            tell("result.score", n(play.Score));
            tell("result.best", credit.Best(index) is { } best ? n(best) : LumenHostValue.Undefined);
            tell("result.good", n(play.Great));
            tell("result.ok", n(play.Good));
            tell("result.bad", n(play.Miss));
            tell("result.maxCombo", n(play.MaxCombo));
            tell("result.rolls", n(play.Rolls + play.BalloonHits));
            tell("result.gauge", n((double)play.GaugeSegments / TaikoSoulGauge.Segments));
            tell("result.cleared", LumenHostValue.FromBoolean(play.Cleared));
            tell("result.fullCombo", LumenHostValue.FromBoolean(play.FullCombo));
        }
    }

    /// <summary>
    /// A course's results: its totals under the player's <paramref name="drum"/>, its outcome, the requirements where the
    /// course ended, and each song's row (<paramref name="titles"/>: the course's song titles, in order).
    /// </summary>
    public static void PublishDan(LumenPlayer movie, DanCourseResult dan, int drum, IReadOnlyList<string?> titles)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(dan);
        ArgumentNullException.ThrowIfNull(titles);
        if (!movie.UsesProvider)
            return;
        var total = dan.Total;
        movie.Publish("dan.name", LumenHostValue.FromString(dan.Course.Name));
        movie.Publish("dan.passed", LumenHostValue.FromString(dan.Level switch { 2 => "gold", 1 => "red", _ => "failed" }));
        movie.Publish("dan.songsPlayed", n(dan.SongsPlayed));
        movie.Publish("result.score", n(total.Score), drum);
        movie.Publish("result.good", n(total.Goods), drum);
        movie.Publish("result.ok", n(total.Oks), drum);
        movie.Publish("result.bad", n(total.Bads), drum);
        movie.Publish("result.maxCombo", n(total.MaxCombo), drum);
        movie.Publish("result.rolls", n(total.Rolls), drum);
        movie.Publish("result.gauge", n(total.GaugePercent), drum);
        movie.Publish("result.cleared", LumenHostValue.FromBoolean(dan.Level > 0), drum);
        var requirements = dan.Course.Requirements;
        movie.Publish("dan.exam.count", n(requirements.Count));
        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index];
            movie.PublishItem("dan.exam.type", index, LumenHostValue.FromString(requirement.Type));
            movie.PublishItem("dan.exam.target", index, requirement.Red is [var red, ..] ? n(red) : LumenHostValue.Undefined);
            movie.PublishItem("dan.exam.gold", index, n(requirement.Gold is [var gold, ..] ? gold : 0));
            movie.PublishItem("dan.exam.value", index, n(DanExams.ValueOf(requirement.Type, total)));
        }
        for (var index = 0; index < dan.Songs.Count; index++)
        {
            var counts = dan.Songs[index].Counts;
            var title = index < titles.Count ? titles[index] : null;
            movie.PublishItem("dan.songTitle", index, title is null ? LumenHostValue.Undefined : LumenHostValue.FromString(title));
            movie.PublishItem("dan.songGood", index, index < dan.SongsPlayed ? n(counts.Goods) : LumenHostValue.Undefined);
            movie.PublishItem("dan.songOk", index, index < dan.SongsPlayed ? n(counts.Oks) : LumenHostValue.Undefined);
            movie.PublishItem("dan.songBad", index, index < dan.SongsPlayed ? n(counts.Bads) : LumenHostValue.Undefined);
        }
    }
}
