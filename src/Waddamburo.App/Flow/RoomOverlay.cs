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
/// is doing. Rims, Up/Down or the mouse wheel (as in the Settings menu) move, a centre hit or Enter picks, Escape closes.
/// For testing, WADDAMBURO_ONLINE_SERVER and WADDAMBURO_ONLINE_TOKEN play online on another server or account, leaving the
/// player's settings and logins alone, WADDAMBURO_ONLINE_LAN=1 shares LAN addresses with strangers, for two games on
/// one machine, and WADDAMBURO_ONLINE_RELAY_ONLY=1 connects through the relay alone.
/// </summary>
internal sealed class RoomOverlay(GameShell shell, OverlayPainter painter) : IInputOverlay, IDisposable
{
    private enum Prompt { None, RoomName, NewPassword, JoinPassword, Code }

    private enum Action { QuickPlay, Host, Rooms, Code, Join, Back, ChooseSong, Choice, Visibility, Password, Leave, Accept, Refuse }

    private sealed record Row(string Text, Action Action, ListedRoom? Room = null);

    private const int BannerWidth = 520, BannerHeight = 50, BannerTop = 658, CodeLength = 6, TextLength = 32;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private Prompt _prompt;
    private string _typed = "";
    private bool _panel;
    // The lobby and its text prompts in the overlay look (Home/OverlayMenu): the lobby's page, a prompt's on top of it.
    private Home.OverlayMenu? _menu;
    private Home.OverlayPage? _lobbyPage, _promptPage;

    private Home.OverlayMenu menu()
    {
        _menu ??= new Home.OverlayMenu(shell.Application, shell.Options.ServiceFontPath ?? shell.Options.FontPath)
        {
            Sound = don => (don ? (System.Action)decide : step)(),
        };
        _lobbyPage ??= new(lobbyTitle, () => [.. rows().Select(row => new Home.OverlayRow(row.Text, Enter: () => pick(row)))],
            Centred: true, Message: lobbyMessage, Back: back, Legend: false);
        _promptPage ??= new(() => promptText().Line, () => [], Centred: true, Message: () => promptText().Hint, Legend: false);
        if (_panel && !_menu.IsOpen)
            _menu.Open(_lobbyPage, home: true);
        if (_prompt != Prompt.None && _menu.Page != _promptPage)
            _menu.Push(_promptPage);
        return _menu;
    }

    // The lobby's selection is its page's.
    private int _selection
    {
        get => menu().Selection;
        set => menu().Selection = value;
    }

    // Escape: out of the share question (refused), the public rooms, then the lobby (not while the other player picks).
    private void back()
    {
        if (_share is not null)
            answerShare(false);
        else if (_browsing && _session is null)
            browse(false);
        else if (!shell.Match.WaitingForTheirPick)
            ClosePanel();
    }

    private string lobbyTitle() => _warning ? _share is null ? T("online.warning_title") : T("online.share_title")
        : _session?.Room is { IsQuick: true } ? T("online.quick_play") : _session?.Room?.Name ?? T("online.lobby");

    // What's going on: the first time's warning, or the status and, in the host's own room, its code to give out.
    private string lobbyMessage() => _warning
        ? string.Join('\n', _share is null ? [T("online.warning_direct"), T("online.warning_ip"), T("online.warning_ok")]
            : (string[])[T("online.share_sent"), T("online.share_authorized"), T("online.share_memory")])
        : _session is { IsHost: true, Room: { IsQuick: false } room } ? $"{status()}\n{T("online.room_code", room.Code)}" : status();
    private OnlineSession? _session;
    private Task<bool>? _start;
    private bool _quickPlay;
    private string? _joining;
    private bool _joiningListed;
    private IReadOnlyList<ListedRoom> _rooms = [];
    private Task<IReadOnlyList<ListedRoom>>? _listing;
    private long _listedAt;
    private ScoreProfile? _shownPartner;
    private bool _browsing; // the public rooms' page
    private bool _warning; // the first time: playing online shows the player's IP address, accepted or not
    private Action<bool>? _share; // the first time a song of the player's is sent: whether they may share it
    private readonly object _bannerSlot = new(), _statusSlot = new();

    public bool IsOpen => _panel || _prompt != Prompt.None;

    /// <summary>The lobby shades Song Select (the shell draws its players again over <see cref="Shade"/>).</summary>
    public bool ShadesScene => _panel;

    public IEnumerable<RenderQuad> Shade() => shell.Application.BlursBackdrop ? []
        : [painter.Rect(0, 0, 1280, 720, new RenderColor(0, 0, 0, 0.8f))];

    /// <summary>The room the player is in, once they're in it.</summary>
    public OnlineSession? Session => _start is { IsCompletedSuccessfully: true, Result: true } ? _session : null;

    /// <summary>The player is typing: the keyboard's letters and Backspace aren't shortcuts now.</summary>
    public bool Typing => _prompt != Prompt.None;

    /// <summary>Whether this player can play online: alone at home, with an account, and a server to meet on.</summary>
    public bool Available => shell.Arcade.Home && server() is not null && shell.Players.JoinedSides.Count == 1 && token() is not null;

    /// <summary>
    /// The Online spine was decided after all (the movie took the hit): the lobby opens, and Song Select reloads on that
    /// spine once it goes (see listDanDojo; not under it, where the rainbow would cover the lobby).
    /// </summary>
    public void OpenLobby()
    {
        open();
        _relayout = true;
        shell.Hosts.ReturnToOnline();
    }

    private void open()
    {
        _panel = true;
        _warning = !shell.Arcade.OnlineWarningAccepted;
        _browsing = false;
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
        _menu?.Close();
    }

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        watchRoom();
        showPartner();
        listDanDojo();
        // The other player is choosing: the lobby stays over Song Select until their song comes.
        if (!IsOpen && shell.Match.WaitingForTheirPick && shell.Active.Id == FlowScenes.SongSelect && !shell.Reload.Busy)
            (_panel, _selection) = (true, 0);
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
        if (_browsing)
            refreshRooms();
        menu().Tick(keys, keys, escape);
        return true;
    }

    /// <summary>Leaves any room, without waiting on the network.</summary>
    public void Leave()
    {
        closePrompt();
        // In Song Select, showPartner() takes the partner off as it lays Song Select out again.
        if (_shownPartner is not null && shell.Active.Id != FlowScenes.SongSelect)
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
        if (IsOpen)
            quads.AddRange(menu().Quads());
        else if (banner() is { } text && (shell.Active.Id == FlowScenes.SongSelect || shell.Match.HoldsReveal && shell.Active.Id == FlowScenes.Gameplay))
        {
            // Over the free play line, between the name boards.
            float x = (OverlayPainter.StageWidth - BannerWidth) / 2;
            quads.AddRange(painter.Panel(x, BannerTop, BannerWidth, BannerHeight, (254, 205, 1), (0, 0, 0), radius: 14, outlineWidth: 3));
            quads.Add(painter.Text(text, OverlayPainter.StageWidth / 2, BannerTop + BannerHeight / 2, BannerWidth - 36, 30, slot: _bannerSlot));
        }
        return quads;
    }

    private List<Row> rows()
    {
        if (_warning)
            return _share is null ? [new(T("online.accept"), Action.Accept), new(T("online.refuse"), Action.Refuse)]
                : [new(T("online.share_accept"), Action.Accept), new(T("online.share_refuse"), Action.Refuse)];
        if (_session is not { } session)
        {
            if (!_browsing)
                return [new(T("online.quick_play"), Action.QuickPlay), new(T("online.host"), Action.Host), new(T("online.rooms"), Action.Rooms),
                    new(T("online.join_code"), Action.Code), new(T("menu.back"), Action.Back)];
            List<Row> rooms = [.. _rooms.Select(room => new Row(room.HasPassword ? $"{room.Name} ({T("online.locked")})" : room.Name, Action.Join, room))];
            rooms.Add(new(T("menu.back"), Action.Back));
            return rooms;
        }
        // Quick play still looking: only a way out.
        if (_quickPlay && !partnerConnected())
            return [new(T("online.stop_search"), Action.Leave)];
        List<Row> choose = shell.Match.MyTurn ? [new(T("online.choose_song"), Action.ChooseSong)] : [];
        // Waiting on the other's pick: nothing to go back to.
        List<Row> back = shell.Match.WaitingForTheirPick ? [] : [new(T("menu.back"), Action.Back)];
        if (!session.IsHost || session.Room is not { } current)
            return [.. choose, new(T("online.leave"), Action.Leave), .. choose.Count == 0 ? back : []];
        List<Row> host = choose;
        if (!current.IsQuick)
        {
            host.Add(new(shell.Match.Choice switch
            {
                SongChoice.Host => T("online.choice_host"),
                SongChoice.Guest => T("online.choice_guest"),
                _ => T("online.choice_turns"),
            }, Action.Choice));
            host.Add(new(current.IsPublic ? T("online.visibility_public") : T("online.visibility_private"), Action.Visibility));
            host.Add(new(current.HasPassword ? T("online.password_on") : T("online.password_off"), Action.Password));
        }
        host.Add(new(T("online.leave"), Action.Leave));
        if (choose.Count == 0)
            host.AddRange(back);
        return host;
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
            case Action.Rooms:
                browse(true);
                break;
            case Action.Back when _browsing && _session is null:
                browse(false);
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
            case Action.Choice:
                shell.Match.Choice = (SongChoice)(((int)shell.Match.Choice + 1) % 3);
                break;
            case Action.Password:
                ask(Prompt.NewPassword);
                break;
            case Action.Leave:
                Leave();
                _listedAt = 0;
                break;
            case Action.Accept when _share is not null:
                answerShare(true);
                break;
            case Action.Refuse when _share is not null:
                answerShare(false);
                break;
            case Action.Accept:
                acceptWarning();
                break;
            case Action.Back or Action.ChooseSong or Action.Refuse:
                ClosePanel();
                break;
        }
    }

    private void browse(bool on)
    {
        _browsing = on;
        _selection = 0;
        _listedAt = 0;
    }

    private bool partnerConnected() => _session is { Partner: not null, Link.State: PeerLinkState.Direct or PeerLinkState.Relayed };

    /// <summary>
    /// The other player lacks the host's custom song: the first time, the host confirms they may share it (kept in
    /// config.cfg); after that it goes at once. <paramref name="answer"/> says whether it may be sent.
    /// </summary>
    public void AskShare(Action<bool> answer)
    {
        if (shell.Arcade.ShareWarningAccepted)
        {
            answer(true);
            return;
        }
        closePrompt();
        (_share, _warning, _panel, _selection) = (answer, true, true, 0);
    }

    private void answerShare(bool share)
    {
        var answer = _share!;
        (_share, _warning) = (null, false);
        if (share)
            save(shell.Arcade with { ShareWarningAccepted = true });
        ClosePanel();
        answer(share);
    }

    // Asked once: the answer is kept in config.cfg.
    private void acceptWarning()
    {
        _warning = false;
        _selection = 0;
        save(shell.Arcade with { OnlineWarningAccepted = true });
    }

    private void save(Waddamburo.Game.Flow.ArcadeSettings settings)
    {
        shell.Arcade = settings;
        if (shell.Options.ArcadePath is not { } path)
            return;
        try
        {
            Waddamburo.Game.Flow.ArcadeSettings.SaveMenuSettings(path, shell.Arcade);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
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
            return _browsing && _rooms.Count == 0 ? T("online.no_rooms") : T("online.lobby_hint");
        if (banner() is { } text)
            return text;
        return T("online.starting");
    }

    // Someone joined the room, or left it: Song Select shows them, Don-chan and name board, on the other side.
    private void showPartner()
    {
        var partner = _session?.Partner;
        if (ReferenceEquals(partner, _shownPartner) || shell.Active.Id != FlowScenes.SongSelect || shell.Reload.Busy
            || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active)
            return;
        showPartnerAs(partner);
        // Someone came: shown at once on the side waiting for them. Someone left: Song Select lays itself out again
        // (once the lobby is gone, see listDanDojo).
        if (partner is not null && shell.Hosts.SongSelect is { } songSelect)
            songSelect.ShowPartner(shell.Hosts.PartnerSide);
        else
            (_relayout, _partnerLeft) = (true, true);
    }

    // In a room Song Select has no dan dojo (a course is played alone): it lays itself out again on joining and leaving.
    // Not under the lobby (its rainbow would cover the lobby, Don-chan and the name boards): once the lobby goes, as the
    // host goes to choose a song. A reload for anything else (the guest's coming to the host's song) lays it out too.
    private bool _roomListed;
    private bool _relayout;
    // A partner who left stays on screen (Don-chan, name board) until Song Select lays itself out again, so that
    // reload can't wait for the lobby: the lobby steps aside for its rainbow and comes back after it.
    private bool _partnerLeft;
    private bool _lobbyAside;

    private void listDanDojo()
    {
        var inRoom = Session is not null;
        if (shell.Reload.Busy)
        {
            (_roomListed, _relayout, _partnerLeft) = (inRoom, false, false);
            return;
        }
        if (_lobbyAside)
            (_panel, _lobbyAside) = (true, false);
        if (inRoom == _roomListed && !_relayout || _panel && !_partnerLeft || shell.Active.Id != FlowScenes.SongSelect)
            return;
        var aside = _panel;
        _panel = false;
        shell.Reload.Start(static () => { });
        if (shell.Reload.Busy)
            (_roomListed, _relayout, _partnerLeft, _lobbyAside) = (inRoom, false, false, aside);
        else
            _panel = aside; // a scene switch under way: next tick
    }

    private void showPartnerAs(ScoreProfile? partner)
    {
        _shownPartner = partner;
        var side = shell.Players.JoinedSides.Count == 0 ? 1 : 1 - shell.Players.JoinedSides.Min;
        TaikoGuest.Profiles[side] = partner;
        shell.Don?.SetLook(side, partner?.Look);
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
        if (_menu is not null && _menu.Page == _promptPage)
            _menu.Pop();
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
            RelayOnly = Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_RELAY_ONLY") == "1",
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
        // A timed-out request cancels the task rather than faulting it: Result would throw.
        if (!start.IsCompletedSuccessfully)
            Console.Error.WriteLine($"Warning ONLINE: {start.Exception?.GetBaseException().Message ?? "request timed out"}");
        var problem = !start.IsCompletedSuccessfully ? T("online.no_server")
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
        if (_session is not { Room: not null } session || _start is { IsCompleted: true } done && !(done.IsCompletedSuccessfully && done.Result))
            return null;
        if (session.Link.State is PeerLinkState.Direct or PeerLinkState.Relayed && shell.Match.Status is { } status)
            return status;
        // The other player left, the link not closed yet: waiting for the next one.
        if (session.Link.State is PeerLinkState.Direct or PeerLinkState.Relayed && session.Partner is null)
            return session.IsHost ? T("online.waiting_guest") : T("online.peer_left");
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
        : shell.Players.JoinedSides.Count == 1 ? TaikoGuest.Profiles[shell.Players.JoinedSides.Min]?.Token : null;

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
