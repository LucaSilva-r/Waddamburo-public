using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

/// <summary>
/// Song Select (normal or Waiwai): the mode-switch folder, and a chosen song's handoff: the rainbow
/// intermission covers the screen (song title in it), the charts load under it, gameplay starts.
/// </summary>
internal sealed class SongSelectFlow(GameShell shell, GameplayFlow gameplay) : FlowScene(shell)
{
    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.SongSelect;

    public override void Enter(SceneId scene) => Shell.Previews?.StartBackground(Shell.Hosts.Waiwai);

    public override void Tick(FlowInput input)
    {
        // The mode-switch folder: reload as the other song select (normal <-> Waiwai).
        if (Shell.Hosts.SongSelect?.ModeSwitchRequested == true)
        {
            Shell.Hosts.Waiwai = !Shell.Hosts.Waiwai;
            Shell.Catalog.Replace(FlowScenes.SongSelectScene([.. Shell.JoinedSides], Shell.Hosts.Waiwai));
            Shell.Show(FlowScenes.SongSelect);
            return;
        }
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
        // Every song gets its themed skin or a fresh random original mix.
        var theme = Shell.Skins.Resolve(song.Descriptor, category);
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
        if (Shell.Sounds is not null) Shell.Sounds.Waiwai = waiwai is not null;
        Shell.Sounds?.PrepareGameplayDrums(request.Players.Length == 2);
        // ponytail: the stage counts every song since launch (no credits yet); 8 stage frames.
        Shell.SongInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(category), Math.Min(++Shell.SongsPlayed, 8));
        Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
        var side = request.Players[0].Player == LocalPlayerSlot.PlayerTwo ? 1 : 0;
        Shell.Catalog.Replace(waiwai is not null
            ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, Shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, Shell.EnsoLayout, theme, side,
                twoPlayers: request.Players.Length == 2));
        gameplay.Prepare(charts, song, side, waiwai);
        Shell.Show(FlowScenes.Gameplay);
    }
}
