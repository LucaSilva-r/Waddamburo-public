

namespace Waddamburo.App.Cli;

internal enum StartScene
{
    Boot,
    Attract,
    Entry,
    SongSelect,
    AiSongSelect,
    DaniSelect,
    ResultFail,
    ResultAlmost,
    ResultClear,
    DaniResultFail,
    DaniResultClear,
    DaniGameOver,
    RewardGameOver,
    RewardShop,
    RewardShop2P,
    Retry,
    GameOver,
    WaiwaiResult,
    AiRemind,
    WaiwaiTutorial,
    AiTutorial,
}

internal static class StartSceneParser
{
    public static StartScene Parse(string? value) => value switch
    {
        null or "boot" => StartScene.Boot,
        "attract" => StartScene.Attract,
        "entry" => StartScene.Entry,
        "song-select" => StartScene.SongSelect,
        "ai-song-select" => StartScene.AiSongSelect,
        "dani-select" => StartScene.DaniSelect,
        "result-fail" => StartScene.ResultFail,
        "result-almost" => StartScene.ResultAlmost,
        "result-clear" => StartScene.ResultClear,
        "dani-result-fail" => StartScene.DaniResultFail,
        "dani-result-clear" => StartScene.DaniResultClear,
        "dani-gameover" => StartScene.DaniGameOver,
        "reward-gameover" => StartScene.RewardGameOver,
        "reward-shop" => StartScene.RewardShop,
        "reward-shop-2p" => StartScene.RewardShop2P,
        "retry" => StartScene.Retry,
        "gameover" => StartScene.GameOver,
        "waiwai-result" => StartScene.WaiwaiResult,
        "ai-remind" => StartScene.AiRemind,
        "waiwai-tutorial" => StartScene.WaiwaiTutorial,
        "ai-tutorial" => StartScene.AiTutorial,
        _ => throw new ArgumentException(
            "--start-scene must be boot, attract, entry, song-select, ai-song-select, dani-select, result-fail, result-almost, dani-result-fail, dani-result-clear, dani-gameover, reward-gameover, result-clear, retry, gameover, waiwai-result, or ai-remind."),
    };
}
