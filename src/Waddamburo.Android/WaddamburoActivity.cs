using Android.Content;
using Android.Content.PM;
using Android.Views;
using Org.Libsdl.App;

namespace Waddamburo.Android;

/// <summary>
/// The game on Android: SDL's activity owns the window and the input; <see cref="Main"/> runs on SDL's thread and
/// boots the game exactly as the desktop executable does (Waddamburo.App.Boot), with the app's private storage
/// as its home.
/// </summary>
[Activity(MainLauncher = true, Exported = true, Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleInstance,
    ConfigurationChanges = ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.Orientation | ConfigChanges.ScreenLayout | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Touchscreen | ConfigChanges.UiMode)]
public sealed class WaddamburoActivity : SDLActivity
{
    private GameFolder? _gameFolder;

    protected override string[] GetLibraries() => ["SDL3"];

    protected override SDLSurface CreateSDLSurface(Context? p0) => new GameSurface(p0);

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
        _gameFolder?.Picked(requestCode, resultCode, data);
    }

    protected override void Main()
    {
        // Diagnostics: the desktop's WADDAMBURO_* environment variables, as `adb shell am start ... --es env "K=V;K=V"`.
        foreach (var pair in (Intent?.GetStringExtra("env") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var key, var value] && key.StartsWith("WADDAMBURO_", StringComparison.Ordinal))
                System.Environment.SetEnvironmentVariable(key, value);
        Waddamburo.App.Boot.AppHome = Path.Combine(FilesDir!.AbsolutePath, "waddamburo");
        Waddamburo.App.Boot.DeviceName = GameFolder.DeviceName; // the setup's pairing and import report too
        Waddamburo.App.Boot.OpenGameFiles = () => RunOnUiThread(() =>
        {
            try
            {
                StartActivity(new Intent(Intent.ActionView).SetDataAndType(GameFilesProvider.RootUri, "vnd.android.document/root"));
            }
            catch (ActivityNotFoundException exception)
            {
                Console.Error.WriteLine($"No file screen for the game's files: {exception.Message}");
            }
        });
        // Crash files from the first moment (the setup screen too) where the player can reach them; Boot keeps it.
        Waddamburo.Platform.Sdl.ReleaseConsole.CrashFolder = Path.Combine(_gameFolder!.Root, "Waddamburo", "logs");
        // Null: the setup screen was closed without Green.
        if (_gameFolder!.Resolve() is not { } game)
            return;
        Waddamburo.App.Boot.TjaFolder = Path.Combine(_gameFolder.Root, GameFolder.CustomSongs);
        // config.cfg (arcade mode and the rest), crash logs and screenshots: where a file manager or a PC reaches them.
        Waddamburo.App.Boot.UserHome = Path.Combine(_gameFolder.Root, "Waddamburo");
        string[] args = [$"--game-data={game}"];
        // Read again on a restart: a release imported since joins.
        do
            Waddamburo.App.Boot.StockFolders = _gameFolder.ReleaseFolders();
        while (Waddamburo.App.Boot.Run(args) == Waddamburo.App.Boot.RestartExitCode);
    }
}
