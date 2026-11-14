using Waddamburo.Game.Flow;
using Waddamburo.Skins.Green;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Scenes;

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
    /// <summary>The audio calibration's own gameplay lane.</summary>
    public static readonly SceneId Calibration = new("calibration");
    /// <summary>The benchmark's stress song, on a lane of its own.</summary>
    public static readonly SceneId Benchmark = new("benchmark");
    public static readonly SceneId Result = new("result");
    public static readonly SceneId Retry = new("retry");
    public static readonly SceneId GameOver = new("gameover");
    public static readonly SceneId AiRemind = new("ai-remind");
    public static readonly SceneId Tutorial = new("tutorial");
    public static readonly SceneId WaiwaiTutorial = new("waiwai-tutorial");
    public static readonly SceneId AiTutorial = new("ai-tutorial");
    public static readonly SceneId DaniSelect = new("dani-select");

    // Traced session30: the game plays this as Song Select ends and sends it out once dani_select has loaded.
    public static SceneDefinition DanCover { get; } = new(SceneDefinition.CurrentVersion, new SceneId("dan-cover"), [
        new SceneLayerDefinition("intermission/packeddata.ddp", "scene_change_dani1/scene_change_dani1.lm",
            LumenMatrix.Identity, RainbowTransitionComposition.StaticHostId),
    ]);

    public static SceneDefinition DanExitCover { get; } = new(SceneDefinition.CurrentVersion, new SceneId("dan-exit-cover"), [
        new SceneLayerDefinition("intermission/packeddata.ddp", "scene_change_dani2/scene_change_dani2.lm",
            LumenMatrix.Identity, RainbowTransitionComposition.StaticHostId),
    ]);

    public static SceneDefinition Rainbow { get; } = RainbowTransitionComposition.Create(new SceneId("rainbow-transition"));

    // Traced 1P results: the results movie and its "press to continue" overlay, full screen.
    public static SceneDefinition Results { get; } = new(SceneDefinition.CurrentVersion, Result, [
        new SceneLayerDefinition("enso_result/packeddata.ddp", "result/result.lm", LumenMatrix.Identity, "result"),
        new SceneLayerDefinition("waitinput/packeddata.ddp", "waitinput/waitinput.lm", LumenMatrix.Identity, "waitinput"),
    ]);

    // AI battle's results (traced session-ai-battle-3): the backdrop, the normal results, then the section results over them.
    public static SceneDefinition AiResults { get; } = new(SceneDefinition.CurrentVersion, Result, [
        new SceneLayerDefinition("ghost/result/packeddata.ddp", "result_bg/result_bg.lm", LumenMatrix.Identity, "ai-result-bg"),
        new SceneLayerDefinition("ghost/result/packeddata.ddp", "result_normal/result_normal.lm", LumenMatrix.Identity, "ai-result"),
        new SceneLayerDefinition("ghost/result/packeddata.ddp", "result_section/result_section.lm", LumenMatrix.Identity, "ai-result-section"),
    ]);

    // A dan course's results (traced sessions 30 and 31), and the shutter closing over its last song.
    public static SceneDefinition DaniResults { get; } = new(SceneDefinition.CurrentVersion, Result, [
        new SceneLayerDefinition("dani_result/packeddata.ddp", "dani_result/dani_result.lm", LumenMatrix.Identity, "dani-result"),
    ]);

    public static SceneDefinition DanShutter { get; } = new(SceneDefinition.CurrentVersion, new SceneId("dan-shutter"), [
        new SceneLayerDefinition("intermission/packeddata.ddp", "dani_shutter/dani_shutter.lm",
            LumenMatrix.Identity, RainbowTransitionComposition.StaticHostId),
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
            // Three boards (traced session13-card): a read card's name centre-top, (-136, -175)
            // centred, then the left and right drums' boards it moves to (-580 / 308, 278).
            indicatorPart("player_name", 504, 185),
            indicatorPart("player_name", 60, 638),
            indicatorPart("player_name", 948, 638),
            indicatorPart("time_counter"),
            indicatorPart("over_msg"),
        ]),
        SongSelectScene([0]),
        .. gameplayAndResults,
        // Traced end of a credit: the revival drum roll after a failed first song, then game over.
        new SceneDefinition(SceneDefinition.CurrentVersion, Retry, [
            new SceneLayerDefinition("enso_result/packeddata.ddp", "retry_game/retry_game.lm", LumenMatrix.Identity, "retry"),
        ]),
        // Traced: the how-to-play folder cuts straight to the tutorial movie, full screen.
        new SceneDefinition(SceneDefinition.CurrentVersion, Tutorial, [
            new SceneLayerDefinition("tutorial/packeddata.ddp", "tutorial/tutorial.lm", LumenMatrix.Identity, "tutorial"),
        ]),
        new SceneDefinition(SceneDefinition.CurrentVersion, WaiwaiTutorial, [
            new SceneLayerDefinition("waiwai_tutorial/packeddata.ddp", "waiwai_tutorial/waiwai_tutorial.lm", LumenMatrix.Identity, "waiwai-tutorial"),
        ]),
        new SceneDefinition(SceneDefinition.CurrentVersion, AiTutorial, [
            new SceneLayerDefinition("ghost/common_bg/packeddata.ddp", "common_bg/common_bg.lm", LumenMatrix.Identity, "ai-remind-bg"),
            new SceneLayerDefinition("ghost/tutorial/packeddata.ddp", "tutorial/tutorial.lm", LumenMatrix.Identity, "ai-tutorial"),
            new SceneLayerDefinition("indicator/packeddata.ddp", "time_counter/time_counter.lm", LumenMatrix.Identity, "ai-tutorial-timer"),
        ]),
        DaniSelectScene(0),
        NormalGameOver,
        // AI battle's farewell as a credit ends (traced session-ai-battle-3: remind, and its backdrop common_bg under it).
        new SceneDefinition(SceneDefinition.CurrentVersion, AiRemind, [
            new SceneLayerDefinition("ghost/common_bg/packeddata.ddp", "common_bg/common_bg.lm", LumenMatrix.Identity, "ai-remind-bg"),
            new SceneLayerDefinition("ghost/remind/packeddata.ddp", "remind/remind.lm", LumenMatrix.Identity, "ai-remind"),
        ]),
    ];

    /// <summary>
    /// Traced sessions 30-32: dani_select full screen at depth 0, the indicator's parts above it, among them one name
    /// board (the player's, on their drum's side as in Song Select).
    /// </summary>
    public static SceneDefinition DaniSelectScene(int side) => new(SceneDefinition.CurrentVersion, DaniSelect, [
        new SceneLayerDefinition("dani_select/packeddata.ddp", "dani_select/dani_select.lm", LumenMatrix.Identity, "dani-select"),
        new SceneLayerDefinition("indicator/packeddata.ddp", "player_name/player_name.lm",
            LumenMatrix.Identity with { X = side == 1 ? 948 : 60, Y = 638 }, side == 1 ? "dani-board-1" : "dani-board-0"),
    ]);

    public static SceneDefinition NormalGameOver { get; } =
        new SceneDefinition(SceneDefinition.CurrentVersion, GameOver, [
            new SceneLayerDefinition("reward_shop/packeddata.ddp", "shop_gameover/shop_gameover.lm", LumenMatrix.Identity, "gameover"),
        ]);

    // A dan course's end (traced sessions 30 to 32): the same calls and sounds as the normal one, its own movie.
    public static SceneDefinition DaniGameOver { get; } =
        new SceneDefinition(SceneDefinition.CurrentVersion, GameOver, [
            new SceneLayerDefinition("dani_result/packeddata.ddp", "dani_gameover/dani_gameover.lm", LumenMatrix.Identity, "gameover"),
        ]);


    // Traced final name-board positions are (-580, 278) for the left drum's player and (308, 278) for
    // the right one's in the cabinet's centered coordinates, or (60, 638) / (948, 638) in our top-left
    // scene coordinates (session8, session9). Board n shows the player on sides[n].
    public static SceneDefinition SongSelectScene(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false)
    {
        var movie = waiwai ? "waiwai_song_select" : "song_select";
        // The second board waits on the other drum's side (hidden), so an online partner's name shows there without a reload.
        SceneLayerDefinition board(int index) => index < sides.Count
            ? indicatorPart("player_name", sides[index] == 1 ? 948 : 60, 638)
            : index == 1 && sides.Count == 1 ? indicatorPart("player_name", sides[0] == 1 ? 60 : 948, 638)
            : indicatorPart("player_name", 640, 360);
        // AI battle (traced session-ai-battle-1): its own song select, then its rank, rank-up and tension panels
        // (depth 70) under the indicator's parts.
        if (aiBattle)
            return new(SceneDefinition.CurrentVersion, SongSelect, [
                new SceneLayerDefinition("ghost/song_select/packeddata.ddp", "ghost_song_select/ghost_song_select.lm",
                    LumenMatrix.Identity, "song-select"),
                aiPanel("rank_panel", "ai_rank_panel"),
                aiPanel("rankup_todo", "ai_rankup_todo"),
                aiPanel("tension", "tension"),
                indicatorPart("indicator"),
                board(0),
                board(1),
                indicatorPart("time_counter"),
            ]);
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

    // An AI battle panel: set up with the indicator parts (IndicatorParts), from its ghost/ archive.
    private static SceneLayerDefinition aiPanel(string archive, string movie) =>
        new($"ghost/{archive}/packeddata.ddp", $"{movie}/{movie}.lm", LumenMatrix.Identity, IndicatorParts.HostId);

    private static SceneLayerDefinition indicatorPart(string movie, float x = 0, float y = 0) => new(
        "indicator/packeddata.ddp",
        $"{movie}/{movie}.lm",
        LumenMatrix.Identity with { X = x, Y = y },
        IndicatorParts.HostId);
}
