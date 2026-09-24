using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

/// <summary>The front end's scene ids and their traced compositions.</summary>
internal static class FlowScenes
{
    public static readonly SceneId Boot = new("boot");
    public static readonly SceneId Logo = new("attract-logo");
    public static readonly SceneId Title = new("attract-title");
    public static readonly SceneId Caution = new("attract-caution");
    public static readonly SceneId Movie = new("attract-movie");
    public static readonly SceneId Entry = new("entry");
    public static readonly SceneId SongSelect = new("song-select");
    public static readonly SceneId Gameplay = new("gameplay");
    public static readonly SceneId Result = new("result");
    public static readonly SceneId Retry = new("retry");
    public static readonly SceneId GameOver = new("gameover");

    public static SceneDefinition Rainbow { get; } = RainbowTransitionComposition.Create(new SceneId("rainbow-transition"));

    // Traced 1P results: the results movie and its "press to continue" overlay, full screen.
    public static SceneDefinition Results { get; } = new(SceneDefinition.CurrentVersion, Result, [
        new SceneLayerDefinition("enso_result/packeddata.ddp", "result/result.lm", LumenMatrix.Identity, "result"),
        new SceneLayerDefinition("waitinput/packeddata.ddp", "waitinput/waitinput.lm", LumenMatrix.Identity, "waitinput"),
    ]);

    // Waiwai's results (traced): its movie full screen, the two name boards at (-580|308, 220) centred.
    public static SceneDefinition WaiwaiResults { get; } = new(SceneDefinition.CurrentVersion, Result, [
        new SceneLayerDefinition("waiwai_result/packeddata.ddp", "waiwai_result/waiwai_result.lm", LumenMatrix.Identity,
            "waiwai-result"),
        new SceneLayerDefinition("indicator/packeddata.ddp", "player_name/player_name.lm",
            LumenMatrix.Identity with { X = 60, Y = 580 }, "waiwai-result-board-0"),
        new SceneLayerDefinition("indicator/packeddata.ddp", "player_name/player_name.lm",
            LumenMatrix.Identity with { X = 948, Y = 580 }, "waiwai-result-board-1"),
    ]);

    // Traced: a finished song closes the intermission shutter (Close(0)), then the results load
    // under it; result.lm opens on the same closed-shutter art, so the shutter is just dropped.
    public static SceneDefinition Shutter { get; } = new(SceneDefinition.CurrentVersion, new SceneId("shutter"), [
        new SceneLayerDefinition("intermission/packeddata.ddp", "shutter/shutter.lm", LumenMatrix.Identity,
            RainbowTransitionComposition.StaticHostId),
    ]);

    // Traced: leaving the results or the revival (except results -> revival, which opens on its own
    // shutter) plays the intermission fade's "in" (1 s to black), swaps scenes under it, then
    // resets the fade to "wait" (transparent).
    public static SceneDefinition Fade { get; } = new(SceneDefinition.CurrentVersion, new SceneId("fade"), [
        new SceneLayerDefinition("intermission/packeddata.ddp", "scene_change_fade/scene_change_fade.lm",
            LumenMatrix.Identity, RainbowTransitionComposition.StaticHostId),
    ]);

    /// <summary>Every scene as first loaded (song select, gameplay and results are replaced per play).</summary>
    public static IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults) =>
    [
        // Traced boot and attract loop: kidou (notice, logos) once, then logo_namco -> title ->
        // keikoku -> attract CM -> logo_namco ..., each full screen at depth 1000.
        attract(Boot, "kidou", "boot"),
        attract(Logo, "logo_namco"),
        attract(Title, "title"),
        attract(Caution, "keikoku"),
        attract(Movie, "movie", "boot"),
        new SceneDefinition(SceneDefinition.CurrentVersion, Entry, [
            // Traced entry composition: the scene movie, then its indicator parts by depth
            // (100 .. 50; the game draws them over the scene). entry_info / shop_info (-890)
            // start hidden and are left out until something shows them.
            new SceneLayerDefinition("entry/packeddata.ddp", "entry/entry.lm", LumenMatrix.Identity, "player-entry"),
            indicatorPart("indicator"),
            indicatorPart("player_name", 640, 360),
            indicatorPart("player_name", 640, 360),
            indicatorPart("time_counter"),
            indicatorPart("over_msg"),
        ]),
        SongSelectScene([0]),
        .. gameplayAndResults,
        // Traced end of a credit: the revival drum roll after a failed first song, then game over.
        new SceneDefinition(SceneDefinition.CurrentVersion, Retry, [
            new SceneLayerDefinition("enso_result/packeddata.ddp", "retry_game/retry_game.lm", LumenMatrix.Identity, "retry"),
        ]),
        new SceneDefinition(SceneDefinition.CurrentVersion, GameOver, [
            new SceneLayerDefinition("reward_shop/packeddata.ddp", "shop_gameover/shop_gameover.lm", LumenMatrix.Identity, "gameover"),
        ]),
    ];

    // Traced final name-board positions are (-580, 278) for the left drum's player and (308, 278) for
    // the right one's in the cabinet's centered coordinates, or (60, 638) / (948, 638) in our top-left
    // scene coordinates (session8, session9). Board n shows the player on sides[n].
    public static SceneDefinition SongSelectScene(IReadOnlyList<int> sides, bool waiwai = false)
    {
        var movie = waiwai ? "waiwai_song_select" : "song_select";
        SceneLayerDefinition board(int index) => index < sides.Count
            ? indicatorPart("player_name", sides[index] == 1 ? 948 : 60, 638)
            : indicatorPart("player_name", 640, 360);
        return new(SceneDefinition.CurrentVersion, SongSelect, [
            new SceneLayerDefinition($"{movie}/packeddata.ddp", $"{movie}/{movie}.lm", LumenMatrix.Identity, "song-select"),
            indicatorPart("indicator"),
            board(0),
            board(1),
            indicatorPart("time_counter"),
        ]);
    }

    private static SceneDefinition attract(SceneId id, string movie, string host = "attract") =>
        new(SceneDefinition.CurrentVersion, id, [
            new SceneLayerDefinition($"attract/{movie}/packeddata.ddp", $"{movie}/{movie}.lm", LumenMatrix.Identity, host),
        ]);

    private static SceneLayerDefinition indicatorPart(string movie, float x = 0, float y = 0) => new(
        "indicator/packeddata.ddp",
        $"{movie}/{movie}.lm",
        LumenMatrix.Identity with { X = x, Y = y },
        IndicatorParts.HostId);
}
