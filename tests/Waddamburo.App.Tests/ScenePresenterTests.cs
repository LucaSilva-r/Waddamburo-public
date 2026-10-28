using Waddamburo.App.Presentation;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Tests;

public sealed class ScenePresenterTests
{
    [Fact]
    public void ResultsKeepSharedNameBoardsWhenSongSelectPrefetchEvictsTheCache()
    {
        RenderTextureId board = new(1001), gameplay = new(1002), result = new(1003);
        RenderTextureId[] held = [board, board, gameplay];
        RenderTextureId[] active = [result, board, board];

        // The prefetch removed the name boards from the cache before the held frame retires.
        Assert.Equal([gameplay], ScenePresenter.RetiredFrameTextures(held, active, []));
        // Once results also release them, each shared board is freed only once.
        Assert.Equal([result, board], ScenePresenter.RetiredFrameTextures(active, [], []));
    }

    [Fact]
    public void RetiringAFramePreservesCachedMoviesAndReleasesUnusedAliasesOnce()
    {
        RenderTextureId cached = new(1), unused = new(2);
        Assert.Equal([unused], ScenePresenter.RetiredFrameTextures([cached, unused, unused], [], [cached]));
    }
}
