using Waddamburo.Game.Flow;

namespace Waddamburo.App.Online;

/// <summary>
/// Phones don't update themselves: when a newer release is out, this build stays off TaikOnline (an outdated client
/// must not call APIs that have moved on; the server checks every request regardless) and says where to get it.
/// Offline play is untouched, and config.cfg keeps its server.
/// </summary>
internal static class UpdateGate
{
    /// <summary>The newer release (its tag and GitHub page), checked once at boot; null: up to date or unknown.</summary>
    public static (string Tag, Uri Page)? Newer { get; private set; }

    /// <summary>Asks GitHub, on a phone's release build (before the game opens: what goes online reads <see cref="Server"/>).</summary>
    public static void Check()
    {
        if (Waddamburo.Platform.Sdl.PlatformInfo.IsMobile)
            Newer = Hosting.SelfUpdate.NewerForPhone();
        if (Newer is { } newer)
            Console.WriteLine($"Waddamburo {newer.Tag} is out: online play waits for the update ({newer.Page}).");
    }

    /// <summary>The server to go online with: none while an update is required.</summary>
    public static Uri? Server(ArcadeSettings arcade) => Newer is null ? arcade.Server : null;
}
