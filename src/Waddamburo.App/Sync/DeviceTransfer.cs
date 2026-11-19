using Waddamburo.App.Home;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Sync;

/// <summary>
/// Device management and song sync under home Settings, and the server paired devices pull from.
/// </summary>
internal sealed class DeviceTransfer : IDisposable
{
    private readonly GameShell _shell;
    private readonly IReadOnlyList<SyncLibrary> _libraries;
    private readonly OverlayMenu _menu;
    private SyncServer? _server;
    private CancellationTokenSource? _work;

    private (OverlayTextField Field, Action Changed, Action? Done)? _typing;
    private readonly OverlayTextField _codeField = new(4, static letter => char.IsAsciiDigit(letter) ? letter : null);

    // Pairing (both sides).
    private volatile List<SyncDevice> _pairable = [];
    private PairingListener? _pairing;
    private string? _problem;
    private CancellationTokenSource? _pairDiscovery;
    private OverlayPage? _addDevicePage;
    private (OverlayPage Page, Task<PairedDevice> Request)? _pairAttempt;

    // Syncing.
    private volatile List<SyncClient>? _servers; // null: still looking
    private volatile string _status = "";
    private long _fetched, _total;
    private volatile bool _finished, _failed, _changed;
    private (string Text, TaskCompletionSource<bool> Answer)? _ask;
    private bool _asking;

    public DeviceTransfer(GameShell shell, IReadOnlyList<SyncLibrary> libraries)
    {
        (_shell, _libraries) = (shell, libraries);
        _menu = new OverlayMenu(shell.Application, shell.Options.ServiceFontPath ?? shell.Options.FontPath) { Sound = sound };
        // Paired devices pull from this one while the game runs.
        _server = SyncServer.StartIfPaired(libraries, Boot.DeviceName, shell.Sync.Jobs);
    }

    public bool IsOpen => _menu.IsOpen;

    /// <summary>A field takes typing: Backspace edits it rather than going back.</summary>
    public bool Typing => _typing is not null;

    private static string T(string key, params object[] values) => values.Length == 0 ? Strings.T(key) : Strings.T(key, values);

    private static string gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / 1048576.0:F0} MB";

    private static long availableSpace(string folder)
    {
        // A new library may not have a folder yet. On Android, '/' is a different filesystem from app storage.
        var directory = new DirectoryInfo(folder);
        while (!directory.Exists && directory.Parent is { } parent)
            directory = parent;
        return new DriveInfo(directory.FullName).AvailableFreeSpace;
    }

    public void Open()
    {
        if (SyncWire.Paired().Any(static device => !device.Serves))
            SyncServer.AllowThroughWindowsFirewall();
        _server ??= SyncServer.StartIfPaired(_libraries, Boot.DeviceName, _shell.Sync.Jobs);
        _work = new CancellationTokenSource();
        _servers = null;
        _menu.Open(new OverlayPage(() => T("sync.devices"), deviceRows, Back: Close), home: true);
        var token = _work.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _servers = await SyncClient.FindAsync(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
                {
                    _servers = [];
                }
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
        }, token);
    }

    private OverlayRow[] deviceRows()
    {
        var devices = SyncWire.Paired();
        var servers = _servers;
        var addDevice = new OverlayRow(T("sync.pair.title"), Enter: openAddDevice, Gap: true);
        var connectToPc = new OverlayRow(T("sync.pair_here"), Enter: openPairHere, Gap: true);
        return
        [
            .. devices.Count == 0 ? [new OverlayRow(T("sync.none_paired"), Cyan: true)] :
                devices.Select(device =>
                {
                    var server = servers?.FirstOrDefault(server => server.Device.Id == device.Id);
                    return new OverlayRow(device.Name, Value: () => T(!device.Serves ? "sync.paired"
                        : server is not null ? "sync.online" : servers is null ? "sync.looking" : "sync.offline"),
                        Enter: () => openDevice(device));
                }),
            PlatformInfo.IsMobile ? connectToPc : addDevice,
            PlatformInfo.IsMobile ? addDevice with { Gap = false } : connectToPc with { Gap = false },
            new(T("transfer.back"), Gap: true, Enter: Close),
        ];
    }

    private void openDevice(PairedDevice device)
    {
        if (device.Serves && _servers?.FirstOrDefault(server => server.Device.Id == device.Id) is { } server)
        {
            openLibraries(server, device);
            return;
        }

        _menu.Push(new OverlayPage(() => device.Name, () =>
        [
            .. device.Serves && _servers?.FirstOrDefault(server => server.Device.Id == device.Id) is { } found
                ? [new OverlayRow(T("sync.title"), Enter: () => { _menu.Pop(); openLibraries(found, device); })]
                : Array.Empty<OverlayRow>(),
            unpairRow(device),
            new(T("transfer.back"), Enter: _menu.Pop),
        ], Centred: true, Body: () => !device.Serves ? [(T("sync.can_pull"), OverlayMenu.White)]
            : _servers?.Any(server => server.Device.Id == device.Id) == true ? []
            : [(T("sync.not_found"), OverlayMenu.Cyan)]));
    }

    private OverlayRow unpairRow(PairedDevice device) => new(T("sync.unpair"), Enter: () =>
        confirm(T("sync.unpair.ask", device.Name), () =>
        {
            SyncWire.Unpair(device.Id);
            _menu.Pop();
        }));

    // Nearby devices only appear while they are showing a pairing code.
    private void openAddDevice()
    {
        var discovery = _pairDiscovery = new CancellationTokenSource();
        var token = discovery.Token;
        _pairable = [];
        _addDevicePage = new OverlayPage(() => T("sync.pair.title"), addDeviceRows,
            Centred: true, Back: closeAddDevice, Body: () => [(T("sync.pair.how"), OverlayMenu.White)]);
        _menu.Push(_addDevicePage);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var found = await SyncWire.DiscoverAsync(SyncWire.PairProbe, SyncWire.PairPort, TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    _pairable = found;
                }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
                {
                    _pairable = [];
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }, token);
    }

    private OverlayRow[] addDeviceRows()
    {
        var nearby = _pairable;
        return
        [
            .. nearby.Count == 0 ? [new OverlayRow(T("sync.pair.looking"), Cyan: true)]
                : nearby.Select(device => new OverlayRow(nearby.Count(other => other.Name == device.Name) > 1
                    ? $"{device.Name} ({device.Address})" : device.Name, Enter: () => pickDevice(device))),
            new(T("transfer.back"), Gap: true, Enter: closeAddDevice),
        ];
    }

    private void closeAddDevice()
    {
        stopPairDiscovery();
        _menu.Pop();
    }

    private void stopPairDiscovery()
    {
        _pairDiscovery?.Cancel();
        _pairDiscovery?.Dispose();
        _pairDiscovery = null;
    }

    private void pickDevice(SyncDevice device)
    {
        _problem = null;
        _codeField.Set("");
        void typeCode() => type(_codeField, () => { if (_codeField.Text.Length == 4) { endTyping(); pair(device); } }, () => pair(device));
        _menu.Push(new OverlayPage(() => T("transfer.code.title"), () =>
        [
            new(T("transfer.code.field"), 400, () => _codeField.Shown(_typing?.Field == _codeField), 560, Enter: typeCode),
            new(T("transfer.code.next"), 400, Gap: true, Enter: () => pair(device)),
            new(T("transfer.back"), 400, Enter: _menu.Pop),
        ], Footer: () =>
        [
            (T("transfer.code.how", device.Name), OverlayMenu.White),
            .. _problem is { } problem ? [(problem, OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
        ]));
        typeCode();
    }

    private void pair(SyncDevice device)
    {
        if (_codeField.Text.Length != 4 || _work is null || _pairAttempt is not null || _menu.Page is not { } page)
            return;
        endTyping();
        _problem = T("transfer.code.checking");
        var (code, token) = (_codeField.Text, _work.Token);
        _pairAttempt = (page, PairingListener.PairAsync(device, code, token));
    }

    private bool finishPairing()
    {
        if (_pairAttempt is not { } attempt || !attempt.Request.IsCompleted)
            return false;
        _pairAttempt = null;
        try
        {
            attempt.Request.GetAwaiter().GetResult();
        }
        catch (SyncRefusedException)
        {
            if (ReferenceEquals(_menu.Page, attempt.Page))
                _problem = T("transfer.code.wrong");
            return false;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException)
        {
            if (ReferenceEquals(_menu.Page, attempt.Page))
                _problem = T("transfer.failed", exception.Message);
            return false;
        }

        SyncServer.AllowThroughWindowsFirewall();
        _server ??= SyncServer.StartIfPaired(_libraries, Boot.DeviceName, _shell.Sync.Jobs);
        if (ReferenceEquals(_menu.Page, attempt.Page))
        {
            endTyping();
            _menu.Pop();
        }
        if (ReferenceEquals(_menu.Page, _addDevicePage))
        {
            closeAddDevice();
            return true;
        }
        return false;
    }

    // This device's code until a PC pairs with it.
    private void openPairHere()
    {
        try
        {
            _pairing?.Dispose();
            _pairing = new PairingListener(Boot.DeviceName);
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            _problem = T("transfer.failed", exception.Message);
        }
        _menu.Push(new OverlayPage(() => T("sync.pair_here"), () => [new(T("transfer.back"), Enter: closePairHere)], Centred: true,
            Back: closePairHere, Body: () => _pairing is not { } pairing ? [(_problem ?? "", OverlayMenu.Red)] : pairing.LockedOut
                ? [(T("transfer.receive.locked"), OverlayMenu.Red)]
                :
                [
                    (T("sync.pair_here.how", pairing.Name), OverlayMenu.White),
                    (pairing.Code, OverlayMenu.Yellow),
                    .. SyncWire.Addresses().Count == 0 ? [(T("transfer.receive.offline"), OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
                ]));
    }

    private void closePairHere()
    {
        _pairing?.Dispose();
        _pairing = null;
        _menu.Pop();
    }

    private void openLibraries(SyncClient server, PairedDevice device)
    {
        var wanted = SyncWire.Wanted(device.Id);
        var request = server.LibrariesAsync(_work!.Token);
        _menu.Push(new OverlayPage(() => device.Name, () => request.IsCompletedSuccessfully
            ? libraryRows(server, device, request.Result, wanted)
            :
            [
                .. request.IsCompleted ? Array.Empty<OverlayRow>() : [new OverlayRow(T("sync.libraries.loading"), Cyan: true)],
                unpairRow(device),
                new(T("transfer.back"), Enter: _menu.Pop),
            ], Footer: () =>
        {
            if (request.Exception?.GetBaseException() is { } error)
                return [(T("transfer.failed", error.Message), OverlayMenu.Red)];
            return request.IsCompletedSuccessfully ? [(T("sync.libraries.legend"), OverlayMenu.White)] : [];
        }, Recenter: true));
    }

    private OverlayRow[] libraryRows(SyncClient server, PairedDevice device, List<RemoteLibrary> remote, HashSet<string> wanted)
    {
        var on = T("transfer.on");
        var off = T("transfer.off");
        var statusWidth = Math.Max(on.Length, off.Length);
        return
        [
            .. remote.Select(library =>
            {
                var size = (library.Kind == NijiiroSync.Kind ? "≈" + gb(library.Bytes) : gb(library.Bytes)).PadLeft(9);
                return ReceivedLibraries.FolderOf(device.Id, library) is null
                    ? new OverlayRow(library.Name, Value: () => $"{size}   {T("sync.not_here")}", Cyan: true)
                    : new OverlayRow(library.Name,
                        Value: () => $"{size}   {(wanted.Contains(library.Id) ? on : off).PadRight(statusWidth)}",
                        Step: _ => toggle(server, library, wanted));
            }),
            _syncing is not null
                ? new(T("sync.view_progress"), Gap: true, Enter: () => _menu.Push(progressPage()))
                : new(T("sync.now"), Gap: true, Enter: () => syncNow(server, remote.Where(library => wanted.Contains(library.Id)).ToList())),
            unpairRow(device),
            new(T("transfer.back"), Enter: _menu.Pop),
        ];
    }

    // On: wanted from the next sync. Off: its files go, after asking (Green's never: they are this device's game).
    private void toggle(SyncClient server, RemoteLibrary library, HashSet<string> wanted)
    {
        if (wanted.Add(library.Id))
        {
            SyncWire.SetWanted(server.Device.Id, wanted);
            return;
        }
        void off()
        {
            wanted.Remove(library.Id);
            SyncWire.SetWanted(server.Device.Id, wanted);
        }
        if (library.Kind == "green" || ReceivedLibraries.FolderOf(server.Device.Id, library) is not { } folder || !Directory.Exists(folder))
        {
            off();
            return;
        }
        if (_syncing is not null)
        {
            _menu.Alert(T("sync.remove.in_progress"));
            return;
        }
        confirm(T("sync.remove.ask", library.Name, gb(SyncClient.Local(folder).Values.Sum(static file => file.Size))), () =>
        {
            off();
            ReceivedLibraries.Remove(folder);
            _changed = true;
        });
    }

    private OverlayPage progressPage() => new(() => T("sync.title"), () =>
        [
            .. _finished && _changed ? [new OverlayRow(T("service.import.restart"), Enter: _shell.RequestRestart)] : Array.Empty<OverlayRow>(),
            .. _finished ? [new OverlayRow(T("service.ok"), Enter: _menu.Pop)]
                : new[] { new OverlayRow(T("sync.background"), Enter: _menu.Pop), new OverlayRow(T("transfer.cancel"), Enter: () => _syncing?.Cancel()) },
        ], Centred: true, Body: () =>
        [
            (_status, _failed ? OverlayMenu.Red : _finished ? OverlayMenu.Green : OverlayMenu.White),
            .. Interlocked.Read(ref _total) > 0 && !_finished
                ? [($"{gb(Interlocked.Read(ref _fetched))} / {gb(Interlocked.Read(ref _total))}", OverlayMenu.Cyan)]
                : Array.Empty<(string, RenderColor)>(),
        ]);

    // The sync runs on after its page closes (a card shows its progress); CANCEL stops it.
    private volatile CancellationTokenSource? _syncing;

    private void syncNow(SyncClient server, List<RemoteLibrary> wanted)
    {
        if (_syncing is not null)
        {
            _menu.Push(progressPage()); // already running: its progress
            return;
        }
        (_status, _fetched, _total, _finished, _failed) = (T("transfer.progress.starting"), 0, 0, false, false);
        _menu.Push(progressPage());
        var syncing = _syncing = new CancellationTokenSource();
        var token = syncing.Token;
        var job = _shell.Sync.Jobs.Start(T("job.sync_songs", server.Device.Name), 1000);
        _ = Task.Run(async () =>
        {
            // The card's bar: per mille of the library being fetched.
            using var progress = new Timer(_ => job.Report((int)(Interlocked.Read(ref _fetched) * 1000 / Math.Max(1, Interlocked.Read(ref _total)))),
                null, 500, 500);
            try
            {
                foreach (var library in wanted)
                {
                    if (ReceivedLibraries.FolderOf(server.Device.Id, library) is not { } folder)
                        continue;
                    _status = T(library.Kind == NijiiroSync.Kind ? "sync.nijiiro.preparing" : "sync.progress.comparing", library.Name);
                    var plan = await server.PlanAsync(library, folder, token).ConfigureAwait(false);
                    // A sync that would remove more than half of a library asks first (a PC's folder half gone is
                    // more likely a mistake than a choice).
                    if (plan.Remove.Count > plan.Have / 2 && plan.Remove.Count > 0
                        && !await askAsync(T("sync.remove.many", library.Name, plan.Remove.Count, plan.Have)).ConfigureAwait(false))
                        plan = plan with { Remove = [] };
                    if (plan.Fetch.Count == 0 && plan.Remove.Count == 0)
                        continue;
                    if (plan.FetchBytes > 0)
                    {
                        var free = availableSpace(folder);
                        if (plan.FetchBytes > free)
                            throw new IOException(T("sync.too_big", library.Name, gb(plan.FetchBytes - free)));
                    }
                    _status = T("sync.progress.fetching", library.Name, plan.Fetch.Count, plan.Remove.Count);
                    (_fetched, _total) = (0, plan.FetchBytes);
                    await server.MirrorAsync(library, folder, plan, bytes => Interlocked.Add(ref _fetched, bytes),
                        () => _changed = true, token).ConfigureAwait(false);
                    _changed = true;
                }
                _status = _changed ? T("sync.progress.done") : T("sync.progress.same");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                (_status, _failed) = (T("transfer.progress.cancelled"), true);
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException
                or InvalidDataException or UnauthorizedAccessException)
            {
                (_status, _failed) = (T("transfer.failed", exception.Message), true);
            }
            _finished = true;
            if (_failed)
                job.Fail(_status);
            else
                job.Complete();
            _syncing = null;
            syncing.Dispose();
        }, CancellationToken.None);
    }

    // Asked on the game's thread (Tick); the sync's thread waits for the answer.
    private Task<bool> askAsync(string text)
    {
        if (!IsOpen)
            return Task.FromResult(false); // nobody to ask: nothing is removed this time
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ask = (text, answer);
        return answer.Task;
    }

    private void confirm(string text, Action yes) =>
        _menu.Push(new OverlayPage(() => T("sync.title"), () =>
            [new(T("menu.yes"), Enter: () => { _menu.Pop(); yes(); }), new(T("menu.no"), Enter: _menu.Pop)],
            Centred: true, Modal: true, Body: () => [(text, OverlayMenu.Yellow)]));

    // ---- Closing and input

    public void Close()
    {
        stop();
        _menu.Close();
    }

    private void stop()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        stopPairDiscovery();
        _pairing?.Dispose();
        _pairing = null;
        _ask?.Answer.TrySetResult(false);
        (_ask, _asking) = (null, false);
        endTyping();
    }

    public void Dispose()
    {
        stop();
        _syncing?.Cancel();
        _server?.Dispose();
        _menu.Dispose();
    }

    public void Tick(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        if (finishPairing())
            return;
        if (_typing is { } typing)
        {
            if (escape)
            {
                endTyping();
                _menu.Pop();
                return;
            }

            _menu.Tick(SdlKeyboardSnapshot.Empty, SdlKeyboardSnapshot.Empty, false); // the blink only
            if (typing.Field.Edit(_shell.Application.TakeTypedText(), keys))
                typing.Changed();
            if (keys.IsDown(SdlKeyboardKey.Enter) && _typing is not null)
            {
                endTyping();
                typing.Done?.Invoke();
            }
            return;
        }
        if (_pairing?.PairedWith is not null)
        {
            closePairHere();
            return;
        }
        if (_ask is { } ask && !_asking)
        {
            _asking = true;
            _menu.Push(new OverlayPage(() => T("sync.title"), () =>
                [new(T("menu.yes"), Enter: () => answer(true)), new(T("menu.no"), Enter: () => answer(false))],
                Centred: true, Modal: true, Back: () => answer(false), Body: () => [(ask.Text, OverlayMenu.Yellow)]));

            void answer(bool yes)
            {
                ask.Answer.TrySetResult(yes);
                (_ask, _asking) = (null, false);
                _menu.Pop();
            }
        }
        _menu.Tick(keys, held, escape);
    }

    private void type(OverlayTextField field, Action changed, Action? done = null)
    {
        _typing = (field, changed, done);
        _shell.Application.TakeTypedText();
        _shell.Application.StartTextInput();
    }

    private void endTyping()
    {
        if (_typing is null)
            return;
        _typing = null;
        _shell.Application.StopTextInput();
    }

    private void sound(bool don) => _shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false);

    public List<RenderQuad> Quads() => _menu.Quads();
}
