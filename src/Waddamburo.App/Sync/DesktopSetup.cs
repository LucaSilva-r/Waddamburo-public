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
        // Counted once, off the screen's thread (what is there already). A copy from the other device or an import knows
        // what it wrote: no count after (a walk of the whole folder stalls the Switch, HOME menu included).
        var counted = false;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = Task.Run(async () =>
        {
            await Task.WhenAny(shown.Task, Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false); // the page first
            Volatile.Write(ref status, Cli.GreenData.SetupProgress(green));
            Volatile.Write(ref counted, true);
            while (!stop.IsCancellationRequested && setup?.Done != true)
                try { await Task.Delay(500, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            if (setup?.Done == true)
                Volatile.Write(ref status, status with { Received = status.Total, Installed = status.Total, Essentials = true });
        });
        try
        {
            return SetupScreen.Run(setup, () => Volatile.Read(ref status), green, import: import, preparing: () => !counted, shown: () => shown.TrySetResult());
        }
        finally
        {
            stop.Cancel();
            poll.Wait();
        }
    }
}
