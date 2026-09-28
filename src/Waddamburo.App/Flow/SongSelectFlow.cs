using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select (normal or Waiwai): the mode-switch folder, and a chosen song's handoff: the rainbow
/// intermission covers the screen (song title in it), the charts load under it, gameplay starts.
/// </summary>
internal sealed class SongSelectFlow(GameShell shell, GameplayFlow gameplay) : FlowScene(shell)
{
    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.SongSelect;

    public override void Enter(SceneId scene) => Shell.Previews?.StartBackground(Shell.Hosts.Waiwai);

    // The gameplay layout picked (skin included) when the difficulty selector opened, its movies
    // decoding in the background; the song's start takes it when the players match.
    private sealed record Prefetched(SongKey Song, int Side, bool TwoPlayers, bool Waiwai,
        GameplaySceneComposition.ThemedSkin? Theme, SceneDefinition Scene);
    private Prefetched? _prefetch;

    public override void Tick(FlowInput input)
    {
        if (Shell.Hosts.SongSelect?.CourseSelectSong is { } picked
            && _prefetch?.Song != picked.Descriptor.Key && Shell.JoinedSides.Count > 0)
        {
            var twoPlayers = Shell.JoinedSides.Count == 2;
            var side = twoPlayers ? 0 : Shell.JoinedSides.Min;
            // Waiwai's fixed layout (its charts only reshape the notes).
            var waiwai = Shell.Hosts.Waiwai && twoPlayers && picked.Descriptor.WaiwaiComposition is not null;
            var (category, _) = find(picked.Descriptor.Key);
            var theme = waiwai ? null : Shell.Skins.Resolve(picked.Descriptor, category);
            var scene = waiwai ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
                : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side, twoPlayers);
            Shell.Prefetch(scene);
            _prefetch = new(picked.Descriptor.Key, side, twoPlayers, waiwai, theme, scene);
            Console.WriteLine($"Prefetching gameplay for '{picked.Descriptor.Key}'.");
        }
        // The mode-switch folder: reload as the other song select (normal <-> Waiwai).
        if (Shell.Hosts.SongSelect?.ModeSwitchRequested == true)
        {
            Shell.Hosts.Waiwai = !Shell.Hosts.Waiwai;
            Shell.Catalog.Replace(FlowScenes.SongSelectScene([.. Shell.JoinedSides], Shell.Hosts.Waiwai));
            Shell.Show(FlowScenes.SongSelect);
            return;
        }
        if (switchLibrary())
            return;
        if (Shell.PlayRequests.Pending is not { } request)
            return;
        var rainbow = gameplay.Rainbow;
        if (rainbow.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete)
        {
            Shell.ReportDiagnostics();
            gameplay.RainbowTitle = Shell.Titles.GetTransitionTitle(find(request.Song).Song);
            _ = Shell.Titles.Resolve(gameplay.RainbowTitle.Value);
            rainbow.Begin(Shell.Tick);
            Console.WriteLine($"Queued rainbow cover for '{request.Song}' at tick {Shell.Tick}.");
        }
        if (rainbow.ShouldStartCover(Shell.Tick))
        {
            var player = Shell.Overlay.Show(FlowScenes.Rainbow);
            player.SetNativeFill(
                RainbowTransitionComposition.SongTitleFill,
                gameplay.RainbowTitle ?? throw new InvalidOperationException("Rainbow transition has no song title."));
            player.GotoLabel(RainbowTransitionComposition.CoverLabel, play: true);
            rainbow.StartCover();
            Shell.Audio?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
            Shell.Audio?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(20));
            Console.WriteLine($"Rainbow cover started at tick {Shell.Tick}.");
        }
        if (Shell.Overlay.Player is { } cover && rainbow.FinishCoverWhenStopped(cover.IsPlaying, Shell.Tick))
            startSong(request);
    }

    // A library folder (home mode): the plain rainbow covers the screen, Song Select reloads listing
    // that library under it (its music plays on), then the rainbow opens.
    private SongSourceKind? _switching;
    private bool _switched;

    private bool switchLibrary()
    {
        if (_switching is null)
        {
            if (Shell.Hosts.SongSelect?.LibraryRequested is not { } library)
                return false;
            _switching = library;
            _switched = false;
            Shell.Overlay.Show(FlowScenes.Rainbow).GotoLabel(RainbowTransitionComposition.PlainCoverLabel, play: true);
            return true;
        }
        if (Shell.Overlay.Player is { IsPlaying: true })
            return true;
        if (!_switched)
        {
            Shell.Hosts.Library = _switching.Value;
            Shell.Catalog.Replace(FlowScenes.SongSelectScene([.. Shell.JoinedSides], Shell.Hosts.Waiwai));
            Shell.Show(FlowScenes.SongSelect);
            Shell.Overlay.Player?.GotoLabel(RainbowTransitionComposition.PlainRevealLabel, play: true);
            _switched = true;
            return true;
        }
        Shell.Overlay.Clear();
        _switching = null;
        return true;
    }

    private (string Category, SongSelectSong Song) find(SongKey song) => Shell.SongCatalog.Categories
        .SelectMany(category => category.Songs.Select(entry => (category.Name, entry)))
        .First(entry => entry.entry.Descriptor.Key == song);

    // Under the closed rainbow: load every player's chart and the song's scene, then show gameplay.
    private void startSong(PlayRequest request)
    {
        PlayableChart[] charts;
        try
        {
            charts = [.. request.Players.Select(player => Shell.Assets.LoadChartAsync(player.Chart, player.ChartAsset)
                .AsTask().GetAwaiter().GetResult())];
        }
        catch (Exception exception) when (exception is InvalidDataException
            or NotSupportedException or IOException or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine($"Cannot play selected chart: {exception.Message}");
            Shell.PlayRequests.CancelPending();
            Shell.Overlay.Clear();
            gameplay.ResetRainbow();
            Shell.Show(FlowScenes.SongSelect);
            return;
        }
        var (category, song) = find(request.Song);
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        var twoPlayers = request.Players.Length == 2;
        // Waiwai: both players on the song's Waiwai layout (its duet charts reshaped by section).
        WaiwaiComposition? waiwai = null;
        if (Shell.Hosts.Waiwai && charts.Length == 2 && song.Descriptor.WaiwaiComposition is { } compositionAsset)
        {
            using var compositionStream = Shell.Assets.OpenReadAsync(compositionAsset).AsTask().GetAwaiter().GetResult();
            waiwai = WaiwaiComposition.Parse(compositionStream);
            // A song gets its rare (heart) note by chance (traced 1 of 4 runs, and 2 of 2 earlier).
            // ponytail: waiwaicollabo/00_taiko.xml prob_rare_0 = 16 read as a percent per song.
            var (left, right) = waiwai.Apply(charts[0], charts[1],
                rareNote: Random.Shared.Next(100) < 16
                    || Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RARE") == "1"); // diagnostic: force it
            charts = [left, right];
        }
        var prefetched = _prefetch is { } ready && ready.Song == request.Song && ready.Side == side
            && ready.TwoPlayers == twoPlayers && ready.Waiwai == waiwai is not null ? ready : null;
        _prefetch = null;
        // Every song gets its themed skin or a fresh random original mix.
        var theme = prefetched is not null ? prefetched.Theme : waiwai is not null ? null
            : Shell.Skins.Resolve(song.Descriptor, category);
        if (Shell.Sounds?.Gameplay is { } sounds)
        {
            sounds.Waiwai = waiwai is not null;
            sounds.PrepareDrums(request.Players.Length == 2);
        }
        // The credit's song number (reset when a credit ends); a home session's endless credit stays on 1
        // (the counters only have art for songs 1-4).
        var stage = Shell.Arcade.Home ? 1 : ++Shell.SongsPlayed;
        Shell.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), stage,
            Final: !Shell.Arcade.Home && stage >= Shell.Arcade.SongsPerSession);
        Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
        Shell.Catalog.Replace(prefetched?.Scene ?? (waiwai is not null
            ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side,
                twoPlayers: twoPlayers)));
        gameplay.Prepare(charts, song, side, waiwai);
        Shell.Show(FlowScenes.Gameplay);
    }
}
