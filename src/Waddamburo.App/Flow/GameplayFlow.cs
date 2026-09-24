using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// A song being played: the rainbow opens on it and the music starts; when chart and music are over,
/// the shutter closes (not in Waiwai) and the results load. Escape abandons the song.
/// </summary>
internal sealed class GameplayFlow(GameShell shell) : FlowScene(shell)
{
    private PlayableChart[] _charts = [];
    private SongSelectSong? _song;
    private int _side;
    private WaiwaiComposition? _waiwai;
    private AudioStreamTransport? _music;
    private GameplayTimeline? _timeline;
    private readonly Stopwatch _clock = new();
    private int _startTick;
    private int _shutterStartTick = -1;
    private bool _shutterClosing;

    /// <summary>The song select -> gameplay rainbow, begun by Song Select and finished here.</summary>
    public RainbowTransitionSequence Rainbow { get; private set; } = new();

    /// <summary>The song title shown in the rainbow.</summary>
    public LumenNativeSurfaceKey? RainbowTitle { get; set; }

    public void ResetRainbow() => Rainbow = new RainbowTransitionSequence();

    private bool revealed => Rainbow.State is RainbowTransitionState.Revealing or RainbowTransitionState.Complete;

    /// <summary>The next song's charts (one per player) and layout, for <see cref="Enter"/>.</summary>
    public void Prepare(PlayableChart[] charts, SongSelectSong song, int side, WaiwaiComposition? waiwai)
    {
        _charts = charts;
        _song = song;
        _side = side;
        _waiwai = waiwai;
    }

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Gameplay;

    public override void Enter(SceneId scene)
    {
        var request = Shell.PlayRequests.ActivatePending();
        var active = Shell.Active;
        // song_info's 720x64 title slot: fixed height, right-aligned, squeezed to fit.
        var songInfoIndex = active.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "song_info");
        if (songInfoIndex >= 0 && _song is not null)
        {
            var title = Shell.Titles.GetGameplayTitle(_song);
            _ = Shell.Titles.Resolve(title);
            active.Player.Layers[songInfoIndex].Player.SetNativeFill("song_name", title);
        }
        _timeline = new GameplayTimeline(_charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
        _startTick = Shell.Tick;
        _clock.Reset();
        Shell.Gameplay.Start(_charts, active, [.. request.Players.Select(player => player.Course)], _side, _waiwai);
        Console.WriteLine(
            $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
            + $"{_charts.Sum(static chart => chart.NoteCount)} notes at tick {Shell.Tick}.");
    }

    /// <summary>The chart position: held at zero under the rainbow, then the music's (or the ticks', headless).</summary>
    private TimeSpan chartTime() => _timeline?.ChartTime(
        !revealed ? TimeSpan.Zero
        : _music is not null ? Shell.Audio!.GetPosition(_music)
        : Shell.Headless ? TimeSpan.FromSeconds((Shell.Tick - _startTick) / 60d)
        : _clock.Elapsed) ?? TimeSpan.Zero;

    public override void Advance(LumenInputSnapshot input)
    {
        var animationFrames = Shell.Gameplay.AdvanceAnimations(chartTime(), input);
        Shell.DonRenderer?.Advance(animationFrames);
    }

    public override LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        Shell.Gameplay.CreateSnapshot(chartTime(), interpolation);

    // Live drum input is judged per display frame (headless, per tick).
    public override void UpdateFrame(SdlKeyboardSnapshot keys)
    {
        if (!Shell.Headless && revealed)
            Shell.Gameplay.Advance(keys, chartTime());
    }

    public override void Tick(FlowInput input)
    {
        var overlay = Shell.Overlay;
        if (overlay.Player is { } rainbow && Rainbow.ShouldStartReveal(Shell.Tick))
        {
            var request = Shell.PlayRequests.Active
                ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
            _startTick = Shell.Tick;
            _music = startAudio(request);
            _clock.Restart();
            rainbow.GotoLabel(RainbowTransitionComposition.RevealLabel, play: true);
            Rainbow.StartReveal();
            Console.WriteLine($"Rainbow reveal and gameplay started at tick {Shell.Tick}.");
            return;
        }
        if (overlay.Player is { } opening && Rainbow.FinishRevealWhenStopped(opening.IsPlaying))
        {
            overlay.Clear();
            Console.WriteLine($"Rainbow reveal completed at tick {Shell.Tick}.");
        }
        if (!revealed)
            return;
        var elapsed = chartTime();
        if (Shell.Headless)
            Shell.Gameplay.Advance(input.Keys, elapsed);
        if (_music?.Failure is not null || Shell.Audio?.Failure is not null)
            throw new IOException("Gameplay audio failed.", _music?.Failure ?? Shell.Audio?.Failure);
        var chartFinished = _charts.Length != 0
            && elapsed >= _charts.Max(static chart => chart.Duration) + TimeSpan.FromSeconds(1);
        var musicFinished = _music is not { } music || Shell.Audio is null || !Shell.Audio.Mixer.IsPlaying(music.Handle);
        if (!input.Escape && _shutterStartTick < 0 && chartFinished && musicFinished && !Shell.Gameplay.OverlayActive)
        {
            overlay.Clear();
            // Traced (5 Waiwai runs): Waiwai never closes the shutter; its results cut in.
            if (Shell.Gameplay.WaiwaiOutcome is null)
                overlay.Show(FlowScenes.Shutter);
            _shutterStartTick = Shell.Tick;
            _shutterClosing = false;
        }
        // Close registers on the shutter's first frame; retry until it exists.
        if (_shutterStartTick >= 0 && !_shutterClosing && overlay.Player is { } shutter)
            // Close(side): the shutter takes the player's colour (traced Close(1) for the right drum's
            // player, Close(2) for two players).
            _shutterClosing = shutter.TryInvokeCallback("Close",
                [LumenHostValue.FromNumber(Shell.Hosts.TwoPlayers ? 2 : Shell.Hosts.PlayerSide)]);
        // ponytail: 70 ticks = traced Close -> results load (1.17 s); the close itself takes ~1 s.
        if (input.Escape || _shutterStartTick >= 0 && Shell.Tick - _shutterStartTick >= 70)
            end(finished: !input.Escape);
    }

    // A finished song shows its results (the shutter stays over them until their first frames are
    // drawn); Escape goes straight back to Song Select.
    private void end(bool finished)
    {
        _shutterStartTick = -1;
        if (_music is { } music)
            Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
        _music = null;
        _clock.Reset();
        if (!finished)
            Shell.Overlay.Clear();
        ResetRainbow();
        Shell.ReportDiagnostics();
        Shell.Gameplay.ReportDiagnostics();
        Shell.Gameplay.Stop();
        Shell.PlayRequests.ClearActive();
        if (finished)
            Shell.Sounds?.Gameplay.Play(null, GameplaySoundEvent.SongFinished);
        Shell.Catalog.Replace(Shell.Gameplay.WaiwaiOutcome is not null ? FlowScenes.WaiwaiResults : FlowScenes.Results);
        _charts = [];
        Shell.Show(finished ? FlowScenes.Result : FlowScenes.SongSelect);
        Console.WriteLine($"Gameplay ended at tick {Shell.Tick}; showing {Shell.Active.Id}.");
    }

    private AudioStreamTransport? startAudio(PlayRequest request)
    {
        if (Shell.Audio is not { } audio || request.AudioAsset is not { } audioAsset)
            return null;
        var inputStream = Shell.Assets.OpenReadAsync(audioAsset).AsTask().GetAwaiter().GetResult();
        BufferedAudioSource? source = null;
        try
        {
            try
            {
                source = new BufferedAudioSource(inputStream, audio.Mixer.Format);
            }
            catch
            {
                inputStream.Dispose();
                throw;
            }
            source.Ready.GetAwaiter().GetResult();
            var playback = audio.PlayTransport(new ScheduledAudioSource(source,
                _timeline!.AudioStart, _timeline.LeadIn + _charts[0].Duration + TimeSpan.FromSeconds(1)));
            source = null;
            return playback;
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>What the run printed if it stopped mid-song.</summary>
    public void ReportFinal(SceneId active)
    {
        if (active != FlowScenes.Gameplay)
            return;
        Console.WriteLine($"Loaded gameplay charts: {_charts.Length}.");
        Shell.Gameplay.ReportDiagnostics();
    }
}
