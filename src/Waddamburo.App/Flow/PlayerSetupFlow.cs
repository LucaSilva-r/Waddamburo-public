using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Game.Scores;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Flow;

internal enum SetupChoiceKind { Guest, Account, Friend, AddAccount }

/// <summary>One column's current choice (an account's avatar once it has downloaded).</summary>
internal sealed record SetupChoice(SetupChoiceKind Kind, string Label, long Baid = 0, RgbaTextSurface? Avatar = null,
    bool IsDefault = false)
{
    /// <summary>The Don-chan look on the stand (an account's or a visitor's; null: the default Don).</summary>
    public Waddamburo.Game.Don.DonLook? Look { get; init; }

    /// <summary>Whether a Don stands for this choice (the options show as text instead).</summary>
    public bool HasDon => Kind is SetupChoiceKind.Guest or SetupChoiceKind.Account
        || Kind == SetupChoiceKind.Friend && Baid != 0;
}

/// <summary>What a column shows: its choice, whether it is locked in, and a code or message while pairing.</summary>
internal sealed record SetupColumn(SetupChoice Choice, bool Ready, string? Code, int? Seconds, string? Message)
{
    /// <summary>Where the code is entered, with the code filled in, for the QR code on the stand.</summary>
    public string? QrUrl { get; init; }
}

/// <summary>
/// Home mode's "who's playing?" screen (like a console's player select), before the entry: each drum
/// picks, in its own column, Not playing / Guest / a stored account / Friend (a visitor pairs with the
/// 6-digit code and plays under a 12-hour token kept in memory) / Add account (an in-game login with a
/// code entered on the website). Rims choose, a centre hit locks the choice in (a rim hit unlocks it);
/// once every drum is locked in or not playing, the entry starts with those players already joined.
/// </summary>
internal sealed class PlayerSetupFlow : IDisposable
{
    private static readonly TimeSpan PairingPoll = TimeSpan.FromSeconds(2);
    private readonly AccountBook _book;
    private readonly Uri? _server;
    private readonly Func<string, ScoreClient?> _clientFor;
    private readonly string _avatarDirectory;
    private readonly ConcurrentQueue<Action> _onGameThread = new();
    private readonly Dictionary<string, RgbaTextSurface?> _avatars = [];
    private readonly HttpClient? _anonymous;
    private readonly Side[] _sides = [new(), new()];
    private CancellationTokenSource? _work;
    private int _workSide = -1;

    // Everyone playing is ready: the entry starts after a short grace in which another drum may still join.
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(1.5);
    private DateTime? _startAt;

    /// <summary>Whether the drums may pick yet (the shell enables it once the entry's intro has played).</summary>
    public bool InputEnabled { get; set; }

    private sealed class Side
    {
        // The chosen item by identity, not position: the list changes when the other drum takes an account.
        public SetupChoiceKind Kind;
        public long Baid;
        public bool Ready;
        public long? ReadyBaid; // the account locked in, which the other drum may not take
        public ScoreProfile? Visitor; // a friend's profile (Friend) once paired
        public string? Code;
        public string? QrUrl; // the page the code is entered on, code filled in (scanned from the stand)
        public DateTime CodeDeadline;
        public string? Message;
    }

    public PlayerSetupFlow(AccountBook book, Uri? server, bool insecure, Func<string, ScoreClient?> clientFor, string avatarDirectory)
    {
        _book = book;
        _server = server;
        _clientFor = clientFor;
        _avatarDirectory = avatarDirectory;
        _anonymous = server is null ? null : ScoreClient.CreateHttp(server, insecure);
    }

    public bool IsOpen { get; private set; }

    /// <summary>The players chosen (null: the setup is still open or was left).</summary>
    public event Action<ScoreProfile?[], bool[]>? Confirmed;

    /// <summary>Escape: back to the title.</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Opens with the drum that started on the default account (or Guest); the other drum waits on Join
    /// with code (Guest offline) and plays only if it locks something in before the start.
    /// </summary>
    public void Open(int starter)
    {
        IsOpen = true;
        InputEnabled = false;
        foreach (var side in _sides)
            reset(side, idleChoice);
        if (_book.Default is { } first)
            select(_sides[starter], SetupChoiceKind.Account, first.Baid);
        else
            select(_sides[starter], SetupChoiceKind.Guest);
        loadAvatars();
    }

    /// <summary>Reopens from the entry with its players preselected (a guest on Guest, a visitor on Friend).</summary>
    public void Reopen(IReadOnlyList<ScoreProfile?> profiles, IReadOnlyCollection<int> joined)
    {
        IsOpen = true;
        InputEnabled = false;
        foreach (var side in _sides)
            reset(side, idleChoice);
        foreach (var index in joined)
        {
            var profile = profiles[index];
            if (profile is null)
                select(_sides[index], SetupChoiceKind.Guest);
            else if (_book.Accounts.All(account => account.Baid != profile.Baid))
            {
                _sides[index].Visitor = profile;
                select(_sides[index], SetupChoiceKind.Friend);
            }
            else
                select(_sides[index], SetupChoiceKind.Account, profile.Baid);
        }
        loadAvatars();
    }

    private void loadAvatars()
    {
        foreach (var account in _book.Accounts)
            if (account.Avatar is { } url && !_avatars.ContainsKey(url))
                loadAvatar(url);
    }

    public void Close()
    {
        cancelWork();
        IsOpen = false;
    }

    /// <summary>What each column shows now.</summary>
    public SetupColumn[] Columns() => [column(0), column(1)];

    /// <summary>Per tick while open, with that tick's key presses.</summary>
    public void Tick(SdlKeyboardSnapshot keys)
    {
        while (_onGameThread.TryDequeue(out var action))
            action();
        if (!IsOpen)
            return;
        if (keys.IsDown(SdlKeyboardKey.Escape))
        {
            Close();
            Cancelled?.Invoke();
            return;
        }
        // Nothing is picked before the screen has finished appearing (the entry's intro), so the hit that
        // opened it, or a few more, cannot lock a drum in or start.
        if (!InputEnabled)
            return;
        // Left drum: D/K rims, F/J centres; right drum: Z/V rims, X/C centres.
        input(0, keys.IsDown(SdlKeyboardKey.D), keys.IsDown(SdlKeyboardKey.K),
            keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J));
        input(1, keys.IsDown(SdlKeyboardKey.Z), keys.IsDown(SdlKeyboardKey.V),
            keys.IsDown(SdlKeyboardKey.X) || keys.IsDown(SdlKeyboardKey.C));
        // S: the left column's account joins by itself from now on (toggle); Delete forgets it.
        if (current(0) is { Kind: SetupChoiceKind.Account } highlighted && !_sides[0].Ready)
        {
            if (keys.IsDown(SdlKeyboardKey.S))
                _book.SetDefault(_book.DefaultBaid == highlighted.Baid ? null : highlighted.Baid);
            else if (keys.IsDown(SdlKeyboardKey.Delete))
                _book.Remove(highlighted.Baid);
        }
        tryStart();
    }

    public void Dispose()
    {
        cancelWork();
        _anonymous?.Dispose();
    }

    private void input(int index, bool left, bool right, bool centre)
    {
        var side = _sides[index];
        if (left || right || centre)
            _startAt = null; // any hit holds the start
        if (left || right)
        {
            if (side.Ready)
            {
                side.Ready = false; // a rim hit unlocks
                side.ReadyBaid = null;
            }
            else
            {
                if (_workSide == index)
                    cancelWork();
                var choices = choicesFor(index);
                var next = (position(index, choices) + (right ? 1 : choices.Count - 1)) % choices.Count;
                select(side, choices[next].Kind, choices[next].Baid);
                side.Message = null;
                side.Visitor = null;
            }
            return;
        }
        if (!centre || side.Ready || _workSide == index)
            return;
        switch (current(index).Kind)
        {
            case SetupChoiceKind.Guest:
                side.Ready = true;
                break;
            case SetupChoiceKind.Account:
                side.Ready = true;
                side.ReadyBaid = current(index).Baid;
                break;
            case SetupChoiceKind.Friend when side.Visitor is not null:
                side.Ready = true;
                break;
            case SetupChoiceKind.Friend:
                startFriend(index);
                break;
            case SetupChoiceKind.AddAccount:
                startDeviceLogin(index);
                break;
        }
    }

    private void tryStart()
    {
        // Whoever locked in plays; a drum that did not simply stays out, unless it is mid-code (a friend
        // pairing or a sign-in), which holds the start.
        var playing = _sides.Select(static side => side.Ready).ToArray();
        if (!playing.Any(static value => value) || _sides.Any(static side => !side.Ready && side.Code is not null))
        {
            _startAt = null;
            return;
        }
        _startAt ??= DateTime.UtcNow + StartGrace;
        if (DateTime.UtcNow < _startAt)
            return;
        _startAt = null;
        var profiles = new ScoreProfile?[2];
        for (var index = 0; index < 2; index++)
            profiles[index] = !playing[index] ? null : current(index) switch
            {
                { Kind: SetupChoiceKind.Account } choice => _book.Accounts.FirstOrDefault(account => account.Baid == choice.Baid)?.Profile,
                { Kind: SetupChoiceKind.Friend } => _sides[index].Visitor,
                _ => null,
            };
        Close();
        Confirmed?.Invoke(profiles, playing);
    }

    // Guest, the accounts the other drum has not taken, Join with code (a friend), Sign in (add an account).
    private List<SetupChoice> choicesFor(int index)
    {
        var taken = _sides[1 - index].ReadyBaid ?? -1;
        var online = _server is not null;
        return
        [
            new(SetupChoiceKind.Guest, "Guest"),
            .. _book.Accounts.Where(account => account.Baid != taken).Select(account => new SetupChoice(SetupChoiceKind.Account,
                account.Profile.DisplayName, account.Baid, account.Avatar is { } url ? _avatars.GetValueOrDefault(url) : null,
                account.Baid == _book.DefaultBaid) { Look = account.Look }),
            .. online ? [new SetupChoice(SetupChoiceKind.Friend, "Join with code"), new(SetupChoiceKind.AddAccount, "Sign in")]
                : Array.Empty<SetupChoice>(),
        ];
    }

    private static void select(Side side, SetupChoiceKind kind, long baid = 0)
    {
        side.Kind = kind;
        side.Baid = kind == SetupChoiceKind.Account ? baid : 0;
    }

    // Where the side's choice is in its list; an account the other drum took falls back to Guest.
    private int position(int index, List<SetupChoice> choices)
    {
        var side = _sides[index];
        var found = choices.FindIndex(choice => choice.Kind == side.Kind && choice.Baid == side.Baid);
        if (found >= 0)
            return found;
        select(side, SetupChoiceKind.Guest);
        return choices.FindIndex(static choice => choice.Kind == SetupChoiceKind.Guest);
    }

    private SetupChoice current(int index)
    {
        var choices = choicesFor(index);
        var side = _sides[index];
        var choice = choices[position(index, choices)];
        return choice.Kind == SetupChoiceKind.Friend && side.Visitor is { } visitor
            ? choice with { Label = visitor.DisplayName, Baid = visitor.Baid, Look = visitor.Look,
                Avatar = visitor.Avatar is { } url ? _avatars.GetValueOrDefault(url) : null }
            : choice;
    }

    private SetupColumn column(int index)
    {
        var side = _sides[index];
        int? seconds = side.Code is null ? null
            : Math.Max(0, (int)Math.Ceiling((side.CodeDeadline - DateTime.UtcNow).TotalSeconds));
        var starting = _startAt is not null;
        var message = starting ? side.Ready ? "Starting..." : null : side.Message;
        return new SetupColumn(current(index), side.Ready, side.Code, seconds, message) { QrUrl = side.Code is null ? null : side.QrUrl };
    }

    // A drum nobody picked for yet: Join with code (Guest when there is no server).
    private SetupChoiceKind idleChoice => _server is null ? SetupChoiceKind.Guest : SetupChoiceKind.Friend;

    private static void reset(Side side, SetupChoiceKind choice)
    {
        select(side, choice);
        side.Ready = false;
        side.ReadyBaid = null;
        side.Visitor = null;
        side.Code = null;
        side.Message = null;
    }

    private void cancelWork()
    {
        _work?.Cancel();
        _work = null;
        if (_workSide >= 0)
            _sides[_workSide].Code = null;
        _workSide = -1;
    }

    private CancellationTokenSource? beginWork(int index)
    {
        if (_workSide >= 0 && _workSide != index)
        {
            _sides[index].Message = "Wait for the other player's code first.";
            return null;
        }
        cancelWork();
        _workSide = index;
        _sides[index].Message = "Connecting...";
        return _work = new CancellationTokenSource();
    }

    private void finishWork(CancellationTokenSource work, int index, string? message)
    {
        if (work.IsCancellationRequested)
            return;
        _work = null;
        _workSide = -1;
        _sides[index].Code = null;
        _sides[index].Message = message;
    }

    // Add account: a code for the website; the approved account is stored and locks this column in.
    private void startDeviceLogin(int index)
    {
        if (_anonymous is null || beginWork(index) is not { } work)
            return;
        var client = new ScoreClient(_anonymous);
        _ = Task.Run(async () =>
        {
            try
            {
                var login = await client.StartDeviceLoginAsync($"Waddamburo on {Environment.MachineName}", work.Token);
                post(() =>
                {
                    if (work.IsCancellationRequested)
                        return;
                    _sides[index].Code = login.UserCode;
                    _sides[index].CodeDeadline = DateTime.UtcNow.AddSeconds(login.ExpiresIn);
                    _sides[index].Message = null;
                    // Scanned from the stand, the approval page opens on the phone of whoever signs in,
                    // already logged in to the account they want.
                    _sides[index].QrUrl = $"{login.VerificationUrl}?code={login.UserCode}";
                });
                while (!work.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, login.Interval)), work.Token);
                    if (await client.PollDeviceLoginAsync(login.DeviceCode, work.Token) is { } account)
                    {
                        post(() =>
                        {
                            if (work.IsCancellationRequested)
                                return;
                            finishWork(work, index, null);
                            _book.Add(account);
                            if (account.Avatar is { } url)
                                loadAvatar(url);
                            select(_sides[index], SetupChoiceKind.Account, account.Baid);
                            // Selected, not locked in: the player confirms with the drum like any choice.
                        });
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                post(() => finishWork(work, index, $"Login failed: {exception.Message}"));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    // Friend: the visitor pairs with the 6-digit code; a stored account's token asks the server for it.
    private void startFriend(int index)
    {
        var owner = _book.Default ?? (_book.Accounts is [var first, ..] ? first : null);
        if (owner is null || _clientFor(owner.Token) is not { } ownerClient)
        {
            _sides[index].Message = "Friends can join once this PC has an account (Sign in).";
            return;
        }
        if (beginWork(index) is not { } work)
            return;
        var pairing = new FriendPairingClient(ownerClient.Http);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!work.IsCancellationRequested)
                {
                    var state = await pairing.PollAsync(accepting: true, work.Token);
                    post(() =>
                    {
                        if (work.IsCancellationRequested)
                            return;
                        switch (state)
                        {
                            case PairingState.Active active:
                                _sides[index].Code = active.Code;
                                // The friend scans it and confirms on their own account's Play page.
                                _sides[index].QrUrl = $"{_server}waddamburo/play?code={active.Code}"; // ponytail: the site's default scope
                                _sides[index].CodeDeadline = DateTime.UtcNow + active.ExpiresIn;
                                _sides[index].Message = null;
                                break;
                            case PairingState.Visitor visitor:
                                finishWork(work, index, null);
                                _sides[index].Visitor = visitor.Profile; // selected; the drum confirms it
                                if (visitor.Profile.Avatar is { } url)
                                    loadAvatar(url);
                                break;
                            case PairingState.Rejected:
                                finishWork(work, index, "That card has no TaikOnline account.");
                                break;
                        }
                    });
                    if (state is PairingState.Visitor or PairingState.Rejected)
                    {
                        await pairing.PollAsync(accepting: false, CancellationToken.None); // acknowledge
                        return;
                    }
                    await Task.Delay(PairingPoll, work.Token);
                }
            }
            catch (HttpRequestException exception)
            {
                post(() => finishWork(work, index, $"Pairing failed: {exception.Message}"));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    // Avatars (the website's custom Don-chan PNGs), cached on disk by URL.
    private void loadAvatar(string url)
    {
        if (_anonymous is null || _avatars.ContainsKey(url))
            return;
        _avatars[url] = null;
        var file = Path.Combine(_avatarDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".png");
        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(file))
                {
                    var bytes = await _anonymous.GetByteArrayAsync(url);
                    Directory.CreateDirectory(_avatarDirectory);
                    await File.WriteAllBytesAsync(file, bytes);
                }
                var image = SdlPng.Decode(await File.ReadAllBytesAsync(file));
                post(() => _avatars[url] = image);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Warning ACCOUNT: avatar not loaded ({exception.Message}).");
            }
        });
    }

    private void post(Action action) => _onGameThread.Enqueue(action);
}
