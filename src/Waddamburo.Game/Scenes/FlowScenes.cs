using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

/// <summary>The front end's scene ids, shared by the frontends (their compositions are each frontend's: GreenScenes, WaddamburoScreens).</summary>
public static class FlowScenes
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
    public static readonly SceneId RewardShop = new("reward-shop");

    /// <summary>The drum a front-end name board is for, by where these scenes put it (0 left, 1 right); null for the
    /// entry's centre board (a read card's player) and a board waiting hidden.</summary>
    public static int? BoardSide(SceneLayerDefinition board) => board.Transform.Y == 638
        ? board.Transform.X < 640 ? 0 : 1 : null;
}
