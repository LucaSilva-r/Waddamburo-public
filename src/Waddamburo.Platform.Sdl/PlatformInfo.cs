namespace Waddamburo.Platform.Sdl;

/// <summary>What kind of device the game runs on: phones and tablets hide the desktop-only settings and features.</summary>
public static class PlatformInfo
{
    /// <summary>Android or iOS: touch drum, the game's files in the app's own storage, no second process.</summary>
    public static bool IsMobile { get; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
}
