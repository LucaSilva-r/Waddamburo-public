using System.Diagnostics;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Home;

/// <summary>
/// Home's quick restart of the song: holding Q fades to black over half a second and restarts once
/// black (releasing early fades back); the pause menu's Restart does the same without the hold, in a
/// quarter second. The rainbow then covers the new start and the black fades away over a quarter second. The menu's
/// Song Select and Title fade the same way, in any scene.
/// </summary>
internal sealed class QuickRestart(Func<bool> restart)
{
    private long _qPressedAt;
    private float _qStartAlpha;
    private bool _qNeedsRelease;
    private long _restartCancelAt;
    private float _restartCancelAlpha;
    private long _menuRestartAt;
    private Func<bool>? _menuThen;
    private long _restartRevealAt;
    // The menu's fade to black and the fade back after any of them (holding Q keeps its half second).
    public static readonly TimeSpan MenuFade = TimeSpan.FromMilliseconds(250);

    /// <summary>Q is being held toward a restart.</summary>
    public bool Holding => _qPressedAt != 0;

    /// <summary>The pause menu's Restart is fading to black.</summary>
    public bool MenuPending => _menuRestartAt != 0;

    /// <summary>The menu's Restart (or <paramref name="then"/>: Song Select, Title): fade to black, then do it.</summary>
    public void FromMenu(Func<bool>? then = null)
    {
        _menuRestartAt = Stopwatch.GetTimestamp();
        _menuThen = then;
    }

    /// <summary>The screen is black under a restart (or the menu's Song Select) switching scenes.</summary>
    public bool Covering { get; private set; }

    /// <summary>The game paused: a Q hold in progress is dropped.</summary>
    public void CancelHold() => _qPressedAt = 0;

    /// <summary>
    /// One tick; <paramref name="allowed"/>: home mode on a restartable song or its results;
    /// <paramref name="holdAllowed"/> false: only the menu restarts (a replay or practice keeps Q for itself).
    /// True when the song restarted this tick (the tick stops there).
    /// </summary>
    public bool Update(bool allowed, SdlKeyboardSnapshot held, bool holdAllowed = true)
    {
        if (!allowed)
        {
            _qPressedAt = 0;
            _restartCancelAt = 0;
            if (_menuThen is null)
                _menuRestartAt = 0;
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        if (_menuRestartAt != 0 && _menuThen is null && Stopwatch.GetElapsedTime(_menuRestartAt, now) >= MenuFade)
        {
            _menuRestartAt = 0;
            return restartNow(restart);
        }
        if (!holdAllowed || !Flow.GameActions.Down(held, Flow.GameAction.Restart))
        {
            if (_qPressedAt != 0)
            {
                _restartCancelAlpha = qHoldAlpha(now);
                _restartCancelAt = now;
            }
            _qPressedAt = 0;
            _qNeedsRelease = false;
        }
        else if (!_qNeedsRelease && _qPressedAt == 0 && _menuRestartAt == 0 && _restartRevealAt == 0)
        {
            _qStartAlpha = cancelAlpha(now);
            _restartCancelAt = 0;
            _qPressedAt = now;
        }
        if (_qPressedAt != 0 && qHoldAlpha(now) >= 1)
        {
            _qPressedAt = 0;
            _qNeedsRelease = true;
            return restartNow(restart);
        }
        if (_restartRevealAt != 0 && Stopwatch.GetElapsedTime(_restartRevealAt, now) >= MenuFade)
            _restartRevealAt = 0;
        if (_restartCancelAt != 0 && cancelAlpha(now) <= 0)
            _restartCancelAt = 0;
        return false;
    }

    /// <summary>
    /// A menu action other than Restart, once its fade is black (each tick, in any scene, ahead of
    /// anything that could take the tick). True when it ran (the tick stops there).
    /// </summary>
    public bool UpdateMenuAction() =>
        _menuThen is { } then && Stopwatch.GetElapsedTime(_menuRestartAt) >= MenuFade
        && restartNow(then);

    /// <summary>The black drawn over the game (0 to 1): fading in toward a restart, or away after one.</summary>
    public float Black
    {
        get
        {
            if (_menuRestartAt != 0)
                return Math.Clamp((float)(Stopwatch.GetElapsedTime(_menuRestartAt) / MenuFade), 0, 1);
            if (_qPressedAt != 0)
                return qHoldAlpha(Stopwatch.GetTimestamp());
            if (_restartCancelAt != 0)
                return cancelAlpha(Stopwatch.GetTimestamp());
            if (_restartRevealAt != 0)
                return Math.Clamp(1 - (float)(Stopwatch.GetElapsedTime(_restartRevealAt) / MenuFade), 0, 1);
            return 0;
        }
    }

    /// <summary>
    /// The switch's old frame is still held (its Don already re-rendered in the new scene's state):
    /// the fade back has not started yet.
    /// </summary>
    public void HoldBlack()
    {
        // Only a fade back still under way: a finished one's timestamp stays set outside songs (Update
        // clears it only where a restart is allowed), and restarting it blacked out every later switch.
        if (_restartRevealAt != 0 && Stopwatch.GetElapsedTime(_restartRevealAt) < MenuFade)
            _restartRevealAt = Stopwatch.GetTimestamp();
    }

    private bool restartNow(Func<bool> action)
    {
        _qPressedAt = 0;
        _restartCancelAt = 0;
        _menuRestartAt = 0;
        _menuThen = null;
        Covering = true;
        try
        {
            if (action())
                _restartRevealAt = Stopwatch.GetTimestamp();
        }
        finally
        {
            Covering = false;
        }
        return true;
    }

    private float qHoldAlpha(long now) => Math.Clamp(_qStartAlpha
        + (float)(Stopwatch.GetElapsedTime(_qPressedAt, now).TotalSeconds * 2), 0, 1);

    private float cancelAlpha(long now) => _restartCancelAt == 0 ? 0 : Math.Clamp(_restartCancelAlpha
        - (float)(Stopwatch.GetElapsedTime(_restartCancelAt, now).TotalSeconds * 2), 0, 1);
}
