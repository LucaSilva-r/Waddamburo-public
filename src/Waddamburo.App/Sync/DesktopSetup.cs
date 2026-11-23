using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Sync;

/// <summary>
/// A PC's first start without Green: the phone's import (<see cref="SetupScreen"/>, <see cref="DeviceImport"/>) with
/// the system's file picker, into the given folder (Green beside Waddamburo). Several files can be picked (an
/// unpacked web-archive download's DATA*.BIN): taken in name order, DATA.BIN (the base) before DATA00010.BIN and on.
/// </summary>
internal static class DesktopSetup
{
    /// <param name="green">The folder Green is imported (or copied) into.</param>
    /// <param name="pairing">Green can also come from a paired device (the Switch: like a phone, it pairs with a PC).</param>
    public static bool Run(string green, bool pairing = false)
    {
        if (pairing)
        {
            // Where the game's own boot keeps them (GameDataLayout's CacheHome): the pair outlives the setup.
            SyncWire.Folder = Path.Combine(green, "waddamburo", "sync");
            ReceivedLibraries.GreenFolder = green;
        }
        using var setup = pairing ? new GreenSetup(Boot.DeviceName) : null;
        var status = new Cli.GreenSetupProgress(0, 0, Cli.GreenData.FileCount, false);
        using var stop = new CancellationTokenSource();
        var import = new DeviceImport(green, picked => SdlApplication.PickFiles(Strings.T("first_setup.import_filter"),
            "zip;7z;rar;bin;pkg", many: true, files => picked([.. files
                .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .Select(static file => new ImportSource(Path.GetFileName(file), new FileInfo(file).Length, () => File.OpenRead(file)))])));
        // Counted off the screen's thread: once, then every 3 s while files arrive (an import, or a copy from the other
        // device) and once more after. Nothing else changes them while the setup is open, and a walk of the whole folder
        // every few seconds stalled the Switch.
        var poll = Task.Run(async () =>
        {
            var wasBusy = true; // the first count
            while (!stop.IsCancellationRequested)
            {
                var busy = import.Busy || setup?.Receiving == true;
                if (busy || wasBusy)
                    Volatile.Write(ref status, Cli.GreenData.SetupProgress(green));
                wasBusy = busy;
                try { await Task.Delay(busy ? 3000 : 500, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        });
        try
        {
            return SetupScreen.Run(setup, () => Volatile.Read(ref status), green, import: import);
        }
        finally
        {
            stop.Cancel();
            poll.Wait();
        }
    }
}
