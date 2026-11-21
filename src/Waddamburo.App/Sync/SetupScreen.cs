using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Sync;

/// <summary>
/// A device's first start without Green, as the update screen looks (the menus' centred page in the bundled font,
/// nothing of Green's): how to pair with a PC and this device's code (then where Green comes from), a bar of what has arrived, and the
/// connection and copy-by-hand hints at the bottom; START ANYWAY once the boot's essentials are in. Closes by itself
/// when Green is complete.
/// </summary>
public static class SetupScreen
{
    /// <summary>
    /// Shows the screen until <paramref name="status"/> says nothing is missing (true), START ANYWAY is picked (true),
    /// or the window is closed (false). <paramref name="status"/> is read every frame: keep it cheap (a cached count).
    /// <paramref name="greenFolder"/>: where a player copies the files by hand instead.
    /// </summary>
    public static bool Run(GreenSetup setup, Func<Cli.GreenSetupProgress> status, string greenFolder,
        string? shot = null, DeviceImport? import = null)
    {
        var frames = 0;
        var fonts = Cli.BundledFonts.Extract(Path.Combine(Boot.AppHome ?? Path.GetTempPath(), "fonts"));
        using var application = new SdlApplication(Hosting.SelfUpdate.WindowTitle, 1280, 720) { OverlayOpen = true };
        using var menu = new OverlayMenu(application, fonts.Service);
        using var painter = new OverlayPainter(application, fonts.Service);
        var startAnyway = false;
        menu.Open(new OverlayPage(() => Strings.T("first_setup.title"), () =>
            [
                .. import is null || import.Busy ? [] : new[] { new OverlayRow(Strings.T("first_setup.import"), Enter: import.Pick) },
                .. status().Essentials && import?.Busy != true
                    ? [new OverlayRow(Strings.T("first_setup.start_anyway"), Enter: () => { startAnyway = true; application.RequestQuit(); })] : Array.Empty<OverlayRow>(),
            ],
            Centred: true, Legend: false, Body: () =>
            {
                var progress = status();
                var filled = progress.Received * 30 / Math.Max(1, progress.Total);
                // Blank lines space it out: the code in the middle of the screen, the bar under it.
                return
                [
                    ("", OverlayMenu.White),
                    .. setup.Message is { } message
                        ? [(message, setup.Failed ? OverlayMenu.Red : OverlayMenu.Cyan), ("", OverlayMenu.White), ("", OverlayMenu.White)]
                        : new[] { (Strings.T("first_setup.step_open", setup.Name), OverlayMenu.White), ("", OverlayMenu.White),
                            (Strings.T("first_setup.code", setup.Code), OverlayMenu.Yellow) },
                    ("", OverlayMenu.White),
                    ("", OverlayMenu.White),
                    ($"[{new string('#', filled)}{new string('-', 30 - filled)}] {Strings.T("first_setup.progress", progress.Received, progress.Total)}", OverlayMenu.Green),
                    .. import?.Message is { } imported ? [("", OverlayMenu.White), (imported.Text, imported.Failed ? OverlayMenu.Red : OverlayMenu.Yellow)]
                        : Array.Empty<(string, RenderColor)>(),
                    .. import?.Detail is { } detail ? [(detail, OverlayMenu.White)] : Array.Empty<(string, RenderColor)>(),
                ];
            }, Footer: () =>
            [
                SyncWire.Addresses().Count > 0 ? (OverlayMenu.upper(Strings.T("first_setup.step_connect")), OverlayMenu.Cyan)
                    : (OverlayMenu.upper(Strings.T("first_setup.offline")), OverlayMenu.Red),
                (OverlayMenu.upper(Strings.T(status().Essentials ? "first_setup.essentials" : "first_setup.auto")), OverlayMenu.Green),
                (OverlayMenu.upper(Strings.T("first_setup.copy", greenFolder)), OverlayMenu.White),
                .. import is null ? [] : new[] { (OverlayMenu.upper(Strings.T("first_setup.import_hint")), OverlayMenu.White) },
            ]), home: true);
        application.Run(_ =>
        {
            // --setup-preview=FILE: one frame saved once its text is drawn, then the screen closes.
            if (shot is not null && ++frames == 120)
                application.CaptureNextFrame = capture => { Tools.ScreenshotWriter.Write(shot, capture); application.RequestQuit(); };
            var frame = menu.Frame();
            if (!PlatformInfo.IsMobile && !application.TouchActive)
                return frame;
            return new RenderFrame(frame.ClearColor, frame.Quads.AddRange(
                TouchDrumOverlay.MenuQuads(painter, application.TouchHits, SdlApplication.TicksNs)), frame.ContentAspectRatio);
        }, keys =>
        {
            menu.Tick(keys, keys, false); // the back button does nothing here: there is nowhere to go back to
            if ((status().Complete || import?.Ready == true) && import?.Busy != true)
                application.RequestQuit();
        });
        return startAnyway || status().Complete || import?.Ready == true;
    }
}

/// <summary>
/// The setup's import of a download on the device itself (<see cref="Cli.GreenImport"/>): <c>pick</c> asks the player
/// for files (Android's file picker) and hands back how to open each, or nothing when they cancel. The files are
/// imported one after another off the screen's thread.
/// </summary>
public sealed class DeviceImport(string root, Action<Action<IReadOnlyList<Func<Stream>>>> pick)
{
    private volatile Cli.GreenImport? _current;
    private volatile string? _done;
    private volatile bool _failed, _busy;
    private int _index, _count;

    public bool Busy => _busy;

    /// <summary>The import is over and nothing but songs' files is missing (a shrunk Green): the game can start.</summary>
    public bool Ready { get; private set; }

    /// <summary>What is being read right now (null when idle).</summary>
    public string? Detail => _busy && _current?.Current is var (entry, taken)
        ? Strings.T(taken ? "first_setup.import_writing" : "first_setup.import_skipping", tail(entry.Replace('\\', '/'), 64))
        : null;

    private static string tail(string text, int length) => text.Length <= length ? text : "…" + text[^(length - 1)..];

    /// <summary>How the import goes, or how it went (null before one).</summary>
    public (string Text, bool Failed)? Message => _current is { } current && _busy
        ? (Strings.T("first_setup.importing", Volatile.Read(ref _index) + 1, _count, current.Progress, current.Files), false)
        : _done is { } done ? (done, _failed) : null;

    public void Pick()
    {
        if (!_busy)
            pick(start);
    }

    private void start(IReadOnlyList<Func<Stream>> sources)
    {
        if (sources.Count == 0 || _busy)
            return;
        (_busy, _count, _index, _done) = (true, sources.Count, 0, null);
        _current = new Cli.GreenImport(root);
        _ = Task.Run(() =>
        {
            try
            {
                // Files cut short when the app was closed midway (complete ones were moved in already).
                if (Directory.Exists(root))
                    foreach (var leftover in Directory.EnumerateFiles(root, "*.import", SearchOption.AllDirectories))
                        File.Delete(leftover);
                foreach (var source in sources)
                {
                    using (var stream = source())
                        _current.Import(stream);
                    Interlocked.Increment(ref _index);
                }
                var missing = Cli.GreenData.Missing(root);
                var required = missing.Count(static path => !Cli.GreenData.IsSongFile(path));
                (_done, _failed) = (missing.Count == 0 ? Strings.T("first_setup.imported", _current.Files)
                    : required == 0 ? Strings.T("first_setup.imported_shrunk", _current.Files, missing.Count)
                    : Strings.T("first_setup.imported_missing", required), false);
                Ready = required == 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Import failed: {exception}");
                (_done, _failed) = (Strings.T("first_setup.import_failed", exception.Message), true);
            }
            finally
            {
                _busy = false;
            }
        });
    }
}

/// <summary>
/// A first start's Green: shows a pairing code until a PC pairs, then mirrors Green from a paired PC that answers (its
/// "green" library, switched on for it), again every few seconds until the screen closes, so a dropped link resumes.
/// </summary>
public sealed class GreenSetup : IDisposable
{
    private readonly PairingListener? _pairing;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _copy;
    private long _fetched, _total;
    private volatile string? _message;

    public GreenSetup(string name)
    {
        Name = name;
        try { _pairing = new PairingListener(name); }
        catch (System.Net.Sockets.SocketException exception) { (_message, Failed) = (Strings.T("transfer.failed", exception.Message), true); }
        _copy = Task.Run(loop);
    }

    public string Name { get; }

    public string Code => _pairing?.Code ?? "----";

    /// <summary>What is happening once paired (null: waiting for a PC to pair).</summary>
    public string? Message => _message is { } message && Interlocked.Read(ref _total) > 0
        ? $"{message} {Interlocked.Read(ref _fetched) / 1048576:N0} / {Interlocked.Read(ref _total) / 1048576:N0} MB" : _message;

    public bool Failed { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        _pairing?.Dispose();
        _copy.GetAwaiter().GetResult();
        _stop.Dispose();
    }

    private async Task loop()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (SyncWire.Paired().Any(static device => device.Serves) && ReceivedLibraries.GreenFolder is { } folder)
                {
                    var servers = await SyncClient.FindAsync(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                    if (servers.Count == 0)
                        (_message, Failed) = (Strings.T("sync.setup.looking") + " " + Strings.T("sync.not_found"), false);
                    foreach (var server in servers.Take(1))
                    {
                        var green = (await server.LibrariesAsync(token).ConfigureAwait(false)).FirstOrDefault(static library => library.Kind == "green");
                        if (green is null)
                        {
                            (_message, Failed) = (Strings.T("sync.setup.no_green", server.Device.Name), true);
                            break;
                        }
                        SyncWire.SetWanted(server.Device.Id, [.. SyncWire.Wanted(server.Device.Id), green.Id]);
                        (_message, Failed) = (Strings.T("sync.setup.receiving", server.Device.Name), false);
                        var plan = await server.PlanAsync(green, folder, token).ConfigureAwait(false);
                        (_fetched, _total) = (0, plan.FetchBytes);
                        await server.MirrorAsync(green, folder, plan, bytes => Interlocked.Add(ref _fetched, bytes), cancellationToken: token).ConfigureAwait(false);
                        _total = 0;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
            {
                (_message, Failed, _total) = (Strings.T("transfer.failed", exception.Message), true, 0);
            }
            try { await Task.Delay(3000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>--setup-preview[=FILE]: the setup screen on a PC, with made-up progress (pairing works: a PC may pair).</summary>
internal static class SetupPreview
{
    public static int Run(string? shot)
    {
        Boot.AppHome ??= Path.Combine(Path.GetTempPath(), "waddamburo-setup-preview");
        SyncWire.Folder = Path.Combine(Boot.AppHome, "sync");
        ReceivedLibraries.GreenFolder = Path.Combine(Boot.AppHome, "device", "Green");
        using var setup = new GreenSetup(Boot.DeviceName);
        var start = Environment.TickCount64;
        const int Total = 14068;
        SetupScreen.Run(setup, () =>
        {
            var arrived = (int)Math.Min(Total, (Environment.TickCount64 - start) / 2);
            return new Cli.GreenSetupProgress(arrived, arrived, Total, arrived > Total / 3);
        }, "Android/data/org.waddamburo.game/files/Green", shot,
            new DeviceImport(ReceivedLibraries.GreenFolder, static _ => { })); // the import row, picking nothing
        return 0;
    }
}
