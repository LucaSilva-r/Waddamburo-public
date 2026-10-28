using Waddamburo.Platform.Sdl;

namespace Waddamburo.Architecture.Tests;

public sealed class MenuWheelInputTests
{
    [Theory]
    [InlineData(0, SdlKeyboardKey.WheelUp, 'A')]
    [InlineData(0, SdlKeyboardKey.WheelDown, 'S')]
    [InlineData(1, SdlKeyboardKey.WheelUp, 'D')]
    [InlineData(1, SdlKeyboardKey.WheelDown, 'F')]
    public void WheelDrivesTheJoinedDrumAndRetainsTheNotchForFastScrolling(int side, SdlKeyboardKey wheel, char rim)
    {
        var input = LumenInputAdapter.CreateSnapshot(new([wheel]), primarySide: side);
        Assert.Equal(2, input.PressedKeyCodes.Count);
        Assert.Contains((int)wheel, input.PressedKeyCodes);
        Assert.Contains((int)rim, input.PressedKeyCodes);
    }

    [Fact]
    public void MenuWheelNeverReachesGameplayMovies()
    {
        var input = LumenInputAdapter.CreateSnapshot(new([SdlKeyboardKey.WheelDown]),
            LumenInputMode.PresentationOnly, primarySide: 1);
        Assert.Empty(input.PressedKeyCodes);
    }
}
