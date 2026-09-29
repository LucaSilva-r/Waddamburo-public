namespace Waddamburo.Game.Gameplay;

/// <summary>
/// Two-player hand notes (traced session10-hand-notes): the note scores double for both players
/// when both hit it Great, each in their own course's window (so Hard and Oni players need to be
/// closer); otherwise each scores a plain hit. Lanes match by the note's time, since each player has
/// their own chart. (The trace doubled hits 1 ms apart but not 12 / 15 ms; user decision: both Great.)
/// </summary>
public sealed class TaikoHandNoteLink
{
    private readonly List<(TaikoJudgementSession Session, Dictionary<TimeSpan, int> Hits)> _lanes = [];

    public void Add(TaikoJudgementSession session, IReadOnlyList<Catalog.PlayableHitObject> notes)
    {
        var hits = new Dictionary<TimeSpan, int>();
        _lanes.Add((session, hits));
        session.HandNoteHit += index =>
        {
            var start = notes[index].StartTime;
            hits[start] = index;
            foreach (var (other, otherHits) in _lanes)
                if (other != session && otherHits.TryGetValue(start, out var partner))
                {
                    session.CompleteStrongHit(index);
                    other.CompleteStrongHit(partner);
                }
        };
    }
}
