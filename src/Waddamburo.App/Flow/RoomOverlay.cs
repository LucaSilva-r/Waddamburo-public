using System.Diagnostics;
using System.Globalization;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scores;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Flow;

/// <summary>
/// The online lobby, the home menu's buttons over Song Select: Quick Play, hosting, joining by code and the public rooms
/// (refreshing by themselves), then the room's own page. Song Select's own Don-chans and name boards stay in view at the
/// sides, both players'. A hit on the Online spine opens it before the movie decides that spine. A text box takes a room's name, password or code (like song search's). Closed, a banner says how the room
/// is doing. Rims or Up/Down move, a centre hit or Enter picks, Escape closes.
/// For testing, WADDAMBURO_ONLINE_SERVER and WADDAMBURO_ONLINE_TOKEN play online on another server or account, leaving the
/// player's settings and logins alone, and WADDAMBURO_ONLINE_LAN=1 shares LAN addresses with strangers, for two games on
/// one machine.
/// </summary>
internal sealed class RoomOverlay(GameShell shell, OverlayPainter painter) : IInputOverlay, IDisposable
{
    private enum Prompt { None, RoomName, NewPassword, JoinPassword, Code }

    private enum Action { QuickPlay, Host, Code, Join, Back, ChooseSong, Visibility, Password, Leave }

    private sealed record Row(string Text, Action Action, ListedRoom? Room = null);

    private const int PromptWidth = 640, PromptHeight = 150, BannerWidth = 520, BannerHeight = 50, BannerTop = 658, CodeLength = 6, TextLength = 32;
    private const float Centre = 640;
    private const float RowX = Centre - 280, RowWidth = 560, RowHeight = 54, RowSpacing = 64, RowsTop = 230;
    private const int VisibleRows = 5;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private Prompt _prompt;
    private string _typed = "";
    private bool _panel;
    private int _selection;
    private OnlineSession? _session;
    private Task<bool>? _start;
    private bool _quickPlay;
    private string? _joining;
    private bool _joiningListed;
    private IReadOnlyList<ListedRoom> _rooms = [];
    private Task<IReadOnlyList<ListedRoom>>? _listing;
    private long _listedAt;
    private ScoreProfile? _shownPartner;

    public bool IsOpen => _panel || _prompt != Prompt.None;

    /// <summary>The lobby shades Song Select (the shell draws its players again over <see cref="Shade"/>).</summary>
    public bool ShadesScene => _panel;

    public IEnumerable<RenderQuad> Shade() => [painter.Rect(0, 0, 1280, 720, new RenderColor(0, 0, 0, 0.6f))];

    /// <summary>The room the player is in, once they're in it.</summary>
    public OnlineSession? Session => _start is { IsCompletedSuccessfully: true, Result: true } ? _session : null;

    /// <summary>The player is typing: the keyboard's letters and Backspace aren't shortcuts now.</summary>
    public bool Typing => _prompt != Prompt.None;

    /// <summary>Whether this player can play online: alone at home, with an account, and a server to meet on.</summary>
    public bool Available => shell.Arcade.Home && server() is not null && shell.JoinedSides.Count == 1 && token() is not null;

    /// <summary>
    /// The Online spine was decided after all (the movie took the hit): the lobby opens, and Song Select reloads under it,
    /// ready on that spine for when it closes.
    /// </summary>
    public void OpenLobby()
    {
        open();
        shell.Reload.Start(static () => { });
        shell.Hosts.ReturnToOnline();
    }

    private void open()
    {
        _panel = true;
        _selection = 0;
        _listedAt = 0;
        decide();
    }

    private void decide() => shell.Sounds?.Bank.Play("SE_COM", 0, Waddamburo.Platform.Sdl.Media.AudioBus.DrumHit, trace: false);

    private void step() => shell.Sounds?.Bank.Play("SE_COM", 3, Waddamburo.Platform.Sdl.Media.AudioBus.DrumHit, trace: false);

    private static bool decidePressed(SdlKeyboardSnapshot keys)
    {
        var pads = SdlInputBindings.Pads;
        return new[] { SdlKeyboardKey.Enter, pads[1], pads[2], pads[5], pads[6] }.Any(keys.IsDown);
    }

    /// <summary>The panel goes (the room stays): the host's song is coming, or the player goes to choose one.</summary>
    public void ClosePanel()
    {
        closePrompt();
        _panel = false;
    }

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        watchRoom();
        showPartner();
        if (!IsOpen)
        {
            // The host under the rainbow, the guest not picking: Escape stops waiting.
            if (escape && shell.Match.WaitingForPartner)
            {
                shell.Match.Cancel();
                return true;
            }
            // A hit on the Online spine: the lobby, without the movie ever deciding the spine.
            if (shell.Active.Id != FlowScenes.SongSelect || shell.Reload.Busy || shell.Hosts.SongSelect?.OnlineSpineUnderCursor != true
                || !decidePressed(keys))
                return false;
            open();
            return true;
        }
        if (_prompt != Prompt.None)
        {
            typeInto(keys, escape);
            return true;
        }
        refreshRooms();
        var rows = this.rows();
        _selection = Math.Clamp(_selection, 0, rows.Count - 1);
        bool pressed(params SdlKeyboardKey[] candidates) => candidates.Any(keys.IsDown);
        var pads = SdlInputBindings.Pads;
        if (escape)
            ClosePanel();
        else if (pressed(SdlKeyboardKey.Up, SdlKeyboardKey.Left, pads[0], pads[4]))
            move(-1, rows.Count);
        else if (pressed(SdlKeyboardKey.Down, SdlKeyboardKey.Right, pads[3], pads[7]))
            move(1, rows.Count);
        else if (decidePressed(keys))
        {
            decide();
            pick(rows[_selection]);
        }
        return true;
    }

    /// <summary>Leaves any room, without waiting on the network.</summary>
    public void Leave()
    {
        closePrompt();
        if (_shownPartner is not null)
            showPartnerAs(null);
        if (_session is { } session)
            _ = leaveAsync(session, _start);
        _session = null;
        _start = null;
        _quickPlay = false;
        _selection = 0;
    }

    public void Dispose()
    {
        closePrompt();
        // The room goes now rather than when it runs out; a dead line doesn't hold up closing.
        if (_session is { } session)
            leaveAsync(session, _start).Wait(TimeSpan.FromSeconds(1));
        _session = null;
    }

    public IEnumerable<RenderQuad> Quads()
    {
        var quads = new List<RenderQuad>();
        if (_panel)
            lobby(quads);
        else if (banner() is { } text && (shell.Active.Id == FlowScenes.SongSelect || shell.Match.HoldsReveal && shell.Active.Id == FlowScenes.Gameplay))
        {
            // Over the free play line, between the name boards.
            float x = (OverlayPainter.StageWidth - BannerWidth) / 2;
            quads.AddRange(painter.Panel(x, BannerTop, BannerWidth, BannerHeight, (254, 205, 1), (0, 0, 0), radius: 14, outlineWidth: 3));
            quads.Add(painter.Text(text, OverlayPainter.StageWidth / 2, BannerTop + BannerHeight / 2, BannerWidth - 36, 30));
        }
        if (_prompt != Prompt.None)
        {
            float x = (OverlayPainter.StageWidth - PromptWidth) / 2, y = (OverlayPainter.StageHeight - PromptHeight) / 2f;
            var (line, hint) = promptText();
            quads.AddRange(painter.Panel(x, y, PromptWidth, PromptHeight, (254, 205, 1), (0, 0, 0), radius: 18, outlineWidth: 4));
            quads.Add(painter.Text(line, OverlayPainter.StageWidth / 2, y + 52, PromptWidth - 48, 44));
            quads.Add(painter.Text(hint, OverlayPainter.StageWidth / 2, y + 108, PromptWidth - 48, 28));
        }
        return quads;
    }

    // What's going on and the choices (a window of them, around the selection), over the shade.
    private void lobby(List<RenderQuad> quads)
    {
        var art = shell.HomeArt;
        var title = _session?.Room?.Name ?? T("online.lobby");
        quads.Add(painter.Text(title, Centre, 92, 700, 64));
        quads.Add(painter.Text(status(), Centre, 160, 900, 34));
        var rows = this.rows();
        var first = Math.Clamp(_selection - VisibleRows / 2, 0, Math.Max(0, rows.Count - VisibleRows));
        for (var index = first; index < Math.Min(rows.Count, first + VisibleRows); index++)
        {
            var y = RowsTop + (index - first) * RowSpacing;
            if (art is not null)
                art.Choice(quads, RowX, y, RowWidth, RowHeight, index == _selection, rows[index].Text);
            else
            {
                quads.Add(painter.Rect(RowX, y, RowWidth, RowHeight, index == _selection
                    ? new RenderColor(0.19f, 0.88f, 1, 1) : new RenderColor(0.16f, 0.45f, 0.65f, 1)));
                quads.Add(painter.Text(rows[index].Text, RowX + RowWidth / 2, y + RowHeight / 2, (int)RowWidth - 24, 36));
            }
        }
        if (first > 0)
            quads.Add(painter.Text("▲", Centre, RowsTop - 22, 40, 24));
        if (first + VisibleRows < rows.Count)
            quads.Add(painter.Text("▼", Centre, RowsTop + VisibleRows * RowSpacing - 4, 40, 24));
    }

    private List<Row> rows()
    {
        if (_session is not { } session)
        {
            List<Row> lobby = [new(T("online.quick_play"), Action.QuickPlay), new(T("online.host"), Action.Host), new(T("online.join_code"), Action.Code)];
            lobby.AddRange(_rooms.Select(room => new Row(room.HasPassword ? $"{room.Name} ({T("online.locked")})" : room.Name, Action.Join, room)));
            lobby.Add(new(T("menu.back"), Action.Back));
            return lobby;
        }
        if (!session.IsHost || session.Room is not { } current)
            return [new(T("online.leave"), Action.Leave), new(T("menu.back"), Action.Back)];
        return
        [
            new(T("online.choose_song"), Action.ChooseSong),
            new(current.IsPublic ? T("online.visibility_public") : T("online.visibility_private"), Action.Visibility),
            new(current.HasPassword ? T("online.password_on") : T("online.password_off"), Action.Password),
            new(T("online.leave"), Action.Leave),
        ];
    }

    private void pick(Row row)
    {
        switch (row.Action)
        {
            case Action.QuickPlay:
                _quickPlay = true;
                begin(session => session.QuickPlayAsync());
                break;
            case Action.Host:
                ask(Prompt.RoomName);
                break;
            case Action.Code:
                (_joining, _joiningListed) = (null, false);
                ask(Prompt.Code);
                break;
            case Action.Join when row.Room is { } room:
                (_joining, _joiningListed) = (room.Code, true);
                if (room.HasPassword)
                    ask(Prompt.JoinPassword);
                else
                    join(null);
                break;
            case Action.Visibility when _session is { Room: { } current } session:
                _ = change(session.UpdateAsync(isPublic: !current.IsPublic));
                break;
            case Action.Password:
                ask(Prompt.NewPassword);
                break;
            case Action.Leave:
                Leave();
                _listedAt = 0;
                break;
            case Action.Back or Action.ChooseSong:
                ClosePanel();
                break;
        }
    }

    private void move(int step, int count)
    {
        _selection = (_selection + step + count) % count;
        this.step();
    }

    // The public rooms, fetched again every few seconds while the lobby shows them; never waited on.
    private void refreshRooms()
    {
        if (_listing is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _rooms = done.Result;
            _listing = null;
        }
        if (_session is not null || _listing is not null || _listedAt != 0 && Stopwatch.GetElapsedTime(_listedAt) < RefreshEvery)
            return;
        _listedAt = Stopwatch.GetTimestamp();
        _listing = new RoomClient(ScoreClient.CreateHttp(server()!, insecureServer(), token())).ListAsync();
    }

    private string status()
    {
        if (_session is null)
            return T("online.lobby_hint");
        if (banner() is { } text)
            return text;
        return _session.Room is { IsPublic: false } room && _session.IsHost ? T("online.room_code", room.Code) : T("online.starting");
    }

    // Someone joined the room, or left it: Song Select shows them, Don-chan and name board, on the other side.
    private void showPartner()
    {
        var partner = _session?.Partner;
        if (ReferenceEquals(partner, _shownPartner) || shell.Active.Id != FlowScenes.SongSelect || shell.Reload.Busy
            || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active)
            return;
        showPartnerAs(partner);
        // Someone came: shown at once on the side waiting for them. Someone left: Song Select lays itself out again.
        if (partner is not null && shell.Hosts.SongSelect is { } songSelect)
            songSelect.ShowPartner(shell.Hosts.PartnerSide);
        else
            shell.Reload.Start(static () => { });
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

    private void typeInto(SdlKeyboardSnapshot keys, bool escape)
    {
        var maximum = _prompt == Prompt.Code ? CodeLength : TextLength;
        foreach (var letter in shell.Application.TakeTypedText())
            if (_typed.Length < maximum && (_prompt != Prompt.Code || char.IsAsciiLetterOrDigit(letter)) && !char.IsControl(letter))
                _typed += _prompt == Prompt.Code ? char.ToUpperInvariant(letter) : letter;
        if (keys.IsDown(SdlKeyboardKey.Backspace) && _typed.Length > 0)
            _typed = _typed[..StringInfo.ParseCombiningCharacters(_typed)[^1]];
        if (escape)
            closePrompt();
        else if (keys.IsDown(SdlKeyboardKey.Enter) && (_prompt != Prompt.Code || _typed.Length == CodeLength))
            submit();
    }

    private void ask(Prompt prompt)
    {
        _prompt = prompt;
        _typed = "";
        shell.Application.TakeTypedText();
        shell.Application.StartTextInput();
    }

    private void closePrompt()
    {
        if (_prompt != Prompt.None)
            shell.Application.StopTextInput();
        _prompt = Prompt.None;
    }

    private void submit()
    {
        var (prompt, typed) = (_prompt, _typed);
        closePrompt();
        switch (prompt)
        {
            case Prompt.RoomName:
                begin(session => session.HostAsync(typed.Trim().Length == 0 ? null : typed.Trim()));
                break;
            case Prompt.NewPassword when _session is { } session:
                _ = change(session.UpdateAsync(password: typed));
                break;
            case Prompt.Code:
                _joining = typed;
                join(null);
                break;
            case Prompt.JoinPassword:
                join(typed);
                break;
        }
        _selection = 0;
    }

    private void join(string? password)
    {
        var (code, listed) = (_joining!, _joiningListed);
        begin(session => session.JoinAsync(code, password, listed));
    }

    private void begin(Func<OnlineSession, Task<bool>> start)
    {
        if (_session is { } previous)
            _ = leaveAsync(previous, _start);
        var insecure = insecureServer();
        var session = new OnlineSession(ScoreClient.CreateHttp(server()!, insecure, token()), insecure)
        {
            ShareLanWithStrangers = Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_LAN") == "1",
        };
        _session = session;
        _start = Task.Run(() => start(session));
        _selection = 0;
    }

    // A room setting changed: the room's page shows it once the server agrees.
    private async Task change(Task update)
    {
        try
        {
            await update.ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"Warning ONLINE: {exception.Message}");
            warn(T("online.no_server"));
        }
    }

    // A room that couldn't be had, or is gone: back to the lobby, saying why.
    private void watchRoom()
    {
        if (_session is not { } session || _start is not { IsCompleted: true } start)
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
        _listedAt = 0;
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
        if (session.Link.State is PeerLinkState.Direct or PeerLinkState.Relayed && shell.Match.Status is { } status)
            return status;
        return session.Link.State switch
        {
            PeerLinkState.Idle => _quickPlay && !session.IsHost ? T("online.searching")
                : _quickPlay ? T("online.searching") : session.IsHost ? T("online.waiting_guest") : T("online.waiting_host"),
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
