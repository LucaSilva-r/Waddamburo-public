using System.Diagnostics;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Home;

/// <summary>
/// Home's quick restart of the song: holding Q fades to black over half a second and restarts once
/// black (releasing early fades back); the pause menu's Restart does the same without the hold. The
/// rainbow then covers the new start and the black fades away over another half second.
/// </summary>
internal sealed class QuickRestart(Func<bool> restart)
{
    private long _qPressedAt;
    private float _qStartAlpha;
    private bool _qNeedsRelease;
    private long _restartCancelAt;
    private float _restartCancelAlpha;
    private long _menuRestartAt;
    private long _restartRevealAt;

    /// <summary>Q is being held toward a restart.</summary>
    public bool Holding => _qPressedAt != 0;

    /// <summary>The pause menu's Restart is fading to black.</summary>
    public bool MenuPending => _menuRestartAt != 0;

    /// <summary>The pause menu's Restart: fade to black, then restart.</summary>
    public void FromMenu() => _menuRestartAt = Stopwatch.GetTimestamp();

    /// <summary>The game paused: a Q hold in progress is dropped.</summary>
    public void CancelHold() => _qPressedAt = 0;

    /// <summary>
    /// One tick; <paramref name="allowed"/>: home mode on a restartable song or its results. True when
    /// the song restarted this tick (the tick stops there).
    /// </summary>
    public bool Update(bool allowed, SdlKeyboardSnapshot held)
    {
        if (!allowed)
        {
            _qPressedAt = 0;
            _restartCancelAt = 0;
            _menuRestartAt = 0;
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        if (_menuRestartAt != 0 && Stopwatch.GetElapsedTime(_menuRestartAt, now) >= TimeSpan.FromMilliseconds(500))
        {
            _menuRestartAt = 0;
            return restartNow();
        }
        if (!held.IsDown(SdlKeyboardKey.Q))
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
            return restartNow();
        }
        if (_restartRevealAt != 0 && Stopwatch.GetElapsedTime(_restartRevealAt, now) >= TimeSpan.FromMilliseconds(500))
            _restartRevealAt = 0;
        if (_restartCancelAt != 0 && cancelAlpha(now) <= 0)
            _restartCancelAt = 0;
        return false;
    }

    /// <summary>The black drawn over the game (0 to 1): fading in toward a restart, or away after one.</summary>
    public float Black
    {
        get
        {
            if (_menuRestartAt != 0)
                return Math.Clamp((float)(Stopwatch.GetElapsedTime(_menuRestartAt).TotalSeconds * 2), 0, 1);
            if (_qPressedAt != 0)
                return qHoldAlpha(Stopwatch.GetTimestamp());
            if (_restartCancelAt != 0)
                return cancelAlpha(Stopwatch.GetTimestamp());
            if (_restartRevealAt != 0)
                return Math.Clamp(1 - (float)(Stopwatch.GetElapsedTime(_restartRevealAt).TotalSeconds * 2), 0, 1);
            return 0;
        }
    }

    private bool restartNow()
    {
        _qPressedAt = 0;
        _restartCancelAt = 0;
        _menuRestartAt = 0;
        if (restart())
            _restartRevealAt = Stopwatch.GetTimestamp();
        return true;
    }

    private float qHoldAlpha(long now) => Math.Clamp(_qStartAlpha
        + (float)(Stopwatch.GetElapsedTime(_qPressedAt, now).TotalSeconds * 2), 0, 1);

    private float cancelAlpha(long now) => _restartCancelAt == 0 ? 0 : Math.Clamp(_restartCancelAlpha
        - (float)(Stopwatch.GetElapsedTime(_restartCancelAt, now).TotalSeconds * 2), 0, 1);
}
