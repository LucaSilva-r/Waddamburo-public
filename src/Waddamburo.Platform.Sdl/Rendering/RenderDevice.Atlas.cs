using Bgfx;

namespace Waddamburo.Platform.Sdl.Rendering;

// Texture atlases: small movie textures share 2048-pixel pages, so the quads that draw them batch into a few draws
// (Lumen movies give nearly every part its own texture: ~350 draws a frame in Song Select, one per quad, and a Switch's
// GPU paid for each). An atlased id keeps its own RenderTextureId; _textures maps it to its page and _atlased to its
// rectangle there, and submitQuads remaps its quads' UVs. Each texture sits in a 4-pixel gutter of its own edge texels,
// so linear filtering behaves as the old per-texture clamp did. Tiling textures (UVs past 0..1) are never atlased.
internal sealed unsafe partial class RenderDevice
{
    private const int Gutter = 4, MaxAtlasedSide = 512;

    // Block-compressed pages 4096 (16 MB as BC3: fewer pages, fewer page changes between quads), RGBA 2048 (16 MB).
    private static int pageSize(AtlasFormat format) => format == AtlasFormat.Rgba8 ? 2048 : 4096;

    // ponytail: shelf packing, a page's space comes back only once all its textures are released (then it is reused
    // whole); holes left meanwhile stay unused. Fine for scenes' textures, which come and go together.
    private sealed class AtlasPage(bgfx.TextureHandle handle, AtlasFormat format)
    {
        public bgfx.TextureHandle Handle { get; } = handle;
        public AtlasFormat Format { get; } = format;
        public int Live;
        public int ShelfY, ShelfHeight, X;
    }

    private enum AtlasFormat { Rgba8, Bc1, Bc3 }

    private readonly record struct AtlasEntry(AtlasPage Page, float U, float V, float Width, float Height);

    private readonly Dictionary<uint, AtlasEntry> _atlased = [];
    private readonly List<AtlasPage> _pages = [];

    // WADDAMBURO_NO_ATLAS=1: every texture on its own (comparisons; a GPU that misdraws the atlas).
    private static readonly bool s_noAtlas = Environment.GetEnvironmentVariable("WADDAMBURO_NO_ATLAS") == "1";

    private static bool fitsAtlas(uint width, uint height) => !s_noAtlas && width <= MaxAtlasedSide && height <= MaxAtlasedSide;

    // A rectangle of (width + 2 gutters) x (height + 2 gutters) on a page of the format, or null when none has room.
    private (AtlasPage Page, int X, int Y)? allocate(AtlasFormat format, int width, int height)
    {
        width += 2 * Gutter;
        height += 2 * Gutter;
        foreach (var page in _pages)
        {
            if (page.Format != format)
                continue;
            if (page.Live == 0)
                page.ShelfY = page.ShelfHeight = page.X = 0;
            var size = pageSize(format);
            if (page.X + width > size)
            {
                page.ShelfY += page.ShelfHeight;
                page.ShelfHeight = page.X = 0;
            }
            if (page.ShelfY + height > size)
                continue;
            var at = (page, page.X, page.ShelfY);
            page.X += width;
            page.ShelfHeight = Math.Max(page.ShelfHeight, height);
            page.Live++;
            return at;
        }
        var created = bgfx.create_texture_2d((ushort)pageSize(format), (ushort)pageSize(format), false, 1, format switch
        {
            AtlasFormat.Rgba8 => bgfx.TextureFormat.RGBA8,
            AtlasFormat.Bc1 => BgfxSupport.bgfxFormat(TextureBlockFormat.Bc1),
            _ => BgfxSupport.bgfxFormat(TextureBlockFormat.Bc3),
        }, 0, null, 0);
        if (!created.Valid)
            return null;
        var fresh = new AtlasPage(created, format) { X = width, ShelfHeight = height, Live = 1 };
        _pages.Add(fresh);
        return (fresh, 0, 0);
    }

    private RenderTextureId? tryAtlasRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        if (!fitsAtlas(width, height) || allocate(AtlasFormat.Rgba8, (int)width, (int)height) is not var (page, x, y))
            return null;
        int w = (int)width, h = (int)height, pw = w + 2 * Gutter, ph = h + 2 * Gutter;
        var padded = new byte[pw * ph * 4];
        for (var row = 0; row < ph; row++)
        {
            var source = Math.Clamp(row - Gutter, 0, h - 1) * w * 4;
            var target = row * pw * 4;
            var left = pixels.Slice(source, 4);
            var right = pixels.Slice(source + (w - 1) * 4, 4);
            for (var column = 0; column < Gutter; column++)
            {
                left.CopyTo(padded.AsSpan(target + column * 4));
                right.CopyTo(padded.AsSpan(target + (Gutter + w + column) * 4));
            }
            pixels.Slice(source, w * 4).CopyTo(padded.AsSpan(target + Gutter * 4));
        }
        bgfx.update_texture_2d(page.Handle, 0, 0, (ushort)x, (ushort)y, (ushort)pw, (ushort)ph, BgfxSupport.Copy(padded), ushort.MaxValue);
        return register(page, x, y, w, h);
    }

    private RenderTextureId? tryAtlasBlocks(TextureBlockFormat format, uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        if (format == TextureBlockFormat.Bc7 || !fitsAtlas(width, height)
            || allocate(format == TextureBlockFormat.Bc1 ? AtlasFormat.Bc1 : AtlasFormat.Bc3, (int)width, (int)height) is not var (page, x, y))
            return null;
        var blockBytes = format == TextureBlockFormat.Bc1 ? 8 : 16;
        int bw = (int)width / 4, bh = (int)height / 4, pbw = bw + 2, pbh = bh + 2; // the gutter is one block wide
        var padded = new byte[pbw * pbh * blockBytes];
        for (var row = 0; row < pbh; row++)
            for (var column = 0; column < pbw; column++)
            {
                var source = blocks.Slice((Math.Clamp(row - 1, 0, bh - 1) * bw + Math.Clamp(column - 1, 0, bw - 1)) * blockBytes, blockBytes);
                var target = padded.AsSpan((row * pbw + column) * blockBytes, blockBytes);
                source.CopyTo(target);
                // A gutter block repeats its neighbour's edge texels: column 0 or 3, row 0 or 3, the corner's one texel.
                var edgeColumn = column == 0 ? 0 : column == pbw - 1 ? 3 : -1;
                var edgeRow = row == 0 ? 0 : row == pbh - 1 ? 3 : -1;
                if (edgeColumn >= 0 || edgeRow >= 0)
                    BlockEdges.Replicate(target, format == TextureBlockFormat.Bc3, edgeColumn, edgeRow);
            }
        bgfx.update_texture_2d(page.Handle, 0, 0, (ushort)x, (ushort)y, (ushort)(pbw * 4), (ushort)(pbh * 4), BgfxSupport.Copy(padded), ushort.MaxValue);
        return register(page, x, y, (int)width, (int)height);
    }

    private RenderTextureId register(AtlasPage page, int x, int y, int width, int height)
    {
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, page.Handle);
        var size = (float)pageSize(page.Format);
        _atlased.Add(id.Value, new AtlasEntry(page, (x + Gutter) / size, (y + Gutter) / size, width / size, height / size));
        return id;
    }

    // An atlased texture's space back to its page (the page stays, for the next scene's).
    private static void releaseAtlased(AtlasEntry entry) => entry.Page.Live--;

    // The rectangle to remap a quad's UVs into, or null (a standalone texture, or one drawn as its replacement).
    private AtlasEntry? atlasRect(uint id) =>
        _atlased.TryGetValue(id, out var entry) && !(ShowReplacements && _replacements.ContainsKey(id)) ? entry : null;

    /// <summary>Real GPU textures: standalone ones, atlas pages, replacements (what a Switch's buffer limit counts).</summary>
    private int gpuTextureCount => _textures.Count - _atlased.Count + _pages.Count + _replacements.Count;

    private void disposeAtlas()
    {
        foreach (var page in _pages)
            bgfx.destroy_texture(page.Handle);
        _pages.Clear();
        _atlased.Clear();
    }
}

/// <summary>BC1/BC3 blocks rewritten to repeat one column, one row or one texel (an atlas gutter).</summary>
public static class BlockEdges
{
    /// <param name="block">8 bytes (BC1) or 16 (BC3: 8 alpha, then 8 colour).</param>
    /// <param name="bc3">A BC3 block (alpha indices rewritten too).</param>
    /// <param name="column">0..3 to repeat that column across the block, or -1.</param>
    /// <param name="row">0..3 to repeat that row down the block, or -1.</param>
    public static void Replicate(Span<byte> block, bool bc3, int column, int row)
    {
        if (bc3)
        {
            // Alpha: two endpoints, then 16 three-bit indices (48 bits, little-endian).
            ulong alpha = 0;
            for (var i = 0; i < 6; i++)
                alpha |= (ulong)block[2 + i] << (8 * i);
            ulong remapped = 0;
            for (var texel = 0; texel < 16; texel++)
                remapped |= ((alpha >> (3 * source(texel, column, row))) & 7) << (3 * texel);
            for (var i = 0; i < 6; i++)
                block[2 + i] = (byte)(remapped >> (8 * i));
            block = block[8..];
        }
        // Colour: two endpoints, then 16 two-bit indices.
        var indices = (uint)(block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
        uint result = 0;
        for (var texel = 0; texel < 16; texel++)
            result |= ((indices >> (2 * source(texel, column, row))) & 3) << (2 * texel);
        block[4] = (byte)result;
        block[5] = (byte)(result >> 8);
        block[6] = (byte)(result >> 16);
        block[7] = (byte)(result >> 24);
    }

    private static int source(int texel, int column, int row) =>
        (row >= 0 ? row : texel / 4) * 4 + (column >= 0 ? column : texel % 4);
}
