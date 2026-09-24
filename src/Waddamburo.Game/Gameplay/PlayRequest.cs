using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

public enum LocalPlayerSlot
{
    PlayerOne,
    PlayerTwo,
}

/// <summary>A cardless guest's name board text (traced: どんちゃん on the left drum, かっちゃん on the right).</summary>
public static class TaikoGuest
{
    // ponytail: guest names until player profiles exist.
    public static string Name(int side) => side == 1 ? "かっちゃん" : "どんちゃん";
}

public sealed record PlayerChartRequest
{
    public PlayerChartRequest(
        LocalPlayerSlot player,
        ChartKey chart,
        CatalogAssetKey chartAsset,
        TaikoCourse course)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(chartAsset);
        Player = player;
        Chart = chart;
        ChartAsset = chartAsset;
        Course = course;
    }

    public LocalPlayerSlot Player { get; }

    public ChartKey Chart { get; }

    public CatalogAssetKey ChartAsset { get; }

    public TaikoCourse Course { get; }
}

/// <summary>
/// Immutable, catalog-revision-pinned input for loading one local play session.
/// It carries provider-owned asset identities; gameplay never receives library paths.
/// </summary>
public sealed record PlayRequest
{
    public PlayRequest(
        long catalogRevision,
        SongKey song,
        CatalogAssetKey? audioAsset,
        IEnumerable<PlayerChartRequest> players)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogRevision);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(players);
        var materialized = players.ToImmutableArray();
        if (materialized.IsEmpty || materialized.Length > 2)
            throw new ArgumentException("A play request must contain one or two local players.", nameof(players));
        if (materialized.Any(static player => player is null))
            throw new ArgumentException("A play request cannot contain a null player.", nameof(players));
        if (materialized.Select(static player => player.Player).Distinct().Count() != materialized.Length)
            throw new ArgumentException("A local player slot can appear only once.", nameof(players));
        // One player may be either drum (a solo right-drum player is Player Two); two are ordered.
        if (materialized.Length == 2
            && (materialized[0].Player != LocalPlayerSlot.PlayerOne || materialized[1].Player != LocalPlayerSlot.PlayerTwo))
        {
            throw new ArgumentException("Local players must be ordered Player One, then Player Two.", nameof(players));
        }
        if (materialized.Any(player => player.Chart.Song != song))
            throw new ArgumentException("Every selected chart must belong to the requested song.", nameof(players));

        CatalogRevision = catalogRevision;
        Song = song;
        AudioAsset = audioAsset;
        Players = materialized;
    }

    public long CatalogRevision { get; }

    public SongKey Song { get; }

    public CatalogAssetKey? AudioAsset { get; }

    public ImmutableArray<PlayerChartRequest> Players { get; }
}

public interface IPlayRequestSink
{
    bool TryRequestPlay(PlayRequest request);
}

/// <summary>Owns the pending Song Select handoff and the request accepted by gameplay.</summary>
public sealed class PlayRequestState : IPlayRequestSink
{
    private readonly object _sync = new();
    private PlayRequest? _pending;
    private PlayRequest? _active;

    public PlayRequest? Pending
    {
        get
        {
            lock (_sync)
                return _pending;
        }
    }

    public PlayRequest? Active
    {
        get
        {
            lock (_sync)
                return _active;
        }
    }

    public bool TryRequestPlay(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (_pending is not null || _active is not null)
                return false;
            _pending = request;
            return true;
        }
    }

    public PlayRequest ActivatePending()
    {
        lock (_sync)
        {
            if (_active is not null)
                throw new InvalidOperationException("A play request is already active.");
            var request = _pending
                ?? throw new InvalidOperationException("No play request is pending.");
            _pending = null;
            _active = request;
            return request;
        }
    }

    public void CancelPending()
    {
        lock (_sync)
            _pending = null;
    }

    public void ClearActive()
    {
        lock (_sync)
            _active = null;
    }
}
