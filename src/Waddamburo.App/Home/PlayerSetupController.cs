using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Flow;

namespace Waddamburo.App.Home;

/// <summary>
/// Home's "who's playing?" screen (stored accounts, guests, friends, in-game login), run inside the
/// entry: it takes the drums while open, dresses the stands, draws the names, arrows and options over
/// the entry, and joins the chosen players when confirmed.
/// </summary>
internal sealed class PlayerSetupController : IDisposable
{
    private readonly GameShell shell;
    private readonly PlayerSetupFlow _flow;
    private readonly EntrySetupOverlay _overlay;
    private readonly OverlayMenu _signIn;

    public PlayerSetupController(GameShell shell, AccountBook accounts, string fontPath, string menuFontPath, string? scoresPath)
    {
        this.shell = shell;
        _overlay = new EntrySetupOverlay(shell.Application, fontPath, shell.ResolveSurface);
        _signIn = new OverlayMenu(shell.Application, menuFontPath)
        {
            Sound = don => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, Waddamburo.Platform.Sdl.Media.AudioBus.DrumHit, trace: false),
        };
        _flow = new PlayerSetupFlow(accounts, Online.UpdateGate.Server(shell.Arcade), shell.Arcade.ServerInsecure, shell.Sync.ClientFor,
            Path.Combine(Path.GetDirectoryName(scoresPath) ?? ".", "avatars"));
        _flow.Confirmed += joinSetupPlayers;
        _flow.Refused += () => shell.Sounds?.Frontend.PlayCue("SE_COM", 12); // SE_COM_COM_CARD_ERROR
        _flow.Cancelled += () =>
        {
            shell.Credit.EntrySetup = false;
            shell.ReturnToAttract();
        };
    }

    /// <summary>The player setup is showing (it takes the controls).</summary>
    public bool IsOpen => _flow.IsOpen;

    /// <summary>
    /// One tick, before the scene: Tab in the entry reopens the setup with the current players (true:
    /// the tick stops there). While open the setup takes the drums: <paramref name="keys"/> comes back empty.
    /// </summary>
    public bool Tick(ref SdlKeyboardSnapshot keys)
    {
        if (!_flow.IsOpen && shell.Active.Id == FlowScenes.Entry && keys.IsDown(SdlKeyboardKey.Tab))
        {
            ScoreProfile?[] profiles = [.. TaikoGuest.Profiles];
            int[] joined = [.. shell.Players.JoinedSides];
            shell.FadeThen(() =>
            {
                if (_flow.IsOpen || shell.Active.Id != FlowScenes.Entry)
                    return;
                beginSetup(joined.Length > 0 ? joined[0] : 0);
                _flow.Reopen(profiles, joined);
            });
            return true;
        }
        var open = _flow.IsOpen;
        // Sign in waiting for the website: its overlay takes the keys (Escape cancels it, not the setup).
        if (open && _flow.SignIn is { } signIn)
        {
            if (!_signIn.IsOpen)
                openSignIn(signIn);
            _signIn.Tick(keys, keys, keys.IsDown(SdlKeyboardKey.Escape));
            keys = SdlKeyboardSnapshot.Empty;
        }
        else if (_signIn.IsOpen)
            _signIn.Close();
        _flow.Tick(open ? keys : SdlKeyboardSnapshot.Empty);
        if (open && _flow.IsOpen && shell.Active.Id == FlowScenes.Entry && shell.Hosts.Entry is { SetupMode: true } entry)
        {
            applySetup(entry, _flow.Columns());
            setupArrows().Advance();
            // The drums pick only once the screen is fully up: the entry on screen, no transition over
            // it, and its intro (Don entry motion, boards fading in) played.
            if (!shell.Overlay.IsShown)
                _setupShownTicks++;
            _flow.InputEnabled = _setupShownTicks >= SetupIntroTicks;
        }
        if (open && shell.Tick % 10 == 0 && Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_TRACE") == "1")
            Console.WriteLine("[setup] " + shell.Tick + " entry=" + (shell.Hosts.Entry?.Ticks ?? -1) + ": " + string.Join(" | ", _flow.Columns().Select(c => c.Choice.Kind + " " + c.Choice.Label + " ready=" + c.Ready)));
        if (open)
            keys = SdlKeyboardSnapshot.Empty;
        return false;
    }

    /// <summary>Back to the title from the menu: an open setup closes.</summary>
    public void Close()
    {
        if (!_flow.IsOpen)
            return;
        _flow.Close();
        shell.Credit.EntrySetup = false;
    }

    public void Dispose()
    {
        _flow.Dispose();
        _overlay.Dispose();
        _signIn.Dispose();
    }

    /// <summary>A drum hit in the attract loop opens the player setup (that drum starts on the default account).</summary>
    public void Open(int side)
    {
        var setup = _flow;
        shell.Sounds?.StopAll();
        // Diagnostic: WADDAMBURO_SETUP_BOTH=1 skips the screen with both drums joined (the DON_BOTH entry
        // start): the first two stored accounts, a guest where there is none.
        if (Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_BOTH") == "1")
        {
            var stored = shell.Accounts?.Accounts ?? [];
            startWithPlayers([.. Enumerable.Range(0, 2).Select(index => index < stored.Count ? stored[index].Profile : null)], [true, true]);
            return;
        }
        shell.Sounds?.StopAll();
        beginSetup(side);
        setup.Open(side);
    }

    // The player setup is the entry itself: both stands up (DON_BOTH), nobody joined yet, the menus
    // hidden (EntrySceneHost.SetupMode). Confirming joins the chosen players in place (FinishSetup).
    private const int SetupIntroTicks = 90; // 1.5 s: the Don's entry motion (traced 0.85 s) and the boards
    private int _setupShownTicks;

    // The entry starts with the opening drum joined (DON_1P/_2P: the movie greets it, once); the host
    // joins the other stand right after, the movie's way for a drum press (no greeting).
    private void beginSetup(int side)
    {
        _setupShownTicks = 0;
        shell.Players.Reset();
        shell.Credit.SongsPlayed = 0;
        Array.Clear(_shownChoice);
        Array.Fill(_swapAt, -1);
        Array.Fill(_revealAt, -1);
        shell.Credit.EntrySetup = true;
        shell.Credit.EntryTrigger = side;
        shell.ResetIndicatorScene();
        shell.Scenes.Show(FlowScenes.Entry);
    }

    // Each side's choice on its stand: its Don in the chosen look (options show as text instead) and
    // the entry's own costume flash and cue when it changes.
    private void applySetup(EntrySceneHost entry, SetupColumn[] columns)
    {
        for (var side = 0; side < 2; side++)
        {
            var column = columns[side];
            var choice = column.Choice;
            // A new choice starts the entry's costume change (smoke and cue); what stands on the drum only
            // swaps once the smoke covers it, so the new Don or text never pops in.
            if (_shownChoice[side] is not { } shown)
                showOnStand(side, choice);
            else if (sameStand(shown, choice))
                _swapAt[side] = -1;
            else if (_swapAt[side] < 0)
            {
                entry.CostumeChanged(side);
                _swapAt[side] = shell.Tick + SetupSwapDelay;
                _revealAt[side] = shell.Tick + SetupRevealDelay;
            }
            else if (shell.Tick >= _swapAt[side])
            {
                showOnStand(side, choice);
                _swapAt[side] = -1;
            }
            entry.SetSetupSide(side, _shownChoice[side]!.HasDon, board: true);
        }
        // Both drums show their choice: no "hit the drum to start" bubble during the setup.
        shell.Indicators?.SetupPanels(false, false);
    }

    // The costume smoke covers the stand from ~6 ticks and bursts at ~54: the Don swaps under it, and text
    // (drawn over the scene, so it would show through the smoke) appears with the burst.
    private const int SetupSwapDelay = 12, SetupRevealDelay = 54;
    private readonly SetupChoice?[] _shownChoice = new SetupChoice?[2];
    private readonly long[] _swapAt = [-1, -1], _revealAt = [-1, -1];

    private void showOnStand(int side, SetupChoice choice)
    {
        _shownChoice[side] = choice;
        if (choice.HasDon)
            shell.Don?.SetLook(side, choice.Look);
        shell.Hosts.Entry?.SetSetupProfile(side, choice.Profile);
    }

    // Two choices look the same on the stand: the same Don look, or the same option text.
    private static bool sameStand(SetupChoice a, SetupChoice b) =>
        a.HasDon == b.HasDon && (a.HasDon ? a.Look == b.Look : a.Kind == b.Kind);

    // The entry's own animated arrows (SetupArrows), per loaded entry; anchored on each stand.
    private SetupArrows? _setupArrows;
    private LumenGameSceneInstance? _setupArrowsScene;
    private static readonly (float X, float Y)[] ArrowAnchors = [(150, 430), (1130, 430)];
    private const float ArrowGap = 150, ArrowScale = 0.45f;
    private const int AutoJoinStar = 295; // entry.lm texture: the yellow four-point sparkle (40x40)
    private const float AutoJoinStarSize = 44;
    private static readonly (float X, float Y)[] AutoJoinStarAt = [(85, 345), (1065, 345)];

    private SetupArrows setupArrows()
    {
        if (_setupArrows is null || _setupArrowsScene != shell.Active)
        {
            _setupArrows = new SetupArrows(shell.Active.Layers[0].Content.Definition);
            _setupArrowsScene = shell.Active;
        }
        return _setupArrows;
    }

    // The settings menu's modal look: what to do in the browser, the code for another device, the browser again.
    private void openSignIn(PendingSignIn signIn)
    {
        var page = $"{signIn.Url.Host}{signIn.Url.AbsolutePath}";
        var code = $"{signIn.Code[..3]}-{signIn.Code[3..]}";
        _signIn.Open(new OverlayPage(() => Strings.T("signin.title"), () =>
            [new(Strings.T("signin.open_browser"), Enter: () => SdlApplication.OpenUrl(signIn.Url)),
                new(Strings.T("signin.cancel"), Enter: _flow.CancelSignIn)],
            Centred: true, Modal: true, Back: _flow.CancelSignIn, Body: () =>
            [(Strings.T(signIn.BrowserOpened ? "signin.browser_opened" : "signin.no_browser"), OverlayMenu.White),
                (Strings.T("signin.approve"), OverlayMenu.White),
                ("", OverlayMenu.White),
                (Strings.T("signin.other_device", page), OverlayMenu.White),
                (code, OverlayMenu.Yellow),
                (Strings.T("signin.expires", Math.Max(0, (int)(signIn.Deadline - DateTime.UtcNow).TotalSeconds)), OverlayMenu.White)]),
            home: true);
    }

    /// <summary>The sign-in overlay is open: the shell frosts everything under it, as under the settings.</summary>
    public bool SignInOpen => _signIn.IsOpen;

    public IEnumerable<RenderQuad> SignInQuads() => shell.Unsqueezed(_signIn.Quads(), centred: true);

    /// <summary>Home players' names over the entry's boards, and the setup's arrows and option text.</summary>
    public IEnumerable<RenderQuad> Quads(float interpolation)
    {
        var overlay = _overlay;
        if (shell.Active.Id != FlowScenes.Entry)
            return [];
        // The tag follows the live choice; the stand shows what is on it now (it swaps mid-smoke), and its
        // text only once the smoke has burst.
        var live = _flow is { IsOpen: true } setup && shell.Hosts.Entry is { SetupMode: true } ? setup.Columns() : null;
        var columns = live?.Select((column, side) => column with { Choice = _shownChoice[side] ?? column.Choice }).ToArray();
        bool[] standVisible = [shell.Tick >= _revealAt[0], shell.Tick >= _revealAt[1]];
        string?[] tags = live is not null
            ? [.. live.Select(static column => column.Choice.Label)]
            : [.. Enumerable.Range(0, 2).Select(side => shell.Players.JoinedSides.Contains(side) ? TaikoGuest.Profiles[side]?.DisplayName ?? Strings.T("setup.guest") : null)];
        bool[] ranked = [.. Enumerable.Range(0, 2).Select(side =>
            (columns is not null ? columns[side].Choice.Profile?.DisplayDanRank : TaikoGuest.DanRank(side)) > 0)];
        var quads = overlay.Quads(columns, tags, ranked, standVisible);
        if (columns is null)
            return quads;
        // The account that joins by itself (S): the entry's yellow sparkle over the Don's top-left, twinkling.
        // The live choice decides (S toggles it at once); the stand must already show that account.
        for (var side = 0; side < 2; side++)
            if (live![side].Choice is { IsDefault: true, HasDon: true } choice && columns[side].Choice.Baid == choice.Baid
                && standVisible[side] && AutoJoinStar < shell.SceneTextureIds.Length)
            {
                var size = AutoJoinStarSize * (0.85f + 0.15f * MathF.Sin((shell.Tick + interpolation) * 0.08f));
                var (x, y) = AutoJoinStarAt[side];
                quads = quads.Append(RenderQuad.FromRectangles(shell.SceneTextureIds[AutoJoinStar],
                    new RenderRectangle((x - size / 2) / 1280f, (y - size / 2) / 720f, size / 1280f, size / 720f),
                    RenderRectangle.Full, RenderColor.White, RenderColor.Transparent));
            }
        // The arrows wait for the stands' slide-in (the same intro gate as the drums).
        if (_setupShownTicks < SetupIntroTicks)
            return quads;
        // The arrows while a drum is choosing (not locked in, no code on screen). The entry is its scene's
        // first layer, so the arrow sprite's texture indices are the scene's.
        var arrows = setupArrows();
        for (var side = 0; side < 2; side++)
            if (!columns[side].Ready && columns[side].Code is null)
                quads = quads.Concat(SceneTextures.Compose(arrows.Snapshot(side, ArrowAnchors[side].X, ArrowAnchors[side].Y,
                    ArrowGap, ArrowScale, interpolation), shell.SceneTextureIds, "Arrows", shell.ResolveSurface).Quads);
        return quads;
    }

    // The setup's players: their profiles and looks go in first, then the entry starts with them joined
    // (SCENE_TRIGGER_DON_1P 0 / _DON_2P 1 / _DON_BOTH 2: the movie joins those drums itself).
    private void startWithPlayers(ScoreProfile?[] profiles, bool[] playing)
    {
        shell.Sounds?.Attract.PlayExit();
        shell.Credit.EntrySetup = false;
        shell.ResetIndicatorScene(); // the entry reloads under the same id: its indicators start over
        shell.Players.Reset();
        shell.Credit.SongsPlayed = 0;
        for (var side = 0; side < 2; side++)
        {
            if (!playing[side])
                continue;
            TaikoGuest.Profiles[side] = profiles[side];
            if (profiles[side] is { } profile)
                shell.Sync.RefreshBests(profile); // a visitor's first, a stored account's again
            shell.Don?.SetLook(side, profiles[side]?.Look);
            shell.Players.Join(side);
        }
        shell.Credit.EntryTrigger = playing[0] && playing[1] ? 2 : playing[1] ? 1 : 0;
        Console.WriteLine($"Player setup: 1P {describe(0)}, 2P {describe(1)}.");
        string describe(int side) => !playing[side] ? "not playing" : profiles[side] is { } profile ? $"{profile.Name} (baid {profile.Baid})" : "guest";
        shell.Scenes.Show(FlowScenes.Entry);
    }

    // The setup's players join the entry already on screen (its music and Don motions carry on).
    private void joinSetupPlayers(ScoreProfile?[] profiles, bool[] playing)
    {
        if (shell.Hosts.Entry is not { SetupMode: true } entry || shell.Active.Id != FlowScenes.Entry)
        {
            startWithPlayers(profiles, playing);
            return;
        }
        shell.Credit.EntrySetup = false;
        for (var side = 0; side < 2; side++)
        {
            if (!playing[side])
                continue;
            TaikoGuest.Profiles[side] = profiles[side];
            if (profiles[side] is { } profile)
                shell.Sync.RefreshBests(profile);
            shell.Don?.SetLook(side, profiles[side]?.Look);
        }
        entry.FinishSetup(playing); // PlayerJoined -> JoinPlayer per side
        Console.WriteLine($"Player setup: 1P {describe(0)}, 2P {describe(1)}.");
        string describe(int side) => !playing[side] ? "not playing" : profiles[side] is { } profile ? $"{profile.Name} (baid {profile.Baid})" : "guest";
    }

}
