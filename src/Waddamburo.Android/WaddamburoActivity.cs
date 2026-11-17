using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Org.Libsdl.App;

namespace Waddamburo.Android;

/// <summary>
/// The game on Android: SDL's activity owns the window and the input; <see cref="Main"/> runs on SDL's thread and
/// boots the game exactly as the desktop executable does (Waddamburo.App.Boot), with the app's private storage
/// as its home.
/// </summary>
[Activity(Label = "Waddamburo", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleInstance,
    ConfigurationChanges = ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.Orientation | ConfigChanges.ScreenLayout | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Touchscreen | ConfigChanges.UiMode)]
public sealed class WaddamburoActivity : SDLActivity
{
    private GameFolder? _gameFolder;

    protected override string[] GetLibraries() => ["SDL3"];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // The default current directory is '/', which differs between devices: use the app's own.
        System.Environment.CurrentDirectory = FilesDir!.AbsolutePath;
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        _gameFolder = new GameFolder(this);
        base.OnCreate(savedInstanceState);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        _gameFolder?.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _gameFolder?.OnPermissionsResult(requestCode);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _gameFolder?.OnResume();
    }

    protected override void Main()
    {
        // Diagnostics: the desktop's WADDAMBURO_* environment variables, as `adb shell am start ... --es env "K=V;K=V"`.
        foreach (var pair in (Intent?.GetStringExtra("env") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var key, var value])
                System.Environment.SetEnvironmentVariable(key, value);
        Waddamburo.App.Boot.AppHome = Path.Combine(FilesDir!.AbsolutePath, "waddamburo");
        string[] args = [$"--game-data={_gameFolder!.Resolve()}"];
        while (Waddamburo.App.Boot.Run(args) == Waddamburo.App.Boot.RestartExitCode)
        {
        }
    }
}
