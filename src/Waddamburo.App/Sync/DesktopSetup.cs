using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Sync;

/// <summary>
/// A PC's first start without Green: the phone's import (<see cref="SetupScreen"/>, <see cref="DeviceImport"/>) with
/// the system's file picker, into the given folder (Green beside Waddamburo). Several files can be picked (an
/// unpacked web-archive download's DATA*.BIN): taken in name order, DATA.BIN (the base) before DATA00010.BIN and on.
/// </summary>
internal static class DesktopSetup
{
    public static bool Run(string green)
    {
        var status = new Cli.GreenSetupProgress(0, 0, Cli.GreenData.FileCount, false);
        using var stop = new CancellationTokenSource();
        // Counted every three seconds off the screen's thread (~14,000 files).
        var poll = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref status, Cli.GreenData.SetupProgress(green));
                try { await Task.Delay(3000, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        });
        var import = new DeviceImport(green, picked => SdlApplication.PickFiles(Strings.T("first_setup.import_filter"),
            "zip;7z;rar;bin;pkg", many: true, files => picked([.. files
                .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .Select(static file => new ImportSource(Path.GetFileName(file), new FileInfo(file).Length, () => File.OpenRead(file)))])));
        try
        {
            return SetupScreen.Run(null, () => Volatile.Read(ref status), green, import: import);
        }
        finally
        {
            stop.Cancel();
            poll.Wait();
        }
    }
}
