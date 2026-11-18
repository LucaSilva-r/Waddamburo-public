using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.App.Sync;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song sync's pages (see song-sync-plan.md) over the overlay menu, and the server paired devices pull from.
/// Pair a device (the PC): devices showing a pairing code, the code, the paired devices (unpair). Sync songs (the
/// phone): the paired PCs that answer, a PC's libraries with a switch each (off removes, after asking), SYNC NOW;
/// PAIR WITH A PC shows this device's code. A field takes typing once Enter is pressed on it; only Enter ends it.
/// </summary>
internal sealed class DeviceTransfer : IInputOverlay, IDisposable
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
    private volatile string? _problem;
    private volatile bool _paired;

    // Syncing.
    private volatile List<SyncClient>? _servers; // null: still looking
    private SyncClient? _source;
    private volatile List<RemoteLibrary>? _remote;
    private HashSet<string> _wanted = [];
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
        _server = SyncServer.StartIfPaired(libraries, Boot.DeviceName);
    }

    public bool IsOpen => _menu.IsOpen;

    /// <summary>A field takes typing: Backspace edits it rather than going back.</summary>
    public bool Typing => _typing is not null;

    private static string T(string key, params object[] values) => values.Length == 0 ? Strings.T(key) : Strings.T(key, values);

    private static string gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / 1048576.0:F0} MB";

    private CancellationToken restart()
    {
        stop();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    // ---- Pair a device (the side holding the libraries)

    public void OpenPair()
    {
        var token = restart();
        (_pairable, _problem) = ([], null);
        _menu.Open(new OverlayPage(() => T("sync.pair.title"), pairRows, Centred: true,
            Body: () => [(T("sync.pair.how"), OverlayMenu.White),
                .. OperatingSystem.IsLinux() ? [(T("sync.firewall"), OverlayMenu.Cyan)] : Array.Empty<(string, RenderColor)>()]), home: true);
        // Looked for again every two seconds while open: a device that opens its pairing page later shows up.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _pairable = await SyncWire.DiscoverAsync(SyncWire.PairProbe, SyncWire.PairPort, TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    private OverlayRow[] pairRows() =>
    [
        .. _pairable.Count == 0
            ? [new OverlayRow(T("sync.pair.looking"), Cyan: true)]
            : _pairable.Select(device => new OverlayRow($"{device.Name}  ({device.Address})", Enter: () => pickDevice(device))),
        .. pairedRows(serves: false),
        new(T("transfer.back"), Gap: true, Enter: Close),
    ];

    // The paired devices of one side, each unpaired with Enter (after asking).
    private IEnumerable<OverlayRow> pairedRows(bool serves) => SyncWire.Paired().Where(device => device.Serves == serves)
        .Select((device, index) => new OverlayRow(T("sync.paired", device.Name), Gap: index == 0, Value: () => T("sync.unpair"), ValueX: 900,
            Enter: () => confirm(T("sync.unpair.ask", device.Name), () => SyncWire.Unpair(device.Id))));

    private void pickDevice(SyncDevice device)
    {
        _problem = null;
        _codeField.Set("");
        void typeCode() => type(_codeField, () => { if (_codeField.Text.Length == 4) { endTyping(); pair(device); } }, () => pair(device));
        _menu.Push(new OverlayPage(() => T("transfer.code.title"), () =>
        [
            new(T("transfer.code.field"), 400, () => _codeField.Shown(_typing?.Field == _codeField), 560, Enter: typeCode),
            new(T("transfer.code.next"), 400, Gap: true, Enter: () => pair(device)),
        ], Footer: () =>
        [
            (T("transfer.code.how", device.Name), OverlayMenu.White),
            .. _problem is { } problem ? [(problem, OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
        ]));
        typeCode();
    }

    private void pair(SyncDevice device)
    {
        if (_codeField.Text.Length != 4 || _work is null)
            return;
        endTyping();
        _problem = T("transfer.code.checking");
        var (code, token) = (_codeField.Text, _work.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                await PairingListener.PairAsync(device, code, token).ConfigureAwait(false);
                (_problem, _paired) = (null, true); // the next tick says so (pages change on the game's thread)
            }
            catch (SyncRefusedException)
            {
                _problem = T("transfer.code.wrong");
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException)
            {
                if (!token.IsCancellationRequested)
                    _problem = T("transfer.failed", exception.Message);
            }
        }, token);
    }

    // ---- Sync songs (the side that pulls)

    public void OpenSync()
    {
        var token = restart();
        _servers = null;
        _menu.Open(new OverlayPage(() => T("sync.title"), serverRows, Centred: true,
            Body: () => [(T("sync.how"), OverlayMenu.White),
                // A paired PC that never answers: most often a Linux firewall.
                .. _servers is { } found && SyncWire.Paired().Any(device => device.Serves && found.All(server => server.Device.Id != device.Id))
                    ? [(T("sync.not_found"), OverlayMenu.Cyan)] : Array.Empty<(string, RenderColor)>()]), home: true);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _servers = await SyncClient.FindAsync(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    private OverlayRow[] serverRows()
    {
        var paired = SyncWire.Paired().Where(static device => device.Serves).ToList();
        var servers = _servers;
        return
        [
            .. paired.Count == 0 ? [new OverlayRow(T("sync.none_paired"), Cyan: true)]
                : servers is null ? [new OverlayRow(T("sync.looking"), Cyan: true)]
                : paired.Select(device => servers.FirstOrDefault(server => server.Device.Id == device.Id) is { } server
                    ? new OverlayRow(device.Name, Value: () => T("sync.online"), ValueX: 900, Enter: () => openLibraries(server))
                    : new OverlayRow(device.Name, Value: () => T("sync.offline"), ValueX: 900, Cyan: true)),
            new(T("sync.pair_here"), Gap: true, Enter: openPairHere),
            .. pairedRows(serves: true),
            new(T("transfer.back"), Gap: true, Enter: Close),
        ];
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

    private void openLibraries(SyncClient server)
    {
        (_source, _remote, _problem) = (server, null, null);
        _wanted = SyncWire.Wanted(server.Device.Id);
        var token = _work!.Token;
        _ = Task.Run(async () =>
        {
            try { _remote = await server.LibrariesAsync(token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                    _problem = T("transfer.failed", exception.Message);
            }
        }, token);
        _menu.Push(new OverlayPage(() => server.Device.Name, libraryRows, Footer: () =>
        [
            (T("sync.libraries.legend"), OverlayMenu.White),
            .. _problem is { } problem ? [(problem, OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
        ]));
    }

    private OverlayRow[] libraryRows()
    {
        if (_remote is not { } remote || _source is not { } server)
            return [new(_problem is null ? T("sync.libraries.loading") : T("transfer.back"), Enter: _problem is null ? null : _menu.Pop, Cyan: _problem is null)];
        return
        [
            .. remote.Select(library => ReceivedLibraries.FolderOf(server.Device.Id, library) is null
                ? new OverlayRow($"{library.Name}  {gb(library.Bytes)}", Value: () => T("sync.not_here"), ValueX: 900, Cyan: true)
                : new OverlayRow($"{library.Name}  {gb(library.Bytes)}", Value: () => _wanted.Contains(library.Id) ? T("transfer.on") : T("transfer.off"),
                    ValueX: 900, Step: _ => toggle(server, library), Enter: () => toggle(server, library))),
            new(T("sync.now"), Gap: true, Enter: () => syncNow(server, remote)),
            new(T("transfer.back"), Enter: _menu.Pop),
        ];
    }

    // On: wanted from the next sync. Off: its files go, after asking (Green's never: they are this device's game).
    private void toggle(SyncClient server, RemoteLibrary library)
    {
        if (!_wanted.Contains(library.Id))
        {
            _wanted.Add(library.Id);
            SyncWire.SetWanted(server.Device.Id, _wanted);
            return;
        }
        void off()
        {
            _wanted.Remove(library.Id);
            SyncWire.SetWanted(server.Device.Id, _wanted);
        }
        if (library.Kind == "green" || ReceivedLibraries.FolderOf(server.Device.Id, library) is not { } folder || !Directory.Exists(folder))
        {
            off();
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
                : new[] { new OverlayRow(T("sync.background"), Enter: Close), new OverlayRow(T("transfer.cancel"), Enter: () => _syncing?.Cancel()) },
        ], Centred: true, Back: () => { if (_finished) _menu.Pop(); else Close(); }, Body: () =>
        [
            (_status, _failed ? OverlayMenu.Red : _finished ? OverlayMenu.Green : OverlayMenu.White),
            .. Interlocked.Read(ref _total) > 0 && !_finished
                ? [($"{gb(Interlocked.Read(ref _fetched))} / {gb(Interlocked.Read(ref _total))}", OverlayMenu.Cyan)]
                : Array.Empty<(string, RenderColor)>(),
        ]);

    // The sync runs on after its page closes (a card shows its progress); CANCEL stops it.
    private CancellationTokenSource? _syncing;

    private void syncNow(SyncClient server, List<RemoteLibrary> remote)
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
        var wanted = remote.Where(library => _wanted.Contains(library.Id)).ToList();
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
                    _status = T("sync.progress.comparing", library.Name);
                    var plan = await server.PlanAsync(library, folder, token).ConfigureAwait(false);
                    // A sync that would remove more than half of a library asks first (a PC's folder half gone is
                    // more likely a mistake than a choice).
                    if (plan.Remove.Count > plan.Have / 2 && plan.Remove.Count > 0
                        && !await askAsync(T("sync.remove.many", library.Name, plan.Remove.Count, plan.Have)).ConfigureAwait(false))
                        plan = plan with { Remove = [] };
                    if (plan.Fetch.Count == 0 && plan.Remove.Count == 0)
                        continue;
                    var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
                    if (plan.FetchBytes > free)
                        throw new IOException(T("sync.too_big", library.Name, gb(plan.FetchBytes - free)));
                    _status = T("sync.progress.fetching", library.Name, plan.Fetch.Count, plan.Remove.Count);
                    (_fetched, _total) = (0, plan.FetchBytes);
                    await server.MirrorAsync(library, folder, plan, bytes => Interlocked.Add(ref _fetched, bytes), token).ConfigureAwait(false);
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
        _work = null;
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

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
            return false;
        if (_typing is { } typing)
        {
            _menu.Tick(SdlKeyboardSnapshot.Empty, SdlKeyboardSnapshot.Empty, false); // the blink only
            if (typing.Field.Edit(_shell.Application.TakeTypedText(), keys))
                typing.Changed();
            // Only Enter leaves a field (back would otherwise fire on the Backspace that edits it).
            if (keys.IsDown(SdlKeyboardKey.Enter))
            {
                endTyping();
                typing.Done?.Invoke();
            }
            return true;
        }
        if (_paired)
        {
            // A new pair: this device serves from now on; back to the list, which shows it.
            _paired = false;
            _server ??= SyncServer.StartIfPaired(_libraries, Boot.DeviceName);
            _menu.Pop();
        }
        if (_pairing?.PairedWith is not null)
            closePairHere();
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
        _menu.Tick(keys, keys, escape);
        if (!_menu.IsOpen)
            stop();
        return true;
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

    public IEnumerable<RenderQuad> Quads() => IsOpen ? _shell.Unsqueezed(_menu.Quads(), centred: true) : [];
}
