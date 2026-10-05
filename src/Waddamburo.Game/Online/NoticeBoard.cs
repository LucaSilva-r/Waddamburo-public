namespace Waddamburo.Game.Online;

public enum NoticeSeverity { Info, Warning, Critical }

/// <summary>Where a notice came from: the server for everyone, the server for one player, or this PC.</summary>
public enum NoticeSource { System, Personal, Local }

/// <summary>A notice as the server sends it (snake_case JSON); ids are strings for both kinds.</summary>
public sealed record ServerNotice(string Id, string Message, string? Severity)
{
    /// <summary>What a personal notice is about (e.g. play_rejected); null for system ones.</summary>
    public string? Kind { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>One line on the notice board. <see cref="Baid"/>: the player a personal notice is for.</summary>
public sealed record Notice(string Id, NoticeSource Source, NoticeSeverity Severity, string Message, DateTimeOffset At)
{
    public string? Kind { get; init; }

    public long? Baid { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>The server's own id (for marking a personal notice read); null for local ones.</summary>
    public string? ServerId { get; init; }

    public static Notice From(ServerNotice notice, NoticeSource source, long? baid = null) => new(
        $"{source}:{notice.Id}", source, notice.Severity switch
        {
            "critical" => NoticeSeverity.Critical,
            "warning" => NoticeSeverity.Warning,
            _ => NoticeSeverity.Info,
        }, notice.Message, notice.CreatedAt ?? DateTimeOffset.UtcNow)
    {
        Kind = notice.Kind,
        Baid = baid,
        ServerId = notice.Id,
        EndsAt = notice.EndsAt,
    };
}

/// <summary>
/// Every notice this session (thread-safe: they arrive from the network). The sidebar shows the
/// board; <see cref="Added"/> raises a toast. A notice seen twice (pushed, then in a backlog) is kept once.
/// </summary>
public sealed class NoticeBoard
{
    private readonly List<Notice> _notices = [];

    public event Action<Notice>? Added;

    /// <summary>The notices still current, newest first.</summary>
    public IReadOnlyList<Notice> Current
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            lock (_notices)
                return [.. _notices.Where(notice => notice.EndsAt is not { } ends || ends > now).OrderByDescending(static notice => notice.At)];
        }
    }

    /// <summary>False when the notice was already on the board.</summary>
    public bool Add(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_notices)
        {
            if (_notices.Any(existing => existing.Id == notice.Id))
                return false;
            _notices.Add(notice);
        }
        Added?.Invoke(notice);
        return true;
    }

    /// <summary>A notice from this PC itself (a failed job, a setting that needs attention).</summary>
    public bool AddLocal(string message, NoticeSeverity severity = NoticeSeverity.Info) =>
        Add(new Notice($"{NoticeSource.Local}:{Guid.NewGuid():N}", NoticeSource.Local, severity, message, DateTimeOffset.UtcNow));

    public void Dismiss(string id)
    {
        lock (_notices)
            _notices.RemoveAll(notice => notice.Id == id);
    }
}
