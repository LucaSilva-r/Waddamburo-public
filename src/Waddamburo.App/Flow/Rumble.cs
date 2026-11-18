using Waddamburo.App.Gameplay;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// #35: the controllers bound to the lane's player (lane null: the one player) rumble, by the VIBRATION setting: big
/// notes, rolls/balloons/kusudama and their pops; every note hit; or every drum hit. A don rumbles the low (heavy) motor,
/// longer and boomy, a ka the high one, short and dry (SDL maps the two to distinct frequencies on HD-rumble and
/// voice-coil controllers); a big note both, harder. Not in a replay or autoplay. ponytail: strengths by feel, untraced.
/// </summary>
internal sealed class Rumble(GameShell shell, GameplayFlow gameplay)
{
    private static readonly (float, float, int) DonFeel = (0.55f, 0f, 70), KaFeel = (0f, 0.45f, 25), BigFeel = (0.9f, 0.5f, 110);

    // Per side: until when (Environment.TickCount64) the last rumble plays, and how strong. A weaker one doesn't cut a
    // stronger one short (a big note's rumble isn't replaced by the drum press's small one right after it).
    private readonly long[] _until = new long[2];
    private readonly float[] _strength = new float[2];

    // Rumbles that change over time (a pop): each tick sets the motors to the shape at that point (t 0-1 over its length).
    private readonly List<(int Side, int[] Pads, long Start, double Seconds, Func<float, (float Low, float High)> Shape)> _shaped = [];

    /// <summary>A drum was hit.</summary>
    public void Hit(int? lane, TaikoInputAction hit)
    {
        if (shell.Arcade.ControllerVibration == Waddamburo.Game.Flow.ControllerVibration.EveryHit)
            play(lane, hit is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? DonFeel : KaFeel);
    }

    /// <summary>A gameplay sound played (a note hit, a roll, a pop).</summary>
    public void Sound(int? lane, GameplaySoundEvent sound)
    {
        var level = shell.Arcade.ControllerVibration;
        switch (sound)
        {
            case GameplaySoundEvent.BigHit:
                play(lane, BigFeel);
                break;
            // Rolls, balloons and kusudama from Big notes up (a big roll as a big note).
            case GameplaySoundEvent.LongDonHit or GameplaySoundEvent.LongKaHit:
                play(lane, sound == GameplaySoundEvent.LongDonHit ? DonFeel : KaFeel);
                break;
            case GameplaySoundEvent.BigLongHit:
                play(lane, BigFeel);
                break;
            // All notes: only presses that hit a note (Every hit feels each drum press instead).
            case GameplaySoundEvent.DonHit or GameplaySoundEvent.KaHit when level == Waddamburo.Game.Flow.ControllerVibration.AllNotes:
                play(lane, sound == GameplaySoundEvent.DonHit ? DonFeel : KaFeel);
                break;
            // A balloon pops: a sharp crack (light motor) over a thump (heavy one) that dies at once.
            case GameplaySoundEvent.BalloonPopped:
                shaped(lane, 0.2, static t => (0.8f * MathF.Pow(1 - t, 3), t < 0.2f ? 1f : 0f));
                break;
            // A kusudama bursts: a hard hit, then a fluttering rumble that fades out (the confetti).
            case GameplaySoundEvent.KusudamaPopped:
                shaped(lane, 0.7, static t => (1f * (1 - t) * (1 - t),
                    t < 0.08f ? 1f : 0.6f * (1 - t) * (0.6f + 0.4f * MathF.Sin(t * 70))));
                break;
        }
    }

    /// <summary>Each tick: the shaped rumbles move on.</summary>
    public void Update()
    {
        for (var index = _shaped.Count - 1; index >= 0; index--)
        {
            var (side, pads, start, seconds, shape) = _shaped[index];
            var t = (float)(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds / seconds);
            var (low, high) = t >= 1 ? (0f, 0f) : shape(t);
            // Held a little past the next tick, so a late tick doesn't leave a gap; as strong as it is now (hits
            // weaker than the pop don't break it up).
            shell.Application.Rumble(pads, low, high, TimeSpan.FromMilliseconds(t >= 1 ? 1 : 50));
            (_until[side], _strength[side]) = (Environment.TickCount64 + (t >= 1 ? 0 : 50), Math.Max(low, high));
            if (t >= 1)
                _shaped.RemoveAt(index);
        }
    }

    private void play(int? lane, (float Low, float High, int Ms) rumble)
    {
        var side = lane ?? shell.Credit.PlayerSide;
        if (pads(side) is not { Length: > 0 } pads1)
            return;
        var strength = Math.Max(rumble.Low, rumble.High);
        if (Environment.TickCount64 < _until[side] && strength < _strength[side])
            return;
        (_until[side], _strength[side]) = (Environment.TickCount64 + rumble.Ms, strength);
        shell.Application.Rumble(pads1, rumble.Low, rumble.High, TimeSpan.FromMilliseconds(rumble.Ms));
    }

    // The controllers to rumble for a side's player; none when vibration is off, in a replay, in autoplay or the attract's
    // demo play.
    private int[]? pads(int side)
    {
        if (shell.Arcade.ControllerVibration == Waddamburo.Game.Flow.ControllerVibration.Off || shell.Autoplay || gameplay.Reviewing
            || gameplay.Demo || (uint)side > 1)
            return null;
        return [.. Enumerable.Range(side * 4, 4).SelectMany(pad => Home.ControlBindings.Inputs(shell.Arcade.Controls[pad]))
            .Select(static token => SdlInput.TryParse(token, out var input) && input.Kind is SdlInputKind.PadButton or SdlInputKind.PadTrigger ? input.Device : 0)
            .Where(static device => device > 0).Distinct()];
    }

    private void shaped(int? lane, double seconds, Func<float, (float Low, float High)> shape)
    {
        var side = lane ?? shell.Credit.PlayerSide;
        if (pads(side) is { Length: > 0 } pads1)
            _shaped.Add((side, pads1, System.Diagnostics.Stopwatch.GetTimestamp(), seconds, shape));
    }
}
