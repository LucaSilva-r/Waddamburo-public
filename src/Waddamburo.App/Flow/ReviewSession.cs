using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// A reviewed play on the gameplay scene (home: the autoplay, or a song's instant replay). Its inputs
/// are baked once into a <see cref="ReviewTimeline"/> per lane; the keys move a <see cref="ReviewClock"/>;
/// each frame the lanes show the bake at the playhead and the music follows it at its pace.
/// </summary>
/// <remarks>
/// Times: the clock runs in chart time (what the lanes draw); judgements are baked in judgement time, the
/// chart time less the input offset the play was made with, so they show where the live game showed them.
/// </remarks>
internal sealed class ReviewSession : IDisposable
{
    private readonly GameShell _shell;
    private readonly PlayableChart[] _charts;
    private readonly TaikoCourse[] _courses;
    private readonly int _side;
    private readonly WaiwaiComposition? _waiwai;
    private readonly TimeSpan _inputOffset;
    private readonly TimeSpan _songStart;
    private readonly Stopwatch _frame = new();
    private readonly Task<ScrubAudioSource>? _decode;
    private ScrubAudioSource? _audio;
    private AudioPlaybackHandle? _voice;
    private TimeSpan _shownAt;
    private bool _jumped = true;

    /// <remarks>
    /// <paramref name="inputs"/>: each lane's drum hits, in judgement time. <paramref name="end"/>: how far the
    /// review may go (the chart's end, or where an instant replay was paused). <paramref name="songStart"/>: the
    /// chart time the song's own audio starts at (its authored offset, less the audio offset).
    /// </remarks>
    public ReviewSession(GameShell shell, PlayRequest request, PlayableChart[] charts, int side, WaiwaiComposition? waiwai,
        IReadOnlyList<TaikoReplayInput>[] inputs, TimeSpan leadIn, TimeSpan end, TimeSpan inputOffset, TimeSpan songStart)
    {
        _shell = shell;
        _charts = charts;
        _courses = [.. request.Players.Select(static player => player.Course)];
        _side = side;
        _waiwai = waiwai;
        _inputOffset = inputOffset;
        _songStart = songStart;
        Clock = new ReviewClock(-leadIn, end);
        Clock.Sought += (_, _) => _jumped = true;
        _shownAt = Clock.Start;

        Timelines = bake(inputs, null, null);

        // The whole song in memory for the scrubbing music, decoded meanwhile (silent until it is ready).
        if (shell.Audio is { } audio && request.AudioAsset is { } asset)
        {
            var stream = shell.Assets.OpenReadAsync(asset).AsTask().GetAwaiter().GetResult();
            var format = audio.Mixer.Format;
            _decode = Task.Run(() => ScrubAudioSource.Decode(stream, format));
        }
    }

    public ReviewClock Clock { get; }

    /// <summary>The baked play of each lane (the lanes are started on them).</summary>
    public ReviewTimeline[] Timelines { get; private set; }

    // from/until: a training attempt's stretch, in judgement time (nothing outside it is judged).
    private ReviewTimeline[] bake(IReadOnlyList<TaikoReplayInput>[] inputs, TimeSpan? from, TimeSpan? until)
    {
        var watch = Stopwatch.StartNew();
        ReviewTimeline[] timelines = [.. _charts.Select((chart, lane) => ReviewTimeline.Build(chart, _courses[lane],
            TaikoGameplayPresentation.WindowsFor(chart, _courses[lane]), TaikoGameplayPresentation.StrongSecondHitWindow, inputs[lane], from, until))];
        Console.WriteLine($"Review: baked {timelines.Sum(static timeline => timeline.Events.Length)} events in {watch.Elapsed.TotalMilliseconds:F1} ms.");
        return timelines;
    }

    /// <summary>
    /// Shows a training attempt played over [<paramref name="from"/>, <paramref name="until"/>) (chart time)
    /// with these <paramref name="inputs"/> (judgement time), the playhead at its end.
    /// </summary>
    public void ShowAttempt(IReadOnlyList<TaikoReplayInput>[] inputs, TimeSpan from, TimeSpan until)
    {
        Timelines = bake(inputs, from - _inputOffset, until - _inputOffset);
        Clock.Seek(until);
        ShowFresh(until);
    }

    /// <summary>The lanes play live from <paramref name="from"/> (a training attempt): fresh, judging the drum from there.</summary>
    public void StartLive(TimeSpan from)
    {
        var gameplay = _shell.Gameplay;
        gameplay.CloseLongNotes();
        gameplay.Start(_charts, _shell.Active, _courses, _side, _waiwai);
        gameplay.ResetCharacters();
        gameplay.StartAt(from - _inputOffset);
        Clock.Seek(from);
        _shownAt = from;
        _jumped = false;
    }

    /// <summary>
    /// Shows the lanes afresh at <paramref name="time"/>: the open roll/balloon overlays close (they belong
    /// to the scene), the lanes start over on the bake, Don drops any balloon pose, and the baked state shows.
    /// </summary>
    public void ShowFresh(TimeSpan time)
    {
        var gameplay = _shell.Gameplay;
        gameplay.CloseLongNotes();
        gameplay.Start(_charts, _shell.Active, _courses, _side, _waiwai, Timelines);
        gameplay.ResetCharacters();
        gameplay.Review(time - _inputOffset, play: false);
        _shownAt = time;
        _jumped = false;
    }

    /// <summary>
    /// One display frame. Space pauses, up/down change the speed, the wheel seeks 1 s a notch, held
    /// left/right scrub (<paramref name="keysEnabled"/> false: a menu has them). Playing on, the judgements
    /// passed show as they happened; any other move jumps to the baked state, going back from fresh lanes.
    /// </summary>
    public void Frame(SdlKeyboardSnapshot keys, bool keysEnabled)
    {
        var scrub = 0;
        if (keysEnabled)
        {
            foreach (var press in keys.Presses)
                switch (press.Key)
                {
                    case SdlKeyboardKey.Space or SdlKeyboardKey.Enter: Clock.TogglePause(); break; // Enter: a Tatacon's A
                    case SdlKeyboardKey.WheelUp: Clock.SeekBy(TimeSpan.FromSeconds(-1)); break;
                    case SdlKeyboardKey.WheelDown: Clock.SeekBy(TimeSpan.FromSeconds(1)); break;
                    case SdlKeyboardKey.Up: Clock.Faster(); break;
                    case SdlKeyboardKey.Down: Clock.Slower(); break;
                }
            scrub = (keys.IsDown(SdlKeyboardKey.Right) ? 1 : 0) - (keys.IsDown(SdlKeyboardKey.Left) ? 1 : 0);
        }
        Step(scrub);
    }

    /// <summary>
    /// Moves the clock on by the frame (<paramref name="scrub"/>: a held seek key's direction) and shows the
    /// lanes there: the bake, or, given <paramref name="live"/> (a training attempt), the drum judged live.
    /// </summary>
    public void Step(int scrub, SdlKeyboardSnapshot? live = null)
    {
        Clock.Update(_frame.Elapsed, scrub);
        _frame.Restart();
        var time = Clock.Position;
        // ponytail: a hit's time within the frame is taken in real time, not scaled by a slowed speed (a few ms).
        if (live is { } keys)
            _shell.Gameplay.Advance(keys, time - _inputOffset);
        else if (time < _shownAt)
            ShowFresh(time);
        else
            _shell.Gameplay.Review(time - _inputOffset, play: !_jumped && !Clock.Scrubbing);
        _jumped = false;
        _shownAt = time;
        followMusic(Clock.Rate);
    }

    /// <summary>The flow is paused (its menu is open): the music stands still and the frame clock restarts after.</summary>
    public void Hold()
    {
        _frame.Restart();
        followMusic(0);
    }

    private void followMusic(double rate)
    {
        if (_audio is null && _decode is { IsCompletedSuccessfully: true } decoded && _shell.Audio is { } audio)
        {
            _audio = decoded.Result;
            _voice = audio.Mixer.PlayStream(_audio, AudioBus.Bgm);
        }
        _audio?.Follow(Clock.Position - _songStart, rate);
    }

    public void Dispose()
    {
        if (_voice is { } voice)
            _shell.Audio?.Mixer.Stop(voice, TimeSpan.FromMilliseconds(20));
        _voice = null;
        _audio = null;
    }
}
