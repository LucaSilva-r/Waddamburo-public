using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Hosting;

/// <summary>
/// Linux: puts the AppImage in the desktop's applications (menu, taskbar, system monitor) where it is, as
/// waddamburo.desktop (the window's app id) with its icon beside it, in the user's applications folder. Updates keep the AppImage's
/// path, so the entry stays good; a moved AppImage needs adding again.
/// </summary>
internal static class DesktopEntry
{
    /// <summary>The AppImage running, or null (Windows, a developer build): only an AppImage can be added.</summary>
    public static string? AppImage => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path ? path : null;

    private static string dataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string file => Path.Combine(dataHome, "applications", "waddamburo.desktop");

    /// <summary>An entry exists and starts this AppImage.</summary>
    public static bool Installed => AppImage is { } appImage && File.Exists(file) && File.ReadAllText(file).Contains(exec(appImage), StringComparison.Ordinal);

    /// <summary>Writes the entry and icon; false when they could not be written.</summary>
    public static bool Install()
    {
        if (AppImage is not { } appImage)
            return false;
        try
        {
            // Beside the entry, by its full path: a user icon theme's icon-theme.cache, once there, hides icons added after it.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var icon = Path.Combine(Path.GetDirectoryName(file)!, "waddamburo.png");
            using (var resource = typeof(SdlApplication).Assembly.GetManifestResourceStream("Waddamburo.Icon.png"))
            using (var output = File.Create(icon))
                resource?.CopyTo(output);
            File.WriteAllText(file, $"""
                [Desktop Entry]
                Type=Application
                Name=Waddamburo
                Exec={exec(appImage)}
                Icon={icon}
                Categories=Game;
                StartupWMClass=waddamburo
                """ + "\n");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: the desktop entry wasn't written ({exception.Message}).");
            return false;
        }
    }

    // The Exec key's quoting: the path in double quotes, with ", `, $ and \ escaped.
    private static string exec(string path) =>
        "\"" + string.Concat(path.Select(static c => c is '"' or '`' or '$' or '\\' ? "\\" + c : c.ToString())) + "\"";
}
