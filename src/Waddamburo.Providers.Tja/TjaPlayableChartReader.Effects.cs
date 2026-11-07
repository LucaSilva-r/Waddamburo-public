using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

internal static partial class TjaPlayableChartReader
{
    /// <summary>
    /// Collects the commands that only change what is drawn (<see cref="ChartEffects"/>) and #SENOTECHANGE's label, so
    /// the timing and note reading around them stays as it was.
    /// </summary>
    private sealed class TjaEffectsBuilder
    {
        // The main route's beat count over time (#HBSCROLL/#BMSCROLL): time, beat, beats per second from there.
        private readonly List<(decimal Time, decimal Beat, decimal Bps)> _beats = [];
        private readonly List<(decimal At, double Seconds, double X, double Y)> _moves = [];
        private readonly List<ChartModEase> _mods = [];
        private string? _noteText;

        public void Start(decimal bpm) => _beats.Add((0, 0, bpm / 60));

        // The beat count at a time from the last anchor (anchors go in time order: a tempo change or a #DELAY each).
        private decimal beatAt(decimal time) => _beats[^1].Beat + (time - _beats[^1].Time) * _beats[^1].Bps;

        public void TempoChanged(decimal at, decimal bpm) => _beats.Add((at, beatAt(at), bpm / 60));

        /// <summary>A #DELAY stops the beat count. ponytail: a negative one (a single note's offset) leaves the count be.</summary>
        public void Delayed(decimal at, decimal seconds, decimal bpm)
        {
            if (seconds <= 0)
                return;
            var beat = beatAt(at);
            _beats.Add((at, beat, 0));
            _beats.Add((at + seconds, beat, bpm / 60));
        }

        /// <summary>#JPOSSCROLL. ponytail: one list for every 譜面分岐 route.</summary>
        public void MoveTarget(decimal at, double seconds, double x, double y) => _moves.Add((at, seconds, x, y));

        public void Mod(ChartModEase change) => _mods.Add(change);

        /// <summary>#SENOTECHANGE: the label under the next don or ka (1 ドン 2 ド 3 コ 4 カッ 5 カ 6 ドン(大) 7 カッ(大); the roll ones unused).</summary>
        public void NoteText(int index) => _noteText = index switch
        {
            1 => "don", 2 => "do", 3 => "ko", 4 => "katsu", 5 => "ka", 6 => "don_dai", 7 => "katsu_dai", _ => null,
        };

        /// <summary>The forced label for a note being read (a don or ka takes it, once).</summary>
        public string? TakeNoteText(PlayableNoteKind kind)
        {
            var text = kind is PlayableNoteKind.Don or PlayableNoteKind.Ka ? _noteText : null;
            _noteText = null;
            return text;
        }

        /// <param name="byBeat">Whether any scroll point scrolls by beat (the beat count is kept only then).</param>
        public ChartEffects Build(bool byBeat)
        {
            var effects = new ChartEffects
            {
                JudgeMoves = resolveMoves(),
                Beats = byBeat ? [.. _beats.Select(static anchor => new ChartBeatPoint(chartTime(anchor.Time), (double)anchor.Beat, (double)anchor.Bps))] : [],
                Mods = [.. _mods.OrderBy(static change => change.Start)],
            };
            return effects.IsEmpty ? ChartEffects.None : effects;
        }

        // Moves in time order, each cut short where the next starts so the target ends where OpenTaiko's does.
        private ImmutableArray<ChartJudgeMove> resolveMoves()
        {
            var ordered = _moves.OrderBy(static move => move.At).ToArray();
            var result = ImmutableArray.CreateBuilder<ChartJudgeMove>(ordered.Length);
            for (var index = 0; index < ordered.Length; index++)
            {
                var (at, seconds, x, y) = ordered[index];
                var room = index + 1 < ordered.Length ? (double)(ordered[index + 1].At - at) : double.MaxValue;
                var done = seconds <= room ? 1 : room / seconds;
                result.Add(new ChartJudgeMove(chartTime(at), TimeSpan.FromSeconds(Math.Min(seconds, room)), x * done, y * done));
            }
            return result.MoveToImmutable();
        }
    }
}
