using Waddamburo.Formats.Lmb;
using Waddamburo.Skins.Green.Gameplay;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Skins.Green.Tests;

public sealed class TaikoSongInfoPresentationTests
{
    [Fact]
    public void HomeHidesOnlyTheStageEvenWhenTheMovieRestoresItsAlpha()
    {
        var player = songInfo();
        var title = new LumenNativeSurfaceKey("synthetic-song-title");
        TaikoSongInfoPresentation.SetTitle(player, title);
        Assert.Equal(3, player.CreateRenderSnapshot().Quads.Length);

        Assert.True(player.InstanceFrame("main")!.Value.Playing);
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage: false, showInfo: true);
        Assert.Equal(2, player.CreateRenderSnapshot().Quads.Length);
        Assert.Contains(player.CreateRenderSnapshot().Quads, quad => quad.NativeSurface == title);
        Assert.False(player.InstanceFrame("main")!.Value.Playing); // held still: no fade cycle without the number
        for (var frame = 0; frame < 120; frame++)
        {
            player.Advance();
            Assert.True(player.TrySetInstanceAlpha("main/stage", 1)); // authored fade-in / script
            TaikoSongInfoPresentation.UpdateVisibility(player, showStage: false, showInfo: true);
            Assert.Equal(2, player.CreateRenderSnapshot().Quads.Length);
        }
        // The arcade HUD, a course's stage, or a replay label can show the slot again.
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage: true, showInfo: true);
        Assert.Equal(3, player.CreateRenderSnapshot().Quads.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BattleResultHidesTitleGenreAndStageTogether(bool showStage)
    {
        var player = songInfo();
        var title = new LumenNativeSurfaceKey("synthetic-ai-title");
        TaikoSongInfoPresentation.SetTitle(player, title);
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage, showInfo: true);
        Assert.Contains(player.CreateRenderSnapshot().Quads, quad => quad.NativeSurface == title);
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage, showInfo: false);
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        player.Advance();
        player.TrySetInstanceAlpha("main", 1);
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage, showInfo: false);
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        // A new play can use the metadata again without losing its title binding.
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage, showInfo: true);
        Assert.Contains(player.CreateRenderSnapshot().Quads, quad => quad.NativeSurface == title);
    }

    [Fact]
    public void RecreatedMovieClipsReceiveTheCurrentVisibilityPolicy()
    {
        var player = songInfo();
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage: false, showInfo: false);
        player.GotoFrame(0, play: false);
        TaikoSongInfoPresentation.UpdateVisibility(player, showStage: false, showInfo: false);
        Assert.Empty(player.CreateRenderSnapshot().Quads);
    }

    // Synthetic metadata slots shared by the normal and AI movies; no supplied asset content.
    private static LumenPlayer songInfo()
    {
        LmbPlaceObjectCommand place(uint id, uint depth, uint name) =>
            new(id, depth, name, 1, 0, (ushort)depth, 0, 0, 0, uint.MaxValue, uint.MaxValue, [], null!);
        LmbSpriteDefinition sprite(uint id, params LmbTimelineCommand[] children) => new(id, 0, 1, 0, 0, [],
            [new LmbShowFrameCommand(0, (uint)children.Length, [], null!), .. children], null!);
        var shape = new LmbShapeDefinition(2, 1, [], [new LmbShapeGeometry(
            [new(0, 0, 0, 0), new(10, 0, 1, 0), new(10, 10, 1, 1), new(0, 10, 0, 1)], 0, 0, [], null!)], null!);
        var movie = new LmbMovieDefinition(null!, null,
            [new(0, "", 0), new(1, "main", 0), new(2, "stage", 0), new(3, "genre", 0), new(4, "song_name", 0)],
            [], [new(1, 0, 0, 1, 0, 0)], [], [], [], [], [shape],
            [sprite(1, place(3, 1, 1)), sprite(3, place(4, 1, 2), place(5, 2, 3), place(6, 3, 4)),
             sprite(4, place(2, 1, 0)), sprite(5, place(2, 1, 0)), sprite(6, place(2, 1, 0))], []);
        var player = new LumenPlayer(movie, 1280, 720, rootCharacterId: 1);
        player.SetNativeFill("stage", new LumenNativeSurfaceKey("synthetic-stage"));
        player.SetNativeFill("genre", new LumenNativeSurfaceKey("synthetic-genre"));
        return player;
    }
}
