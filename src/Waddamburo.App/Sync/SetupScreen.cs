using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Sync;

/// <summary>
/// A device's first start without Green, as the update screen looks (the menus' centred page in the bundled font,
/// nothing of Green's). With an import (a phone): a welcome asking where Green comes from, then either the import (what
/// to pick, Android's file picker, its progress) or the PC's pairing page. Without: the pairing page alone (this
/// device's code, a bar of what has arrived, the connection and copy-by-hand hints). START ANYWAY once the boot's
/// essentials are in; closes by itself when Green is complete.
/// </summary>
public static class SetupScreen
{
    /// <summary>
    /// Shows the screen until <paramref name="status"/> says nothing is missing (true), START ANYWAY is picked (true),
    /// or the window is closed (false). <paramref name="status"/> is read every frame: keep it cheap (a cached count).
    /// <paramref name="greenFolder"/>: where a player copies the files by hand instead. No <paramref name="setup"/> (a
    /// PC's first start): the import alone, no pairing.
    /// </summary>
    // preparing: true while the setup is not ready yet (its first look at the folder): a page with nothing to pick until
    // then (the Switch is slow to answer meanwhile). shown: once that page is on screen, so the look can start.
    public static bool Run(GreenSetup? setup, Func<Cli.GreenSetupProgress> status, string greenFolder,
        string? shot = null, DeviceImport? import = null, Func<bool>? preparing = null, Action? shown = null)
    {
        var frames = 0;
        var fonts = Cli.BundledFonts.Extract(Path.Combine(Boot.AppHome ?? Path.GetTempPath(), "fonts"));
        using var application = new SdlApplication(Hosting.SelfUpdate.WindowTitle, 1280, 720) { OverlayOpen = true };
        // A controller's buttons as the game's defaults bind them (no settings yet): A, the drum's centre, confirms.
        var defaults = new Waddamburo.Game.Flow.ArcadeSettings();
        application.Bindings = SdlInputBindings.Parse(defaults.Controls, defaults.Buttons, null);
        application.PadsAsGamepad = !defaults.PadMenusAsDrum; // as GameShell.ApplyControls: the D-pad moves, not drums
        application.MenuInput = true; // all menus here
        using var menu = new OverlayMenu(application, fonts.Service);
        using var painter = new OverlayPainter(application, fonts.Service);
        var startAnyway = false;
        OverlayRow[] anyway() => status().Essentials && import?.Busy != true
            ? [new OverlayRow(Strings.T("first_setup.start_anyway"), Enter: () => { startAnyway = true; application.RequestQuit(); })] : [];
        (string, RenderColor) blank = ("", OverlayMenu.White);
        // Asked every frame: the network's state at most every two seconds (a system call each time).
        long? onlineAt = null;
        var isOnline = false;
        bool online()
        {
            if (onlineAt is not { } at || Environment.TickCount64 - at > 2000)
                (onlineAt, isOnline) = (Environment.TickCount64, SyncWire.Addresses().Count > 0);
            return isOnline;
        }
        (string, RenderColor) bar()
        {
            var progress = status();
            // While a copy from the other device runs: its bytes (the files are counted once it is over).
            var filled = setup?.Fraction is { } fraction ? (int)(fraction * 30) : progress.Received * 30 / Math.Max(1, progress.Total);
            return ($"[{new string('#', filled)}{new string('-', 30 - filled)}] {Strings.T("first_setup.progress", progress.Received, progress.Total)}", OverlayMenu.Green);
        }

        // From a PC: this device's pairing code, the bar, how to connect.
        var fromPc = new OverlayPage(() => Strings.T("first_setup.title"), () =>
            [
                .. anyway(),
                .. import is null ? [] : new[] { new OverlayRow(Strings.T("first_setup.import_back"), Enter: menu.Pop) },
            ],
            Centred: true, Legend: false, Body: () =>
            [
                blank,
                .. setup!.Message is { } message
                    ? [(message, setup.Failed ? OverlayMenu.Red : OverlayMenu.Cyan), blank, blank]
                    : new[] { (Strings.T("first_setup.step_open", setup.Name), OverlayMenu.White), blank,
                        (Strings.T("first_setup.code", setup.Code), OverlayMenu.Yellow) },
                blank,
                blank,
                bar(),
            ], Footer: () =>
            [
                online() ? (OverlayMenu.upper(Strings.T("first_setup.step_connect")), OverlayMenu.Cyan)
                    : (OverlayMenu.upper(Strings.T("first_setup.offline")), OverlayMenu.Red),
                (OverlayMenu.upper(Strings.T(status().Essentials ? "first_setup.essentials" : "first_setup.auto")), OverlayMenu.Green),
                (OverlayMenu.upper(Strings.T("first_setup.copy", greenFolder)), OverlayMenu.White),
            ]);
        if (import is null)
            menu.Open(fromPc, home: true);
        else
        {
            if (PlatformInfo.IsSwitch) // no system file picker: the SD card's folders on the menu
                import.Picker = picked => browse(menu, "/", 0, picked);
            // The import's own page: the bar and what is being read; picking again or going back once it is over.
            var importing = new OverlayPage(() => Strings.T("first_setup.import_title"), () =>
                import.Busy ? [] :
                [
                    .. import.Ready ? [new OverlayRow(Strings.T("first_setup.import_start"), Enter: application.RequestQuit)] : anyway(),
                    new OverlayRow(Strings.T(import.Message is null ? "first_setup.import_pick" : "first_setup.import_again"), Enter: import.Pick),
                    // The report to send for help, in the system's file screen.
                    .. import.Report is not null && Boot.OpenGameFiles is { } files ? [new OverlayRow(Strings.T("menu.game_files"), Enter: files)] : Array.Empty<OverlayRow>(),
                    new OverlayRow(Strings.T("first_setup.import_back"), Enter: menu.Pop),
                ],
                Centred: true, Legend: false, Body: () =>
                [
                    blank,
                    bar(),
                    blank,
                    .. import.Message is { } imported ? [(imported.Text, imported.Failed ? OverlayMenu.Red : OverlayMenu.Yellow)]
                        : new[] { (Strings.T("first_setup.import_waiting"), OverlayMenu.White) },
                    .. import.Detail is { } detail ? [(detail, OverlayMenu.White)] : Array.Empty<(string, RenderColor)>(),
                    .. import.Note is { } note ? [blank, (note, OverlayMenu.Cyan)] : Array.Empty<(string, RenderColor)>(),
                    .. import.Report is { } report ? [blank, (Strings.T("first_setup.import_report", report), OverlayMenu.White)] : Array.Empty<(string, RenderColor)>(),
                ], Footer: () => import.Busy ? [(OverlayMenu.upper(Strings.T("first_setup.import_keep_open")), OverlayMenu.Cyan)] : []);
            // What the import does and what to pick, before Android's file picker opens.
            var steps = new OverlayPage(() => Strings.T("first_setup.import_title"), () =>
                [
                    new OverlayRow(Strings.T("service.ok"), Enter: () => { menu.Pop(); menu.Push(importing); import.Pick(); }),
                    new OverlayRow(Strings.T("first_setup.import_back"), Enter: menu.Pop),
                ],
                Centred: true, Legend: false, Body: () =>
                [
                    blank,
                    (Strings.T("first_setup.import_auto"), OverlayMenu.Yellow),
                    blank,
                    (Strings.T(desktop("first_setup.import_step1", "first_setup.import_step1_pc")), OverlayMenu.White),
                    (Strings.T(desktop("first_setup.import_step2", "first_setup.import_step2_pc")), OverlayMenu.White),
                    (Strings.T("first_setup.import_step3"), OverlayMenu.White),
                    blank,
                    (Strings.T("first_setup.import_after"), OverlayMenu.Cyan),
                ]);
            // Welcome: where Green's files come from.
            menu.Open(new OverlayPage(() => Strings.T("first_setup.welcome_title"), () =>
                [
                    new OverlayRow(Strings.T(desktop("first_setup.choose_import", "first_setup.choose_import_pc")), Enter: () => menu.Push(steps)),
                    .. setup is null ? [] : new[] { new OverlayRow(Strings.T("first_setup.choose_pc"), Enter: () => menu.Push(fromPc)) },
                    .. anyway(),
                ],
                Centred: true, Legend: false, Body: () =>
                [
                    blank,
                    (Strings.T("first_setup.welcome"), OverlayMenu.White),
                    blank,
                    (Strings.T("first_setup.welcome_ask"), OverlayMenu.Yellow),
                    // A PC: an extracted Green is used where it is, Waddamburo dropped into its USRDIR.
                    .. PlatformInfo.IsMobile ? [] : new[] { blank, (Strings.T("first_setup.welcome_extracted_pc"), OverlayMenu.Cyan) },
                ]), home: true);
            if (Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_PAGE") is { } page) // --setup-preview of the other pages
                foreach (var next in page switch { "steps" => [steps], "importing" => [steps, importing], "pc" => new[] { fromPc }, _ => [] })
                    menu.Push(next);
        }
        var waiting = new OverlayPage(() => Strings.T("first_setup.preparing"), static () => [], Centred: true, Legend: false);
        if (preparing?.Invoke() == true)
            menu.Push(waiting);
        var drawn = 0;
        application.Run(_ =>
        {
            if (++drawn == 3) // presented by now
                shown?.Invoke();
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
            // Presses, not held keys: a held OK would pick on the next page too. The back button does nothing here.
            if (menu.Page == waiting)
            {
                if (preparing?.Invoke() != true)
                    menu.Pop();
                return;
            }
            var presses = new SdlKeyboardSnapshot(keys.Presses.Select(static press => press.Key));
            menu.Tick(presses, keys, menu.Page?.Tag == Browser && presses.IsDown(SdlKeyboardKey.Escape));
            // Complete from a PC: on by itself. An import's result stays until START (its notes are read first).
            if (status().Complete && import is not { Busy: true } && import?.Message is null)
                application.RequestQuit();
        });
        return startAnyway || status().Complete || import?.Ready == true;
    }

    // The PC's wording of a phone's line (its own picker, several files, no "this phone").
    private static string desktop(string phone, string pc) => PlatformInfo.IsMobile ? phone : pc;
    private static readonly string[] Dumps = [".zip", ".7z", ".rar", ".bin", ".pkg"];
    private static readonly object Browser = new(); // the browser's pages: B goes up a folder there

    // One folder of the SD card: its folders, then the game files in it (A picks one; a folder of several, a DATA*.BIN
    // set, can be taken whole). depth: the browser's pages below this one, all closed on a pick.
    private static void browse(OverlayMenu menu, string folder, int depth, Action<IReadOnlyList<ImportSource>> picked)
    {
        string[] folders, files;
        try
        {
            folders = [.. Directory.EnumerateDirectories(folder).Where(static path => !Path.GetFileName(path).StartsWith('.'))
                .Order(StringComparer.OrdinalIgnoreCase)];
            files = [.. Directory.EnumerateFiles(folder).Where(static path => Dumps.Contains(Path.GetExtension(path).ToLowerInvariant()))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            (folders, files) = ([], []);
        }
        void pick(IEnumerable<string> paths)
        {
            for (var i = 0; i <= depth; i++)
                menu.Pop();
            picked([.. paths.Select(static path => new ImportSource(Path.GetFileName(path), new FileInfo(path).Length, () => File.OpenRead(path)))]);
        }
        // Built once: the rows are asked for every frame, and the SD card answers slowly while the setup counts files.
        OverlayRow[] rows =
        [
            new OverlayRow("..", Cyan: true, Enter: menu.Pop), // up a folder (at the card's root: out of the browser)
            .. files.Length > 1 ? [new OverlayRow(Strings.T("first_setup.browse_all", files.Length), Enter: () => pick(files))] : Array.Empty<OverlayRow>(),
            .. files.Select(path => (path, size: $"{new FileInfo(path).Length / 1048576.0:F0} MB"))
                .Select(file => new OverlayRow(Path.GetFileName(file.path), Value: () => file.size, Enter: () => pick([file.path]))),
            .. folders.Select(path => new OverlayRow(Path.GetFileName(path) + "/", Cyan: true, Enter: () => browse(menu, path, depth + 1, picked))),
            .. folders.Length + files.Length == 0 ? [new OverlayRow(Strings.T("first_setup.browse_empty"))] : Array.Empty<OverlayRow>(),
        ];
        menu.Push(new OverlayPage(() => folder, () => rows, Tag: Browser));
    }
}

/// <summary>A file the player picked: its name and size as the system reports them (for the report), and how to open it.</summary>
public sealed record ImportSource(string Name, long? Size, Func<Stream> Open);

/// <summary>
/// The setup's import of a download on the device itself (<see cref="Cli.GreenImport"/>): <c>pick</c> asks the player
/// for files (Android's file picker) and hands back the picked ones, or nothing when they cancel. The files are
/// imported one after another off the screen's thread. Every import leaves a report in the logs (what was picked, how
/// far it got, what is missing, the error in full), named to the player when something went wrong, to send for help.
/// </summary>
public sealed class DeviceImport(string root, Action<Action<IReadOnlyList<ImportSource>>> pick)
{
    /// <summary>How files are picked (the Switch's: the setup screen's own SD card browser).</summary>
    public Action<Action<IReadOnlyList<ImportSource>>> Picker { get; set; } = pick;

    private volatile Cli.GreenImport? _current;
    private volatile string? _done;
    private volatile bool _failed, _busy;
    private int _index, _count;

    public bool Busy => _busy;

    /// <summary>The import is over and nothing but songs' files is missing (a shrunk Green): the game can start.</summary>
    public bool Ready { get; private set; }

    /// <summary>What is being read right now (null when idle).</summary>
    public string? Detail => !_busy ? null
        : _current?.Current is var (entry, taken)
            ? Strings.T(taken ? "first_setup.import_writing" : "first_setup.import_skipping", tail(entry.Replace('\\', '/'), 64))
            : Strings.T("first_setup.import_opening");

    private static string tail(string text, int length) => text.Length <= length ? text : "…" + text[^(length - 1)..];

    /// <summary>What the player should know about the imported Green (null: nothing).</summary>
    public string? Note { get; private set; }

    /// <summary>Where the report of an import that went wrong is, to send for help (null: nothing went wrong).</summary>
    public string? Report { get; private set; }

    /// <summary>How the import goes, or how it went (null before one).</summary>
    public (string Text, bool Failed)? Message => _current is { } current && _busy
        ? (Strings.T("first_setup.importing", Volatile.Read(ref _index) + 1, _count, current.Progress, current.Files), false)
        : _done is { } done ? (done, _failed) : null;

    public void Pick()
    {
        if (!_busy)
            Picker(start);
    }

    private void start(IReadOnlyList<ImportSource> sources)
    {
        if (sources.Count == 0 || _busy)
            return;
        (_busy, _count, _index, _done, Note, Report) = (true, sources.Count, 0, null, null, null);
        var import = _current = new Cli.GreenImport(root);
        _ = Task.Run(() =>
        {
            var report = new System.Text.StringBuilder();
            var started = DateTimeOffset.Now;
            var problem = false;
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Waddamburo {BuildInfo.Version} import, {started:yyyy-MM-dd HH:mm:ss zzz}");
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Device: {Boot.DeviceName}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, "
                + $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Green folder: {root}");
            try
            {
                // The folder's own volume: its root "/" is Android's system partition (reported as 0 MB free).
                var volume = new DriveInfo(Directory.Exists(root) ? Path.GetFullPath(root) : Path.GetDirectoryName(Path.GetFullPath(root))!);
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Free space there: {volume.AvailableFreeSpace / 1048576:N0} MB");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            try
            {
                // Files cut short when the app was closed midway (complete ones were moved in already).
                if (Directory.Exists(root))
                    foreach (var leftover in Directory.EnumerateFiles(root, "*.import", SearchOption.AllDirectories))
                        File.Delete(leftover);
                string? broken = null;
                foreach (var source in sources)
                {
                    var before = import.Files;
                    var sourceStarted = DateTimeOffset.Now;
                    var progress = 0d; // how far through the file, taken before it closes
                    report.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Source: {source.Name} ({(source.Size is { } size ? $"{size:N0} bytes" : "size unknown")})");
                    try
                    {
                        using (var stream = source.Open())
                        {
                            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Opened: {stream.Length:N0} bytes, seekable {stream.CanSeek}");
                            try
                            {
                                import.Import(stream);
                            }
                            finally
                            {
                                progress = import.Progress;
                            }
                        }
                        report.AppendLine("Result: read to the end");
                    }
                    // A damaged download (a solid archive can't be read past the damage): what came before it is kept,
                    // and it may well be enough (a Green whose missing files are only songs plays).
                    // (SharpCompress's own exceptions, its LZMA "Data Error" among them, are internal: matched by namespace.)
                    catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException
                        || exception.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true)
                    {
                        Console.Error.WriteLine($"Import broke off at {import.Current?.Entry}: {exception}");
                        broken = import.Current?.Entry is { } entry ? tail(entry.Replace('\\', '/'), 48) : exception.Message;
                        problem = true;
                        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Result: broke off (a damaged download?) at {import.Current?.Entry ?? "the start"}").AppendLine(exception.ToString());
                    }
                    finally
                    {
                        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Format: {import.Kind ?? "not recognised"}; entries read {import.Entries:N0}; Green's files taken "
                            + $"{import.Files - before:N0}; read {progress:P1} of it; {(DateTimeOffset.Now - sourceStarted).TotalSeconds:N0} s");
                    }
                    Interlocked.Increment(ref _index);
                }
                var missing = Cli.GreenData.Missing(root);
                var required = missing.Where(static path => !Cli.GreenData.IsSongFile(path)).ToList();
                (_done, _failed) = (broken is not null && required.Count > 0 ? Strings.T("first_setup.import_damaged", broken, required.Count)
                    : missing.Count == 0 ? Strings.T("first_setup.imported", import.Files)
                    : required.Count == 0 ? Strings.T("first_setup.imported_shrunk", import.Files, missing.Count)
                    : Strings.T("first_setup.imported_missing", required.Count), broken is not null && required.Count > 0);
                Ready = required.Count == 0;
                problem |= required.Count > 0;
                var aiBattle = Cli.GreenData.HasAiBattle(root);
                Note = broken is not null && Ready ? Strings.T("first_setup.import_damaged_songs", broken)
                    : Ready && !aiBattle ? Strings.T("first_setup.import_no_ai_battle") : null;
                report.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Green after the import: {Cli.GreenData.FileCount - missing.Count:N0} of {Cli.GreenData.FileCount:N0} release files; "
                    + $"missing {required.Count:N0} required, {missing.Count - required.Count:N0} song files; AI battle {(aiBattle ? "present" : "absent")}");
                foreach (var path in required.Take(200))
                    report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  missing {path}");
                if (required.Count > 200)
                    report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  ... and {required.Count - 200:N0} more");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Import failed: {exception}");
                (_done, _failed) = (Strings.T("first_setup.import_failed", exception.Message), true);
                problem = true;
                report.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Failed at {import.Current?.Entry ?? "the start"}").AppendLine(exception.ToString());
            }
            finally
            {
                var written = writeReport(report.ToString(), started);
                Report = problem ? written : null;
                _busy = false;
            }
        });
    }

    // The report beside the crash logs (the player's folder on a phone); its path as the player finds it, or null.
    private string? writeReport(string text, DateTimeOffset started)
    {
        try
        {
            var folder = Waddamburo.Platform.Sdl.ReleaseConsole.CrashFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"import-{started:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text);
            Console.WriteLine($"Import report: {path}");
            // On a phone, from the app's folder (Android/data/<app>/files) a file manager or a PC opens.
            var parent = Path.GetDirectoryName(Path.GetFullPath(root));
            return parent is not null && path.StartsWith(parent, StringComparison.Ordinal) ? Path.GetRelativePath(parent, path) : path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Import report not written: {exception.Message}");
            return null;
        }
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
    public string? Message => _message is { } message && Interlocked.Read(ref _total) is > 0 and var total
        ? Interlocked.Read(ref _fetched) >= total ? Strings.T("sync.progress.finishing", "Green") // moving files into place
            : $"{message} {Interlocked.Read(ref _fetched) / 1048576:N0} / {total / 1048576:N0} MB"
        : _message;

    public bool Failed { get; private set; }

    /// <summary>How far a copy from the other device is (0-1), or null when none runs.</summary>
    public double? Fraction => Interlocked.Read(ref _total) is > 0 and var total ? Math.Min(1, Interlocked.Read(ref _fetched) / (double)total) : null;

    /// <summary>Green came whole from the other device (its listing, mirrored): nothing to count.</summary>
    public bool Done { get; private set; }

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
                        Done = true;
                        return; // planning again would compare the whole folder every few seconds
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
