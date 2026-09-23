internal enum StartScene
{
    Entry,
    SongSelect,
    ResultFail,
    ResultClear,
    Retry,
    GameOver,
}

internal static class StartSceneParser
{
    public static StartScene Parse(string? value) => value switch
    {
        null or "entry" => StartScene.Entry,
        "song-select" => StartScene.SongSelect,
        "result-fail" => StartScene.ResultFail,
        "result-clear" => StartScene.ResultClear,
        "retry" => StartScene.Retry,
        "gameover" => StartScene.GameOver,
        _ => throw new ArgumentException(
            "--start-scene must be entry, song-select, result-fail, result-clear, retry, or gameover."),
    };
}
