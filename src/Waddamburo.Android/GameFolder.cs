using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using Android.Widget;

namespace Waddamburo.Android;

/// <summary>
/// Where the user's game data (Green's USRDIR) is: asked for once with the system's folder picker, then remembered.
/// ponytail: read as a plain path with "All files access" (fine sideloaded, not on the Play Store); the content-URI
/// file layer replaces the permission, the picker stays.
/// </summary>
internal sealed class GameFolder(Activity activity) : IDisposable
{
    private const int PickRequest = 7101, PermissionRequest = 7102;
    private readonly SemaphoreSlim _answered = new(0);
    private global::Android.Net.Uri? _picked;

    private string RememberedFile => Path.Combine(activity.FilesDir!.AbsolutePath, "game-folder.txt");

    /// <summary>The game folder, asking for it (and for the permission to read it) until one is usable. Blocks: call
    /// it from SDL's thread, never the UI thread.</summary>
    public string Resolve()
    {
        // Development: data copied into the app's own files (adb, run-as) wins.
        var copied = Path.Combine(activity.FilesDir!.AbsolutePath, "game");
        if (IsGameFolder(copied))
            return copied;
        ensureReadable();
        if (File.Exists(RememberedFile) && File.ReadAllText(RememberedFile).Trim() is var remembered && IsGameFolder(remembered))
            return remembered;
        var first = true;
        while (true)
        {
            toast(first ? "Choose your Taiko no Tatsujin Green folder (USRDIR)."
                : "That folder has no data/lumendata: choose Green's USRDIR folder.");
            first = false;
            _picked = null;
            activity.RunOnUiThread(() => activity.StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), PickRequest));
            _answered.Wait();
            if (_picked is null || pathOf(_picked) is not { } path)
                continue;
            var root = IsGameFolder(path) ? path : Path.Combine(path, "USRDIR");
            if (!IsGameFolder(root))
                continue;
            File.WriteAllText(RememberedFile, root);
            return root;
        }
    }

    /// <summary>Forget the folder: the next start asks again.</summary>
    public void Forget() => File.Delete(RememberedFile);

    public static bool IsGameFolder(string path) => Directory.Exists(Path.Combine(path, "data", "lumendata", "packed"));

    public void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != PickRequest)
            return;
        _picked = resultCode == Result.Ok ? data?.Data : null;
        _answered.Release();
    }

    public void OnPermissionsResult(int requestCode)
    {
        if (requestCode == PermissionRequest)
            _answered.Release();
    }

    /// <summary>Back from the system's settings (All files access): check again.</summary>
    public void OnResume()
    {
        if (_waitingForSettings)
        {
            _waitingForSettings = false;
            _answered.Release();
        }
    }

    private volatile bool _waitingForSettings;

    // Android 11+: All files access (a settings page); older: the storage permission (Android 10 also needs
    // requestLegacyExternalStorage, in the manifest).
    private void ensureReadable()
    {
        while (true)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                if (global::Android.OS.Environment.IsExternalStorageManager)
                    return;
                toast("Allow Waddamburo to read your game files (All files access).");
                _waitingForSettings = true;
                var settings = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                    global::Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.RunOnUiThread(() => activity.StartActivity(settings));
            }
            else
            {
                if (activity.CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted)
                    return;
                activity.RunOnUiThread(() => activity.RequestPermissions([Manifest.Permission.ReadExternalStorage], PermissionRequest));
            }
            _answered.Wait();
        }
    }

    // A picked tree's path: "primary:Games/USRDIR" is internal storage, "1234-ABCD:..." a memory card.
    private static string? pathOf(global::Android.Net.Uri tree)
    {
        if (DocumentsContract.GetTreeDocumentId(tree) is not { } id || id.Split(':', 2) is not [var volume, var relative])
            return null;
        var root = volume == "primary" ? global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath : $"/storage/{volume}";
        return Path.Combine(root, relative);
    }

    public void Dispose() => _answered.Dispose();

    private void toast(string text) =>
        activity.RunOnUiThread(() => Toast.MakeText(activity, text, ToastLength.Long)!.Show());
}
