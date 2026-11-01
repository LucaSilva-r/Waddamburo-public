namespace Waddamburo.Game.Online;

/// <summary>What a home player is doing: online, afk or playing (with the chart, when known).</summary>
public sealed record PlayerStatus(string State, string? ChartSha256 = null)
{
    public static readonly PlayerStatus Online = new("online");
    public static readonly PlayerStatus Afk = new("afk");

    public static PlayerStatus Playing(string? chartSha256) => new("playing", chartSha256);
}

/// <summary>
/// Sends the player's status when it changes and every <see cref="Heartbeat"/> otherwise (the server forgets
/// it after 90 s). The game calls <see cref="Report"/> every tick; one request runs at a time, and a failed one
/// is simply tried again at the next heartbeat. It never blocks the tick.
/// </summary>
public sealed class StatusReporter(Func<PlayerStatus, Task> send, Func<TimeSpan> now)
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    private PlayerStatus? _sent;
    private TimeSpan _sentAt;
    private Task _running = Task.CompletedTask;

    public void Report(PlayerStatus status)
    {
        var time = now();
        if (!_running.IsCompleted || status == _sent && time - _sentAt < Heartbeat)
            return;
        _sent = status;
        _sentAt = time;
        _running = sendAsync(status);
    }

    private async Task sendAsync(PlayerStatus status)
    {
        try
        {
            await send(status).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Warning STATUS: not sent ({exception.Message}).");
        }
    }
}
