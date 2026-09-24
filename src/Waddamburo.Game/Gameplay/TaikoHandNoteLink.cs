namespace Waddamburo.Game.Gameplay;

/// <summary>
/// Two-player hand notes (traced session10-hand-notes): the note scores double for both players
/// when both hit it together; a player hitting alone scores a plain hit. Lanes match by the note's
/// time, since each player has their own chart.
/// </summary>
/// <param name="together">How far apart the two hits may be: the great window (user decision; the
/// trace doubled 1 ms apart but not 12 / 15 ms, to revisit with the judgement tune-up).</param>
public sealed class TaikoHandNoteLink(TimeSpan together)
{
    private readonly List<(TaikoJudgementSession Session, Dictionary<TimeSpan, (int Index, TimeSpan Time)> Hits)> _lanes = [];

    public void Add(TaikoJudgementSession session, IReadOnlyList<Catalog.PlayableHitObject> notes)
    {
        var hits = new Dictionary<TimeSpan, (int Index, TimeSpan Time)>();
        _lanes.Add((session, hits));
        session.HandNoteHit += (index, time) =>
        {
            var start = notes[index].StartTime;
            hits[start] = (index, time);
            foreach (var (other, otherHits) in _lanes)
                if (other != session && otherHits.TryGetValue(start, out var partner)
                    && (time - partner.Time).Duration() <= together)
                {
                    session.CompleteStrongHit(index);
                    other.CompleteStrongHit(partner.Index);
                }
        };
    }
}
