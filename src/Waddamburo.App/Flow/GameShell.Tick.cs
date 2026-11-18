using System.Diagnostics;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

// One 60 Hz tick (tickOnce). Who gets it, in this order; each step may end the tick:
//  1. the service menu (cabinet test switch) takes everything while open;
//  2. F1 / an entry that gave up: back to the attract loop;
//  3. song reload and the input overlays (search, scores, online room, upload prompt): the scene still advances,
//     without input;
//  4. the home menu (pause, settings); online or over the attract, the scene goes on under it;
//  5. the home player setup (it drives the entry's drums; the scene doesn't advance);
//  6. the quick restart hold;
//  7. the scene advances with the keys; results held by the home controls stop here;
//  8. the scene's own tick, or (a transition pending) the switch: after the last voice, under a fade for the entry
//     and Song Select.
// ponytail: an ordered IInputLayer list was planned (docs/development/app-refactor.md) and dropped: the steps differ in
// whether the scene advances and with which keys, so a uniform loop would need a flag per step.
internal sealed partial class GameShell
{
    // One 60 Hz tick: the input first (back to the attract loop, coins, the home controls, the player
    // setup, the quick restart), then the active scene, the overlays, and a pending scene switch.
    // Diagnostic: WADDAMBURO_TICK_STATS=1 prints the game logic's average time and allocation per tick every 600 ticks,
    // with the scene (the Lumen runtime's before/after measure).
    private static readonly bool s_tickStats = Environment.GetEnvironmentVariable("WADDAMBURO_TICK_STATS") == "1";
    private long _tickStatsTicks, _tickStatsBytes;

    private void tick(SdlKeyboardSnapshot keyboard)
    {
        if (!s_tickStats)
        {
            tickOnce(keyboard);
            return;
        }
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        tickOnce(keyboard);
        _tickStatsTicks += Stopwatch.GetTimestamp() - start;
        _tickStatsBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (Tick % 600 == 0)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Tick stats at {Tick} ({Active.Id}): {Stopwatch.GetElapsedTime(0, _tickStatsTicks).TotalMilliseconds / 600:F3} ms, {_tickStatsBytes / 600 / 1024.0:F1} KiB per tick."));
            _tickStatsTicks = _tickStatsBytes = 0;
        }
    }

    private void tickOnce(SdlKeyboardSnapshot keyboard)
    {
        Tick++;
        _rumble.Update();
        Sounds?.Bank.Pump();
        AudioOutput.CheckFailure(Audio);
        // Diagnostic: WADDAMBURO_CRASH_AT=<tick> crashes the game there, to see its error screen.
        if (_crashAt == Tick)
            throw new InvalidOperationException($"WADDAMBURO_CRASH_AT asked the game to crash at tick {Tick}.");
        var keys = Options.InputTimeline.Apply(Tick, keyboard);
        _benchmark?.Tick();
        _flowSmoke?.Tick();
        Book.PowerOnTicks++;
        if (_gameplay.Playing)
            Book.PlayTicks++;
        if (Tick % 3600 == 0)
            Book.Save();
        if (_cabinet.TickServiceMenu(keys))
            return;
        var presses = _input.Take(keys);
        _presence.Report();
        Match.Tick();
        // F1 (back to the attract loop) is a home shortcut; a cabinet only gets there through its movies.
        if (backToAttract(Arcade.Home && presses.Attract))
            return;
        _cabinet.TakeCoins(_input.TakeCoins(keys));
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        // A reviewed play takes the mouse wheel (seeking) like a menu does.
        Application.MenuInput = !Scenes.OnLane || _home.MenuOpen || _gameplay.Reviewing && !_gameplay.Attempting;
        var held = keys;
        keys = scene.MapKeys(_input.Pulses(keys));
        // Home: Backspace is Escape too (a Tatacon's B button), except while it edits text or is being bound.
        var escape = _input.EscapePressed(keys, menuKey: Arcade.Home && !_search.IsOpen && !Room.Typing && !DeviceTransfer.Typing && !_home.CapturingBinding ? GameActions.Key(GameAction.Menu) : null);
        // Focus is followed whichever overlay takes the keys (the mute in background).
        _home.FollowFocus();
        // Song Select's search field takes the keyboard while open (its Escape closes it, not the menu).
        if (Reload.Tick() || !_home.MenuOpen && overlaysTake(keys, escape))
        {
            advance(scene, SdlKeyboardSnapshot.Empty);
            return;
        }
        if (_home.Tick(keys, held, escape))
        {
            // Online, the menu doesn't stop the song: its movies keep moving and it ends as it would. The attract
            // and its demo play go on too (muted).
            if (Match.InSong && Active.Id == FlowScenes.Gameplay || _home.AttractUnderMenu)
            {
                advance(scene, SdlKeyboardSnapshot.Empty);
                scene.Tick(new FlowInput(SdlKeyboardSnapshot.Empty, null, false, false));
            }
            return;
        }
        if (!Scenes.OnLane)
            keys = menuKeys(keys);
        // Home: the player setup runs inside the entry and takes the drums; the movie only animates.
        if (_playerSetup is not null && _playerSetup.Tick(ref keys))
            return;
        if (_home.QuickRestart(held))
            return;
        advance(scene, keys);
        var switching = Coordinator.Flow.State == GameFlowState.TransitionPending;
        if (_home.HoldsResults(held, switching))
            return;
        if (!switching)
        {
            scene.Tick(new FlowInput(keys, presses.DrumSide, presses.Skip, escape));
            return;
        }
        // A movie asked for the next scene (entry -> song select); its last voice finishes first.
        if (Sounds?.Bank.IsVoicePlaying == true)
            return;
        if (Active.Id == FlowScenes.Entry && Coordinator.Flow.PendingTransition is { } request)
            Players.ChooseMode(aiBattle: request.SceneNumber == 10);
        // The entry and Song Select fade to black first, as the menu's Restart, Song Select and Title do;
        // not under an intermission (the rainbow into a song covers its own switch).
        if (!Overlay.IsShown && PlayRequests.Pending is null && (Active.Id == FlowScenes.Entry || Active.Id == FlowScenes.SongSelect))
        {
            _home.FadeThen(() =>
            {
                if (Coordinator.Flow.State == GameFlowState.TransitionPending)
                    Scenes.ApplyPending();
            });
            return;
        }
        Scenes.ApplyPending();
    }

    // An open overlay takes the tick's input; with none open, each may open on its own key (Tab, R).
    private bool overlaysTake(SdlKeyboardSnapshot keys, bool escape) =>
        _inputOverlays.LastOrDefault(static overlay => overlay.IsOpen) is { } open
            ? open.Tick(keys, escape) || true
            : _inputOverlays.Any(overlay => overlay.Tick(keys, escape));

    // F1 (testing convenience, not cabinet behaviour) or an entry that gave up: drop the credit and
    // return to the attract loop from any menu scene (not mid-song, whose music the gameplay flow owns).
    private bool backToAttract(bool f1)
    {
        var toAttract = f1 || _returnToAttract;
        _returnToAttract = false;
        if (!toAttract || Scenes.OnLane || Active.Id == FlowScenes.Boot)
            return false;
        // Fade out first; the switch below then runs under the black (Covering).
        if (!_home.Covering)
        {
            _home.FadeThen(() => GoToAttract());
            return false;
        }
        Sounds?.StopAll();
        SongSelectFlow.AbandonCover();
        Don?.SetDialogDon(false);
        Players.Reset();
        Credit.SongsPlayed = 0; // a new credit starts at the first song
        Scenes.Show(FlowScenes.Logo);
        return true;
    }

    // The scene's movies and the overlays drawn with it advance one tick.
    private void advance(FlowScene scene, SdlKeyboardSnapshot keys)
    {
        scene.Advance(LumenInputAdapter.CreateSnapshot(keys,
            Scenes.OnLane ? LumenInputMode.PresentationOnly : LumenInputMode.AuthoredControls));
        Overlay.Advance();
        // Diagnostic: WADDAMBURO_DUMP_TREE=<tick> prints the active scene's display lists.
        if (_dumpTreeTick == Tick)
            foreach (var (layer, index) in Active.Layers.Select((layer, index) => (layer, index)))
            {
                Console.WriteLine($"== layer {index} {layer.Definition.MovieId}");
                foreach (var line in Active.Player.Layers[index].Player.DescribeDisplayList())
                    Console.WriteLine(line);
            }
        if (_indicators is null)
            return;
        if (_indicatorScene != Active.Id)
        {
            _indicatorScene = Active.Id;
            _indicators.SetScene(scene.IndicatorsFor(Active.Id));
        }
        _indicators.Advance();
    }

    // Everything the game is doing stops and its state goes: sounds, fades, menus, the song (dropped unsaved), a
    // pending scene switch or play, the room, the credit's players; the active flow cleans up as it leaves for the
    // attract's logo, which waits (not ticked) under the service menu.
    private void resetForService()
    {
        Audio?.Mixer.StopAll();
        Sounds?.StopAll();
        Scenes.CancelFade();
        _home.Reset();
        _search.Close();
        ClosePlayerSetup();
        if (Room.Session is not null)
            Room.Leave();
        if (Active.Id == FlowScenes.Gameplay)
            _gameplay.Discard();
        Coordinator.Flow.CancelPendingTransition();
        PlayRequests.CancelPending();
        Overlay.Clear();
        Don?.SetDialogDon(false);
        Players.Reset();
        Credit.SongsPlayed = 0;
        _returnToAttract = false;
        Coins?.Clear();
        Scenes.Show(FlowScenes.Logo);
        Audio?.Mixer.StopAll(); // anything the switch started
        Console.WriteLine($"Service menu: game reset at tick {Tick}.");
    }
}
