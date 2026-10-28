using System.Collections.Concurrent;
using System.Security.Cryptography;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>Tiny shared colour/coverage fields; geometry is drawn by LumenVectorGeometry.</summary>
internal static class VectorUiPaint
{
    private static readonly ConcurrentDictionary<LumenNativeSurfaceKey, RgbaTextSurface> Paints = new();

    public static LumenNativeSurfaceKey Gradient(byte[] rgba, bool vertical = false)
    {
        var key = new LumenNativeSurfaceKey("vector-ui:paint:" + Convert.ToHexString(SHA256.HashData(rgba)) + (vertical ? ":v" : ":h"));
        Paints.GetOrAdd(key, _ => new(vertical ? 1U : 256U, vertical ? 256U : 1U, rgba));
        return key;
    }

    public static LumenNativeSurfaceKey Solid(LumenRenderColor colour)
    {
        var pixels = new byte[1024];
        for (var i = 0; i < 256; i++)
        {
            pixels[i * 4] = channel(colour.Red); pixels[i * 4 + 1] = channel(colour.Green);
            pixels[i * 4 + 2] = channel(colour.Blue); pixels[i * 4 + 3] = 255;
        }
        return Gradient(pixels);
    }

    public static LumenRenderColor Colour(LumenNativeSurfaceKey key, float position = .08f)
    {
        var surface = Resolve(key); var at = Math.Clamp((int)(position * 255), 0, 255) * 4;
        return new(surface.Pixels[at] / 255f, surface.Pixels[at + 1] / 255f, surface.Pixels[at + 2] / 255f, 1);
    }

    public static RgbaTextSurface Resolve(LumenNativeSurfaceKey key) => Paints.GetOrAdd(key, static surface =>
    {
        if (surface == LumenVectorGeometry.White) return new(1, 1, [255, 255, 255, 255]);
        if (surface == LumenVectorGeometry.Coverage) return new(2, 1, [255, 255, 255, 0, 255, 255, 255, 255]);
        if (surface == LumenWavePattern.Stroke) return new(8, 1,
            [255,255,255,0, 255,255,255,255, 255,255,255,255, 255,255,255,255,
             255,255,255,255, 255,255,255,255, 255,255,255,255, 255,255,255,0]);
        if (surface != LumenVectorGeometry.Shadow) throw new KeyNotFoundException($"Unknown vector paint {surface}.");
        var pixels = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var dx = Math.Abs(x + .5f - 32) - 20; var dy = Math.Abs(y + .5f - 32) - 20;
            var distance = MathF.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0)) + Math.Min(Math.Max(dx, dy), 0);
            var at = (y * 64 + x) * 4;
            pixels[at] = pixels[at + 1] = pixels[at + 2] = 255;
            pixels[at + 3] = channel(.40f / (1 + MathF.Exp(distance / 2.2f)));
        }
        return new(64, 64, pixels);
    });

    private static byte channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
}
