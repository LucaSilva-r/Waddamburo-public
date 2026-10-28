using Waddamburo.App.Flow;
using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Tests;

public sealed class ChartCarouselControlsTests
{
    [Theory]
    [InlineData(0, SdlKeyboardKey.F, SdlKeyboardKey.V, SdlKeyboardKey.C)]
    [InlineData(1, SdlKeyboardKey.C, SdlKeyboardKey.K, SdlKeyboardKey.F)]
    public void NativeSettingsDoNotBlockTheOtherDrumsChart(int utilitySide, SdlKeyboardKey utilityDon,
        SdlKeyboardKey otherRight, SdlKeyboardKey otherDon)
    {
        var choices = carousel();
        choices.EnterUtilities(utilitySide);
        var other = 1 - utilitySide;
        var remaining = route(new([utilityDon, otherRight, otherDon]), choices);
        Assert.Equal(4, choices.Selected(other));
        Assert.True(choices.IsConfirmed(other));
        Assert.False(choices.IsConfirmed(utilitySide));
        Assert.Equal(utilityDon, Assert.Single(remaining.PressedKeys));
    }

    [Fact]
    public void EnteringSettingsAndMovingTheOtherDrumInTheSameTickBothWork()
    {
        var choices = carousel();
        var remaining = route(new([SdlKeyboardKey.D, SdlKeyboardKey.V]), choices);
        Assert.True(choices.IsUtility(0));
        Assert.Equal(4, choices.Selected(1));
        Assert.Equal(SdlKeyboardKey.D, Assert.Single(remaining.PressedKeys));
    }

    [Fact]
    public void BothDrumsCanEnterAndConfirmNativeSettingsInTheSameTicks()
    {
        var choices = carousel();
        var entering = route(new([SdlKeyboardKey.D, SdlKeyboardKey.Z]), choices);
        Assert.All(choices.Sides, side => Assert.True(choices.IsUtility(side)));
        Assert.Equal(2, entering.PressedKeys.Length);
        Assert.Contains(SdlKeyboardKey.D, entering.PressedKeys);
        Assert.Contains(SdlKeyboardKey.Z, entering.PressedKeys);

        var deciding = route(new([SdlKeyboardKey.F, SdlKeyboardKey.C]), choices);
        Assert.Equal(2, deciding.PressedKeys.Length);
        Assert.Contains(SdlKeyboardKey.F, deciding.PressedKeys);
        Assert.Contains(SdlKeyboardKey.C, deciding.PressedKeys);
        Assert.All(choices.Sides, side => Assert.False(choices.IsConfirmed(side)));
    }

    [Fact]
    public void EscapeUndoesAChartEvenWhileTheOtherDrumIsInSettings()
    {
        var choices = carousel();
        choices.EnterUtilities(0);
        choices.Confirm(1);
        var cancelled = false;
        ChartCarouselControls.Route(new([SdlKeyboardKey.Escape]), choices, _ => false,
            () => cancelled = true, () => choices.UndoConfirm(1), choices.Confirm);
        Assert.False(cancelled);
        Assert.False(choices.IsConfirmed(1));
        Assert.True(choices.IsUtility(0));
    }

    private static SdlKeyboardSnapshot route(SdlKeyboardSnapshot keys, SongChartCarousel choices) =>
        ChartCarouselControls.Route(keys, choices, _ => false,
            () => throw new InvalidOperationException("Unexpected cancel"),
            () => throw new InvalidOperationException("Unexpected undo"), choices.Confirm);

    private static SongChartCarousel carousel()
    {
        var key = new SongKey(SongSourceKind.Tja, "controls");
        return new(new SongDescriptor(key, new SongTitle("Controls"), null,
            Enumerable.Range(0, 5).Select(index => new SongChartDescriptor(new ChartKey(key, $"oni-{index}"),
                $"Oni {index}", new CatalogAssetKey(new("synthetic"), $"chart-{index}"), TaikoCourse.Oni, 9))), [0, 1]);
    }
}
