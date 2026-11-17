using System.Net;
using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.App.Sync;
using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Settings › Send to device and Receive from device (see song-sync-plan.md), pages of the overlay menu. Sending: the
/// devices found on the network, the code the chosen one shows, what to send (a Song Select search; Green's game data
/// when the device lacks it), progress. Receiving: listens only while open, shows the code, asks before anything is
/// written. A field takes typing once Enter is pressed on it (a phone shows its keyboard); Enter or Escape ends it.
/// </summary>
internal sealed class DeviceTransfer(GameShell shell, SongCatalogSnapshot catalog, string greenData) : IInputOverlay, IDisposable
{
    private readonly OverlayMenu _menu = new(shell.Application, shell.Options.ServiceFontPath ?? shell.Options.FontPath);
    private CancellationTokenSource? _work;

    // The field being typed in and what its changes do, null when none.
    private (OverlayTextField Field, Action Changed, Action? Done)? _typing;
    private readonly OverlayTextField _codeField = new(4, static letter => char.IsAsciiDigit(letter) ? letter : null);
    private readonly OverlayTextField _queryField = new();

    // Sending.
    private volatile List<Phone> _phones = [];
    private Phone? _phone;
    private string _code => _codeField.Text;
    private string _query => _queryField.Text;
    private volatile string? _codeProblem;
    private volatile bool _codeAccepted;
    private volatile int _matches = -1;
    private volatile List<PhoneFile>? _greenMissing; // null: not known yet or the device takes no Green
    private bool _sendGreen;
    private volatile string _status = "";
    private long _sent, _total;
    private volatile bool _finished;

    // Receiving.
    private PhoneReceiver? _receiver;
    private int _commitsSeen;
    private (int Songs, long Bytes, TaskCompletionSource<bool> Answer)? _ask;
    private bool _asking;

    public bool IsOpen => _menu.IsOpen;

    private static string T(string key, params object[] values) => values.Length == 0 ? Strings.T(key) : Strings.T(key, values);

    private static string gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / 1048576.0:F0} MB";

    // ---- Opening and closing

    public void OpenSend()
    {
        stop();
        _work = new CancellationTokenSource();
        (_phones, _phone, _codeProblem, _matches, _greenMissing, _finished) = ([], null, null, -1, null, false);
        _codeField.Set("");
        _queryField.Set("");
        _menu.Sound = sound;
        _menu.Open(new OverlayPage(() => T("transfer.send.title"), devicesRows, Centred: true,
            Body: () => [(T("transfer.send.how"), OverlayMenu.White)]), home: true);
        var token = _work.Token;
        // Found again every two seconds while open: a device that opens Receive later shows up.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var found = await PhoneSync.DiscoverAsync(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
                    _phones = [.. _phones.Where(old => found.All(phone => phone.Name != old.Name)), .. found];
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

    public void OpenReceive()
    {
        stop();
        _work = new CancellationTokenSource();
        _menu.Sound = sound;
        try
        {
            var targets = Boot.ReceiveTargets ?? new Dictionary<string, string> { ["store"] = Boot.ReceivedSongsFolder! };
            Directory.CreateDirectory(targets["store"]);
            _receiver = new PhoneReceiver(targets["store"], targets, Boot.DeviceName) { Accept = accept };
        }
        catch (Exception exception) when (exception is HttpListenerException or System.Net.Sockets.SocketException or IOException)
        {
            _menu.Open(new OverlayPage(() => T("transfer.receive.title"), () => [new(T("service.ok"), Enter: Close)],
                Centred: true, Body: () => [(T("transfer.receive.busy", exception.Message), OverlayMenu.Red)]), home: true);
            return;
        }
        _commitsSeen = 0;
        _menu.Open(new OverlayPage(() => T("transfer.receive.title"), receiveRows, Centred: true, Body: receiveBody), home: true);
    }

    public void Close()
    {
        stop();
        _menu.Close();
    }

    private void stop()
    {
        _work?.Cancel();
        _work = null;
        _receiver?.Dispose();
        _receiver = null;
        _ask?.Answer.TrySetResult(false);
        (_ask, _asking) = (null, false);
        endTyping();
    }

    public void Dispose()
    {
        stop();
        _menu.Dispose();
    }

    // ---- Input

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
            return false;
        if (_typing is { } typing)
        {
            _menu.Tick(SdlKeyboardSnapshot.Empty, SdlKeyboardSnapshot.Empty, false); // the blink only
            if (typing.Field.Edit(shell.Application.TakeTypedText(), keys))
                typing.Changed();
            if (escape || keys.IsDown(SdlKeyboardKey.Enter))
            {
                endTyping();
                if (!escape)
                    typing.Done?.Invoke();
            }
            return true;
        }
        if (_codeAccepted)
        {
            _codeAccepted = false;
            openWhat();
        }
        // The receiver's question comes from its own thread: asked here, on the game's.
        if (_ask is { } ask && !_asking)
        {
            _asking = true;
            _menu.Push(new OverlayPage(() => T("transfer.receive.title"), () =>
                [new(T("menu.yes"), Enter: () => answer(true)), new(T("menu.no"), Enter: () => answer(false))],
                Centred: true, Modal: true, Back: () => answer(false),
                Body: () => [(T("transfer.receive.ask", _receiver?.Expected.From ?? "?", ask.Songs, gb(ask.Bytes)), OverlayMenu.Yellow)]));

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
        shell.Application.TakeTypedText();
        shell.Application.StartTextInput();
    }

    private void endTyping()
    {
        if (_typing is null)
            return;
        _typing = null;
        shell.Application.StopTextInput();
    }


    private void sound(bool don) => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false);

    // ---- Sending: devices, code, what, progress

    private OverlayRow[] devicesRows() =>
    [
        .. _phones.Count == 0
            ? [new OverlayRow(T("transfer.send.looking"), Cyan: true)]
            : _phones.Select(phone => new OverlayRow($"{phone.Name}  ({phone.Address})", Enter: () => pickDevice(phone))),
        new(T("transfer.back"), Gap: true, Enter: Close),
    ];

    private void pickDevice(Phone phone)
    {
        (_phone, _codeProblem) = (phone, null);
        _codeField.Set("");
        // The fourth digit checks it at once; Enter on a shorter one does nothing.
        void typeCode() => type(_codeField, () => { if (_code.Length == 4) { endTyping(); checkCode(); } }, checkCode);
        _menu.Push(new OverlayPage(() => T("transfer.code.title"), () =>
        [
            new(T("transfer.code.field"), 400, () => _codeField.Shown(_typing?.Field == _codeField), 560, Enter: typeCode),
            new(T("transfer.code.next"), 400, Gap: true, Enter: checkCode),
        ], Footer: () =>
        [
            (T("transfer.code.how", phone.Name), OverlayMenu.White),
            .. _codeProblem is { } problem ? [(problem, OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
        ]));
        typeCode();
    }

    // The code is right when the device answers with it; then what it lacks of Green is asked.
    private void checkCode()
    {
        if (_phone is not { } phone || _code.Length != 4)
            return;
        endTyping();
        _codeProblem = T("transfer.code.checking");
        var device = PhoneSync.Address(phone.Address, _code);
        var token = _work!.Token;
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                await PhoneSync.FreeSpaceAsync(http, device, token).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                _codeProblem = exception.StatusCode == HttpStatusCode.NotFound ? T("transfer.code.wrong") : T("transfer.failed", exception.Message);
                return;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                _codeProblem = T("transfer.failed", "no answer");
                return;
            }
            _codeProblem = null;
            _codeAccepted = true; // the next tick opens the next page (pages change on the game's thread)
            try
            {
                // A device that takes Green (a phone) lacking files of it: offered, ticked.
                var missing = await PhoneSync.MissingAsync(http, device, PhoneSync.GreenPlan(greenData), token).ConfigureAwait(false);
                if (missing.Count > 0)
                    (_greenMissing, _sendGreen) = (missing, true);
            }
            catch (HttpRequestException) { } // 400: it takes no Green
        }, token);
    }

    private void openWhat()
    {
        if (_work is null)
            return;
        _menu.Push(new OverlayPage(() => T("transfer.what.title"), () =>
        [
            new(T("transfer.what.songs"), 400, () => _query.Length == 0 && _typing is null ? T("transfer.what.songs_hint")
                : _queryField.Shown(_typing?.Field == _queryField), 560, Enter: () => type(_queryField, count)),
            new(T("transfer.what.count"), 400, () => _query.Trim().Length == 0 ? T("transfer.what.none")
                : _matches < 0 ? T("search.searching") : T("search.count", _matches), 560, Cyan: true),
            .. _greenMissing is { } green
                ? [new OverlayRow(T("transfer.what.green", gb(green.Sum(static file => file.Size))), 400,
                    () => _sendGreen ? T("transfer.on") : T("transfer.off"), 560, Step: _ => _sendGreen = !_sendGreen)]
                : Array.Empty<OverlayRow>(),
            new(T("transfer.what.send"), 400, Gap: true, Enter: send),
        ], Footer: () =>
        [
            (T("transfer.what.legend"), OverlayMenu.White),
            (T("search.legend.filters"), OverlayMenu.Cyan),
        ]));
    }

    // Songs the search matches, counted off the game thread.
    private void count()
    {
        var query = _query;
        _matches = -1;
        var categories = new Dictionary<SongKey, string>();
        foreach (var category in catalog.Categories)
            foreach (var key in category.Songs)
                categories.TryAdd(key, category.Name);
        _ = Task.Run(() =>
        {
            var matched = query.Trim() == "*" ? catalog.Songs.Count
                : catalog.Songs.Values.Count(song => categories.ContainsKey(song.Key)
                    && SongBrowse.Matches(song, query, categoriesOf: song => categories.TryGetValue(song.Key, out var name) ? [name] : []));
            if (_query == query)
                _matches = matched;
        });
    }

    private void send()
    {
        if (_phone is not { } phone || _work is null || (_query.Trim().Length == 0 && !(_sendGreen && _greenMissing is not null)))
            return;
        endTyping();
        (_status, _sent, _total, _finished) = (T("transfer.progress.starting"), 0, 0, false);
        var (device, query, green, token) = (PhoneSync.Address(phone.Address, _code), _query.Trim(),
            _sendGreen ? _greenMissing : null, _work.Token);
        _menu.Push(new OverlayPage(() => T("transfer.send.title"), () =>
            [new(_finished ? T("service.ok") : T("transfer.cancel"), Enter: Close)], Centred: true, Back: Close, Body: () =>
            [
                (_status, _finished ? OverlayMenu.Green : OverlayMenu.White),
                .. Interlocked.Read(ref _total) > 0 && !_finished
                    ? [($"{gb(Interlocked.Read(ref _sent))} / {gb(Interlocked.Read(ref _total))}", OverlayMenu.Cyan)]
                    : Array.Empty<(string, RenderColor)>(),
            ]));
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            void sent(long bytes) => Interlocked.Add(ref _sent, bytes);
            try
            {
                if (green is not null)
                {
                    _status = T("transfer.progress.green");
                    (_sent, _total) = (0, green.Sum(static file => file.Size));
                    await PhoneSync.SendAsync(http, device, green, sent, token).ConfigureAwait(false);
                }
                var taken = 0;
                if (query.Length > 0)
                {
                    (_sent, _total) = (0, 0);
                    var (rows, blobs) = await SongExport.BuildAsync(catalog, shell.Assets, query,
                        static warning => Console.Error.WriteLine($"Send: {warning}"),
                        (done, count) => _status = T("transfer.progress.reading", done, count), token).ConfigureAwait(false);
                    _status = T("transfer.progress.waiting", phone.Name);
                    taken = await PhoneSync.SendSongsAsync(http, device, rows, blobs, sent, bytes =>
                    {
                        (_sent, _total) = (0, bytes);
                        _status = T("transfer.progress.sending", rows.Count);
                    }, token).ConfigureAwait(false);
                }
                _status = T("transfer.progress.done", taken, phone.Name);
            }
            catch (OperationCanceledException)
            {
                _status = T("transfer.progress.cancelled");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                _status = T("transfer.failed", exception.Message);
            }
            _finished = true;
        }, token);
    }

    // ---- Receiving

    // On the receiver's thread: the player is asked on the game's (Tick), this waits for the answer (two minutes).
    private bool accept(int songs, long bytes)
    {
        var answer = new TaskCompletionSource<bool>();
        _ask = (songs, bytes, answer);
        return answer.Task.Wait(TimeSpan.FromMinutes(2)) && answer.Task.Result;
    }

    private (string Text, RenderColor Colour)[] receiveBody()
    {
        if (_receiver is not { } receiver)
            return [];
        if (receiver.LockedOut)
            return [(T("transfer.receive.locked"), OverlayMenu.Red)];
        var (_, bytes) = receiver.Received;
        var addresses = receiver.Addresses();
        return
        [
            (T("transfer.receive.how", receiver.Name), OverlayMenu.White),
            (T("transfer.receive.code", receiver.Code), OverlayMenu.Yellow),
            .. addresses.Count == 0 ? [(T("transfer.receive.offline"), OverlayMenu.Red)] : Array.Empty<(string, RenderColor)>(),
            receiver.Commits > _commitsSeen
                ? (T("transfer.receive.done", receiver.Committed, receiver.Expected.From), OverlayMenu.Green)
                : receiver.Expected.Bytes > 0
                    ? ($"{T("transfer.receive.receiving", receiver.Expected.From)} {gb(bytes)} / {gb(receiver.Expected.Bytes)}", OverlayMenu.Cyan)
                    : (T("transfer.receive.waiting"), OverlayMenu.Cyan),
        ];
    }

    private OverlayRow[] receiveRows() =>
    [
        .. _receiver is { Commits: > 0 } ? [new OverlayRow(T("service.import.restart"), Enter: shell.RequestRestart)] : Array.Empty<OverlayRow>(),
        new(T("transfer.receive.stop"), Enter: Close),
    ];

    public IEnumerable<RenderQuad> Quads() => IsOpen ? shell.Unsqueezed(_menu.Quads(), centred: true) : [];
}
