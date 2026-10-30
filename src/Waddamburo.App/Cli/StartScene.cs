

namespace Waddamburo.App.Cli;

internal enum StartScene
{
    Boot,
    Attract,
    Entry,
    SongSelect,
    DaniSelect,
    ResultFail,
    ResultClear,
    Retry,
    GameOver,
    WaiwaiResult,
}

internal static class StartSceneParser
{
    public static StartScene Parse(string? value) => value switch
    {
        null or "boot" => StartScene.Boot,
        "attract" => StartScene.Attract,
        "entry" => StartScene.Entry,
        "song-select" => StartScene.SongSelect,
        "dani-select" => StartScene.DaniSelect,
        "result-fail" => StartScene.ResultFail,
        "result-clear" => StartScene.ResultClear,
        "retry" => StartScene.Retry,
        "gameover" => StartScene.GameOver,
        "waiwai-result" => StartScene.WaiwaiResult,
        _ => throw new ArgumentException(
            "--start-scene must be boot, attract, entry, song-select, dani-select, result-fail, result-clear, retry, gameover, or waiwai-result."),
    };
}
