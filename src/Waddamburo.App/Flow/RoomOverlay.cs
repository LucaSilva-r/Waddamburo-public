using System.Globalization;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Flow;

/// <summary>
/// The Online folder: what its spines do, the room the player is in, a text box for a room's name, password or code (like
/// song search's), and a banner saying how the link to the other player is doing. A spine ends the movie's selection, so
/// each ends in Song Select reloading on what's next. For testing, WADDAMBURO_ONLINE_SERVER and WADDAMBURO_ONLINE_TOKEN play
/// online on another server or account, leaving the player's settings and logins alone, and WADDAMBURO_ONLINE_LAN=1 shares
/// LAN addresses with strangers, for two games on one machine.
/// </summary>
internal sealed class RoomOverlay(GameShell shell, OverlayPainter painter) : IInputOverlay, IDisposable
{
    private enum Prompt { None, RoomName, NewPassword, JoinPassword, Code }

    private const int Width = 640, PromptHeight = 150, BannerHeight = 56, CodeLength = 6, TextLength = 32;
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RoomTimeout = TimeSpan.FromSeconds(5);

    private Prompt _prompt;
    private string _typed = "";
    private OnlineSession? _session;
    private Task<bool>? _start;
    private bool _quickPlay;
    private string? _joining;
    private bool _joiningListed;
    private IReadOnlyList<ListedRoom> _rooms = [];
    private ScoreProfile? _shownPartner;

    public bool IsOpen => _prompt != Prompt.None;

    /// <summary>The player is typing: the keyboard's letters and Backspace aren't shortcuts now.</summary>
    public bool Typing => IsOpen;

    /// <summary>Whether this player can play online: alone at home, with an account, and a server to meet on.</summary>
    public bool Available => shell.Arcade.Home && server() is not null && shell.JoinedSides.Count == 1 && token() is not null;

    /// <summary>A spine of the Online folder (or the folder's own) was picked.</summary>
    public void Handle(SongBrowseControl control, string? room)
    {
        switch (control)
        {
            // In a room, its folder holds the room's settings; otherwise the rooms to join.
            case SongBrowseControl.Online:
                show(_session is null ? Folder.Rooms : Folder.Room, control);
                break;
            case SongBrowseControl.RefreshRooms:
                show(Folder.Rooms, control);
                break;
            case SongBrowseControl.LeaveOnline:
                show(Folder.Songs, SongBrowseControl.Online);
                break;
            case SongBrowseControl.QuickPlay:
                _quickPlay = true;
                begin(session => session.QuickPlayAsync());
                show(Folder.Songs, SongBrowseControl.Online);
                break;
            case SongBrowseControl.HostRoom:
                ask(Prompt.RoomName);
                break;
            case SongBrowseControl.JoinByCode:
                (_joining, _joiningListed) = (null, false);
                ask(Prompt.Code);
                break;
            case SongBrowseControl.JoinListed when room is not null:
                (_joining, _joiningListed) = (room, true);
                if (_rooms.FirstOrDefault(listed => listed.Code == room)?.HasPassword == true)
                    ask(Prompt.JoinPassword);
                else
                    join(null);
                break;
            case SongBrowseControl.RoomVisibility when _session is { Room: { } current } session:
                update(session.UpdateAsync(isPublic: !current.IsPublic), control);
                break;
            case SongBrowseControl.RoomPassword:
                ask(Prompt.NewPassword);
                break;
            case SongBrowseControl.LeaveRoom:
                Leave();
                show(Folder.Rooms, SongBrowseControl.Online);
                break;
            default:
                show(_session is null ? Folder.Rooms : Folder.Room, control);
                break;
        }
    }

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
        {
            watchRoom();
            showPartner();
            return false;
        }
        var maximum = _prompt == Prompt.Code ? CodeLength : TextLength;
        foreach (var letter in shell.Application.TakeTypedText())
            if (_typed.Length < maximum && (_prompt != Prompt.Code || char.IsAsciiLetterOrDigit(letter)) && !char.IsControl(letter))
                _typed += _prompt == Prompt.Code ? char.ToUpperInvariant(letter) : letter;
        if (keys.IsDown(SdlKeyboardKey.Backspace) && _typed.Length > 0)
            _typed = _typed[..StringInfo.ParseCombiningCharacters(_typed)[^1]];
        if (escape)
        {
            var prompt = _prompt;
            close();
            // Back where the prompt was asked from.
            show(prompt == Prompt.NewPassword ? Folder.Room : Folder.Rooms, SongBrowseControl.Online);
        }
        else if (keys.IsDown(SdlKeyboardKey.Enter) && (_prompt != Prompt.Code || _typed.Length == CodeLength))
            submit();
        return true;
    }

    /// <summary>Leaves any room, without waiting on the network.</summary>
    public void Leave()
    {
        close();
        if (_shownPartner is not null)
            showPartnerAs(null);
        if (_session is { } session)
            _ = leaveAsync(session, _start);
        _session = null;
        _start = null;
        _quickPlay = false;
    }

    public void Dispose()
    {
        close();
        // The room goes now rather than when it runs out; a dead line doesn't hold up closing.
        if (_session is { } session)
            leaveAsync(session, _start).Wait(TimeSpan.FromSeconds(1));
        _session = null;
    }

    public IEnumerable<RenderQuad> Quads()
    {
        float x = (OverlayPainter.StageWidth - Width) / 2;
        if (IsOpen)
        {
            var y = (OverlayPainter.StageHeight - PromptHeight) / 2f;
            var (line, hint) = promptText();
            return
            [
                .. painter.Panel(x, y, Width, PromptHeight, (254, 205, 1), (0, 0, 0), radius: 18, outlineWidth: 4),
                painter.Text(line, OverlayPainter.StageWidth / 2, y + 52, Width - 48, 44),
                painter.Text(hint, OverlayPainter.StageWidth / 2, y + 108, Width - 48, 28),
            ];
        }
        if (banner() is not { } text || shell.Active.Id != FlowScenes.SongSelect)
            return [];
        return
        [
            .. painter.Panel(x, 24, Width, BannerHeight, (254, 205, 1), (0, 0, 0), radius: 14, outlineWidth: 3),
            painter.Text(text, OverlayPainter.StageWidth / 2, 24 + BannerHeight / 2, Width - 40, 34),
        ];
    }

    // Someone joined the room, or left it: Song Select shows them, Don-chan and name board, on the other side.
    private void showPartner()
    {
        var partner = _session?.Partner;
        if (ReferenceEquals(partner, _shownPartner) || shell.Active.Id != FlowScenes.SongSelect || shell.Reload.Busy
            || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active)
            return;
        showPartnerAs(partner);
        // Wherever the player is, again with the partner on screen.
        var view = shell.Hosts.OnlineFolder;
        shell.Reload.Start(() => shell.Hosts.ShowOnlineFolder(view is null ? null : view.Room is null ? folder(Folder.Rooms) : folder(Folder.Room)));
    }

    private void showPartnerAs(ScoreProfile? partner)
    {
        _shownPartner = partner;
        var side = shell.JoinedSides.Count == 0 ? 1 : 1 - shell.JoinedSides.Min;
        TaikoGuest.Profiles[side] = partner;
        shell.Don?.SetLook(side, partner?.Look);
        if (shell.Don is { } don)
            don.MirrorPartner = partner is not null && side == 1;
        // The side the player didn't join on (a host on the right drum has their partner on the left). Set before the
        // reload: it lays Song Select out for these sides as it starts.
        shell.Hosts.PartnerSide = partner is null ? null : side;
        if (shell.Indicators is { } indicators)
            indicators.Partner = partner is not null;
    }

    private void ask(Prompt prompt)
    {
        _prompt = prompt;
        _typed = "";
        shell.Application.TakeTypedText();
        shell.Application.StartTextInput();
    }

    private void close()
    {
        if (IsOpen)
            shell.Application.StopTextInput();
        _prompt = Prompt.None;
    }

    private void submit()
    {
        var (prompt, typed) = (_prompt, _typed);
        close();
        switch (prompt)
        {
            case Prompt.RoomName:
                begin(session => session.HostAsync(typed.Trim().Length == 0 ? null : typed.Trim()));
                show(Folder.Songs, SongBrowseControl.Online);
                break;
            case Prompt.NewPassword when _session is { } session:
                update(session.UpdateAsync(password: typed), SongBrowseControl.RoomPassword);
                break;
            case Prompt.Code:
                _joining = typed;
                join(null);
                break;
            case Prompt.JoinPassword:
                join(typed);
                break;
            default:
                show(Folder.Rooms, SongBrowseControl.Online);
                break;
        }
    }

    private void join(string? password)
    {
        var (code, listed) = (_joining!, _joiningListed);
        begin(session => session.JoinAsync(code, password, listed));
        show(Folder.Songs, SongBrowseControl.Online);
    }

    private void begin(Func<OnlineSession, Task<bool>> start)
    {
        replaceSession();
        var insecure = insecureServer();
        var session = new OnlineSession(ScoreClient.CreateHttp(server()!, insecure, token()), insecure)
        {
            ShareLanWithStrangers = Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_LAN") == "1",
        };
        _session = session;
        _start = Task.Run(() => start(session));
    }

    private void replaceSession()
    {
        if (_session is { } previous)
            _ = leaveAsync(previous, _start);
        _session = null;
        _start = null;
    }

    private void update(Task change, SongBrowseControl cursor) => shell.Reload.Start(() =>
    {
        waitFor(change, RoomTimeout);
        if (change.IsFaulted)
            warn(T("online.no_server"));
        shell.Hosts.ShowOnlineFolder(folder(Folder.Room), cursor);
    });

    private enum Folder { Songs, Rooms, Room }

    // Song Select reloads on the songs (the Online spine opening the room), the rooms to join, or the room's settings.
    private void show(Folder target, SongBrowseControl? cursor = null) => shell.Reload.Start(() =>
    {
        // Under the reload's cover: a short wait here shows nothing, and the folder is right when it opens.
        if (_session is { } session)
            SpinWait.SpinUntil(() => session.Room is not null || _start?.IsCompleted != false, RoomTimeout);
        if (target == Folder.Rooms)
            fetchRooms();
        shell.Hosts.ShowOnlineFolder(target == Folder.Songs ? null : folder(target), cursor);
    });

    private void fetchRooms()
    {
        var list = new RoomClient(ScoreClient.CreateHttp(server()!, insecureServer(), token())).ListAsync();
        waitFor(list, ListTimeout);
        if (list.IsCompletedSuccessfully)
            _rooms = list.Result;
        else
            warn(T("online.no_server"));
    }

    private OnlineFolderView folder(Folder target) =>
        target == Folder.Room ? new(_rooms, _session?.Room, _session?.IsHost == true) : new(_rooms);

    // A room that couldn't be had, or is gone: back to the list, saying why.
    private void watchRoom()
    {
        if (_session is not { } session || _start is not { IsCompleted: true } start || shell.Active.Id != FlowScenes.SongSelect || shell.Reload.Busy)
            return;
        if (start.IsFaulted)
            Console.Error.WriteLine($"Warning ONLINE: {start.Exception?.GetBaseException().Message}");
        var problem = start.IsFaulted ? T("online.no_server")
            : !start.Result ? session.WrongPassword ? T("online.wrong_password") : T("online.no_room")
            : session.RoomGone ? T("online.room_gone")
            : null;
        if (problem is null)
            return;
        warn(problem);
        Leave();
        show(Folder.Rooms, SongBrowseControl.Online);
    }

    private (string Line, string Hint) promptText() => _prompt switch
    {
        Prompt.RoomName => (T("online.type_name", _typed + "_"), T("online.name_hint")),
        Prompt.NewPassword => (T("online.type_password", new string('*', _typed.Length) + "_"), T("online.password_hint")),
        Prompt.JoinPassword => (T("online.type_password", new string('*', _typed.Length) + "_"), T("online.type_hint")),
        _ => (T("online.type_code", _typed.PadRight(CodeLength, '_')), T("online.type_hint")),
    };

    private string? banner()
    {
        if (_session is not { Room: not null } session || _start is { IsCompleted: true, Result: false })
            return null;
        return session.Link.State switch
        {
            PeerLinkState.Idle => _quickPlay ? T("online.searching")
                : session.IsHost ? T("online.waiting_guest") : T("online.waiting_host"),
            PeerLinkState.Connecting => T("online.connecting"),
            PeerLinkState.Direct or PeerLinkState.Relayed => T("online.connected", session.Link.RoundTrip?.TotalMilliseconds ?? 0),
            PeerLinkState.Failed => T("online.failed"),
            _ => T("online.peer_left"),
        };
    }

    private void warn(string message) => shell.Sync.Notices.AddLocal(message, NoticeSeverity.Warning);

    private static string? serverOverride => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_SERVER") is { Length: > 0 } other ? other : null;

    private Uri? server() => serverOverride is { } other ? new Uri(other) : shell.Arcade.Server;

    private bool insecureServer() => serverOverride is null && shell.Arcade.ServerInsecure;

    private string? token() => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_TOKEN") is { Length: > 0 } other ? other
        : shell.JoinedSides.Count == 1 ? TaikoGuest.Profiles[shell.JoinedSides.Min]?.Token : null;

    private static void waitFor(Task task, TimeSpan timeout)
    {
        try
        {
            task.Wait(timeout);
        }
        catch (AggregateException)
        {
            // The caller reads the outcome off the task.
        }
    }

    private static async Task leaveAsync(OnlineSession session, Task<bool>? start)
    {
        if (start is not null)
        {
            try
            {
                await start.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // It never got as far as a room to leave, or the room is left below all the same.
            }
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }
}
