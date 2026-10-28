using Waddamburo.App.Presentation;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.App.Tests;

public sealed class SongBrowserFrameTests
{
    [Theory]
    [InlineData(350, 171, 506, 234)]
    [InlineData(580, 420, 65, 234)]
    public void ConfirmationStaysInsideTheCurrentCategoryIncludingItsOpeningMorph(float x, float y, float width, float height)
    {
        var pulse = Assert.Single(SongBrowserFramePresentation.CategoryConfirmation(new(x, y, width, height), .8f));
        Assert.Equal(new(x + 4, y + 4, width - 8, height - 8), LumenNativeFrame.Bounds(pulse));
        Assert.Equal(.28f, pulse.MultiplyColor.Alpha, 4);
        Assert.Empty(SongBrowserFramePresentation.CategoryConfirmation(new(x, y, width, height), 0));
        Assert.Empty(SongBrowserFramePresentation.CategoryConfirmation(new(x, y, 4, height), 1));
    }
}
