namespace Waddamburo.Platform.Sdl;

/// <summary>What kind of device the game runs on: phones and tablets hide the desktop-only settings and features.</summary>
public static class PlatformInfo
{
    /// <summary>Android or iOS: touch drum, files through the system's document picker, no second process.</summary>
    public static bool IsMobile { get; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
}
