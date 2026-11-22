namespace Waddamburo.Platform.Sdl;

/// <summary>What kind of device the game runs on: phones and tablets hide the desktop-only settings and features.</summary>
public static class PlatformInfo
{
    /// <summary>Android or iOS: touch drum, the game's files in the app's own storage, no second process.</summary>
    public static bool IsMobile { get; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    /// <summary>
    /// The Switch build (.NET sees a Linux there): plenty of memory and a slow SD card. Its build sets the option
    /// (Waddamburo.Switch, a RuntimeHostConfigurationOption).
    /// </summary>
    public static bool IsSwitch { get; } = AppContext.TryGetSwitch("Waddamburo.Switch", out var value) && value;
}
