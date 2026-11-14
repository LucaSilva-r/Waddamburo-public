using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Tests;

public sealed class SongSelectScrollTests
{
    [Theory]
    [InlineData(1001, 'A')]
    [InlineData(1002, 'S')]
    public void DifficultyFolderReceivesOneRimInputPerWheelNotch(int wheel, char rim)
    {
        var (movie, scroll) = menu(panelVisible: true);
        var hits = 0;
        for (var tick = 0; tick < 40; tick++)
        {
            var input = tick == 0 ? new LumenInputSnapshot([wheel, rim]) : LumenInputSnapshot.Empty;
            var delivered = scroll.Before(input, listActive: true);
            if (delivered.IsDown(rim)) hits++;
            movie.Advance(delivered);
            scroll.After();
        }
        Assert.Equal(1, hits);
    }

    [Fact]
    public void OpeningDifficultyFolderDropsStepsQueuedForTheSongList()
    {
        var (movie, scroll) = menu(panelVisible: false);
        Assert.True(scroll.Before(new LumenInputSnapshot([1002, 'S']), listActive: true).IsDown('S'));
        scroll.After(); // list has not consumed the step yet
        movie.SetInstanceHidden("main/courseStar_", false);
        Assert.Empty(scroll.Before(LumenInputSnapshot.Empty, listActive: true).PressedKeyCodes);
        scroll.After();
        movie.SetInstanceHidden("main/courseStar_", true);
        Assert.Empty(scroll.Before(LumenInputSnapshot.Empty, listActive: true).PressedKeyCodes);
    }

    [Fact]
    public void HiddenDifficultyFolderDoesNotDisableTheSongListQueue()
    {
        var (_, scroll) = menu(panelVisible: false);
        Assert.True(scroll.Before(new LumenInputSnapshot([1002, 'S']), listActive: true).IsDown('S'));
        scroll.After();
        // The song list still retries a queued step while its animation prevents acknowledgement.
        Assert.True(scroll.Before(LumenInputSnapshot.Empty, listActive: true).IsDown('S'));
    }

    [Fact]
    public void AVisiblePanelOwnsInputDuringItsFadeIn()
    {
        var (movie, scroll) = menu(panelVisible: true);
        movie.TrySetInstanceAlpha("main/courseStar_", 0);
        Assert.True(movie.IsInstanceVisible("main/courseStar_"));
        Assert.True(scroll.Before(new LumenInputSnapshot([1002, 'S']), listActive: true).IsDown('S'));
        scroll.After();
        Assert.Empty(scroll.Before(LumenInputSnapshot.Empty, listActive: true).PressedKeyCodes);
    }

    [Fact]
    public void InstanceVisibilityIncludesAncestorsAndMissingClips()
    {
        var (movie, _) = menu(panelVisible: true);
        Assert.True(movie.IsInstanceVisible("main/courseStar_"));
        movie.SetInstanceHidden("main", true);
        Assert.False(movie.IsInstanceVisible("main/courseStar_"));
        movie.SetInstanceHidden("main", false);
        Assert.True(movie.IsInstanceVisible("main/courseStar_"));
        movie.SetInstanceHidden("main/courseStar_", true);
        Assert.False(movie.IsInstanceVisible("main/courseStar_"));
        Assert.False(movie.IsInstanceVisible("main/missing"));
    }

    private static (LumenPlayer Movie, SongSelectScroll Scroll) menu(bool panelVisible)
    {
        LmbPlaceObjectCommand place(uint id, ushort depth, uint name) =>
            new(id, depth, name, 1, 0, depth, 0, 0, 0, uint.MaxValue, uint.MaxValue, [], null!);
        LmbSpriteDefinition sprite(uint id, params LmbTimelineCommand[] children) => new(id, 0, 1, 0, 0, [],
            [new LmbShowFrameCommand(0, (uint)children.Length, [], null!), .. children], null!);
        var list = new LmbSpriteDefinition(3, 2, 3, 2, 0, [],
            [new LmbShowFrameCommand(0, 0, [], null!),
             new LmbFrameLabelCommand(4, 1, 0, [], null!), new LmbShowFrameCommand(1, 0, [], null!),
             new LmbFrameLabelCommand(5, 2, 0, [], null!), new LmbShowFrameCommand(2, 0, [], null!)], null!);
        var definition = new LmbMovieDefinition(null!, null,
            [new(0, "", 0), new(1, "main", 0), new(2, "genreSelect_", 0), new(3, "courseStar_", 0),
             new(4, "カーソル右", 0), new(5, "カーソル左", 0)], [], [new(1, 0, 0, 1, 0, 0)], [], [], [], [], [],
            [sprite(1, place(2, 1, 1)), sprite(2, place(3, 1, 2), place(4, 2, 3)), list, sprite(4)], []);
        var movie = new LumenPlayer(definition, 1280, 720, rootCharacterId: 1);
        movie.GotoFrame(0, play: false);
        movie.TryGotoFrame("main/genreSelect_", 0, play: false);
        movie.SetInstanceHidden("main/courseStar_", !panelVisible);
        return (movie, new SongSelectScroll(movie, static () => -1, static _ => { }));
    }
}
