using Waddamburo.App.Scenes;
using Waddamburo.Game.Online;

namespace Waddamburo.App.Online;

/// <summary>
/// What the player is doing, for Discord's status and the website's (playing a chart, online, away). Home mode only:
/// a cabinet has no player's Discord or status; never in a headless run.
/// </summary>
internal sealed class PresenceReporter(GameShell shell, GameplayFlow gameplay) : IDisposable
{
    // No key or drum for this long at home, outside a song, shows the player as away.
    private static readonly TimeSpan AfkAfter = TimeSpan.FromMinutes(5);
    private readonly System.Diagnostics.Stopwatch _sinceInput = System.Diagnostics.Stopwatch.StartNew();
    private DiscordPresence? _discord;

    /// <summary>A key or drum was pressed (noted per frame: ticks only see held keys).</summary>
    public void Pressed() => _sinceInput.Restart();

    /// <summary>Each tick.</summary>
    public void Report()
    {
        reportPresence();
        if (!shell.Arcade.Home)
            return;
        // A song counts as activity, so leaving one is never AFK.
        if (gameplay.Playing)
            _sinceInput.Restart();
        // The room, once the other player is there: the website names them (the server checks both are in it).
        var status = gameplay.Playing ? PlayerStatus.Playing(gameplay.PlayingChart)
            : _sinceInput.Elapsed >= AfkAfter ? PlayerStatus.Afk : PlayerStatus.Online;
        shell.Sync.ReportStatus(shell.Room.Session is { Partner: not null, Room: { } room } ? status with { Room = room.Code } : status);
    }

    private void reportPresence()
    {
        if (!shell.Arcade.Home || shell.Headless)
            return;
        _discord ??= new DiscordPresence();
        var partner = shell.Room.Session?.Partner?.DisplayName;
        var versus = partner is { Length: > 0 } ? $"Online vs {partner}" : shell.Players.JoinedSides.Count > 1 ? "Two players" : null;
        var active = shell.Active.Id;
        var activity = gameplay.Presence is { } playing ? new PresenceActivity(playing.What, versus ?? "Solo", playing.Since)
            : active == FlowScenes.SongSelect ? new PresenceActivity("Choosing a song", versus)
            : active == FlowScenes.DaniSelect ? new PresenceActivity("Choosing a dan course")
            : active == FlowScenes.Result ? new PresenceActivity("Looking at the results", versus)
            : shell.Room.Session is not null ? new PresenceActivity("In an online room", versus)
            : new PresenceActivity("In the menus");
        _discord.Set(activity, shell.Arcade.DiscordPresence);
    }

    public void Dispose() => _discord?.Dispose();
}
