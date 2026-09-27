using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Text;

/// <summary>Decodes a PNG (e.g. a player's avatar) to RGBA with SDL's own loader.</summary>
public static unsafe class SdlPng
{
    /// <summary>The image as straight RGBA rows, or null when the bytes are not a PNG SDL can read.</summary>
    public static RgbaTextSurface? Decode(ReadOnlySpan<byte> png)
    {
        fixed (byte* bytes = png)
        {
            var stream = SDL_IOFromConstMem((nint)bytes, (nuint)png.Length);
            if (stream == null)
                return null;
            var loaded = SDL_LoadPNG_IO(stream, true);
            if (loaded == null)
                return null;
            var rgba = SDL_ConvertSurface(loaded, SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888); // R,G,B,A bytes (little endian)
            SDL_DestroySurface(loaded);
            if (rgba == null)
                return null;
            try
            {
                int width = rgba->w, height = rgba->h, pitch = rgba->pitch;
                var pixels = new byte[width * height * 4];
                var source = (byte*)rgba->pixels;
                for (var row = 0; row < height; row++)
                    new ReadOnlySpan<byte>(source + row * pitch, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
                return new RgbaTextSurface((uint)width, (uint)height, pixels);
            }
            finally
            {
                SDL_DestroySurface(rgba);
            }
        }
    }
}
