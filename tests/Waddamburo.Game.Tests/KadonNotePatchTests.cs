using Waddamburo.Game.Patching;

namespace Waddamburo.Game.Tests;

public sealed class KadonNotePatchTests
{
    [Fact]
    public void BlueTurnsPurpleWhileWhiteBlackAndRedStay()
    {
        byte[] pixels = [70, 190, 220, 255, 255, 255, 255, 255, 0, 0, 0, 255, 240, 60, 40, 255];
        KadonNotePatch.Recolour(pixels);
        // Ka's blue: red and blue up, green down (purple).
        Assert.True(pixels[0] > 150 && pixels[1] < 100 && pixels[2] > 180, $"{pixels[0]},{pixels[1]},{pixels[2]}");
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255, 240, 60, 40, 255], pixels[4..]);
    }
}
