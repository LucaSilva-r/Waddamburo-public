namespace Waddamburo.App.Presentation;

/// <summary>
/// A background video's see-through parts, cut on its decoding thread: black bars (rows and columns black in every
/// frame so far: osu!taiko videos leave the top black for osu!'s playfield) and a keyed colour (a green screen).
/// </summary>
internal sealed class VideoMask(int width, int height, int? keyColor = null, int keyTolerance = 60, float? lumaCurve = null)
{
    // The luma key's opacity for each brightness (the brightest channel), and the factor that divides the colour back.
    private readonly (byte Alpha, float Scale)[]? _luma = lumaCurve is { } curve ? [.. Enumerable.Range(0, 256).Select(level =>
    {
        var alpha = 1 - MathF.Pow(1 - level / 255f, curve);
        return ((byte)MathF.Round(alpha * 255), alpha <= 0 ? 0f : 1 / alpha);
    })] : null;

    // Brighter than this (r + g + b) is picture: compressed black stays well under it.
    private const int BlackSum = 48;

    // Rows and columns that have shown picture: from then on they are never a bar again.
    private readonly bool[] _rowSeen = new bool[height];
    private readonly bool[] _columnSeen = new bool[width];
    private bool _allSeen;
    private int _frames;

    /// <summary>Makes the frame's bars and keyed colour transparent (RGBA8, in place).</summary>
    public void Apply(Span<byte> rgba)
    {
        if (!_allSeen)
            cutBars(rgba, scan: _frames++ % 8 == 0);
        if (keyColor is { } key)
            cutKey(rgba, key);
        if (_luma is { } luma)
            lumaKey(rgba, luma);
    }

    // Brightness is opacity; the colour is divided by it, so over black the frame looks as it was.
    private static void lumaKey(Span<byte> rgba, (byte Alpha, float Scale)[] luma)
    {
        for (var at = 0; at < rgba.Length; at += 4)
        {
            int r = rgba[at], g = rgba[at + 1], b = rgba[at + 2];
            var (alpha, scale) = luma[Math.Max(r, Math.Max(g, b))];
            if (alpha == 255)
                continue;
            rgba[at] = (byte)Math.Min(255, r * scale);
            rgba[at + 1] = (byte)Math.Min(255, g * scale);
            rgba[at + 2] = (byte)Math.Min(255, b * scale);
            rgba[at + 3] = (byte)(rgba[at + 3] * alpha / 255);
        }
    }

    // Looking for picture in the bars every 8th frame (a full pass); cutting them every frame.
    private void cutBars(Span<byte> rgba, bool scan)
    {
        for (var y = 0; y < height && scan; y++)
        {
            var row = rgba.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                if (_rowSeen[y] && _columnSeen[x])
                    continue;
                if (row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2] > BlackSum)
                    _rowSeen[y] = _columnSeen[x] = true;
            }
        }
        _allSeen = Array.TrueForAll(_rowSeen, static seen => seen) && Array.TrueForAll(_columnSeen, static seen => seen);
        for (var y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * width * 4, width * 4);
            if (!_rowSeen[y])
            {
                row.Clear();
                continue;
            }
            for (var x = 0; x < width; x++)
                if (!_columnSeen[x])
                    row.Slice(x * 4, 4).Clear();
        }
    }

    // The key's matte, worked out on a quarter-size frame (4x4 blocks, their average colour): a block within the tolerance
    // is background where it joins the frame's edge through such blocks. Dark parts of the figure that touch the
    // background through it (black sleeves on a black backdrop) keep their highlights in the average, and a closing
    // (grow the figure two blocks, shrink it back) fills the holes left; a 3x3 average softens the edge. Full size, the
    // matte is scaled up smoothly, and pixels at the key's own colour in its soft edge go clear.
    private const int Block = 4;
    private readonly int _blocksWide = (width + Block - 1) / Block, _blocksHigh = (height + Block - 1) / Block;
    private int[]? _blockDistance, _queue;
    private bool[]? _figure, _grown;
    private float[]? _matte, _soft;
    private bool[]? _flooded;
    private (int Left, int Right, float Between)[]? _columns;

    private void cutKey(Span<byte> rgba, int key)
    {
        int kr = key >> 16 & 0xFF, kg = key >> 8 & 0xFF, kb = key & 0xFF, bw = _blocksWide, bh = _blocksHigh, blocks = bw * bh;
        var distance = _blockDistance ??= new int[blocks];
        var queue = _queue ??= new int[blocks];
        var figure = _figure ??= new bool[blocks];
        var grown = _grown ??= new bool[blocks];
        var matte = _matte ??= new float[blocks];
        var soft = _soft ??= new float[blocks];
        // Block averages' squared distance to the key: each block's pixels summed in place.
        for (var by = 0; by < bh; by++)
        {
            int y0 = by * Block, y1 = Math.Min(height, y0 + Block);
            for (var bx = 0; bx < bw; bx++)
            {
                // Every other pixel of every other row: an average all the same, at a quarter of the reads.
                int x0 = bx * Block, x1 = Math.Min(width, x0 + Block), r = 0, g = 0, b = 0, count = 0;
                for (var y = y0; y < y1; y += 2)
                {
                    var row = rgba.Slice((y * width + x0) * 4, (x1 - x0) * 4);
                    for (var at = 0; at < row.Length; at += 8, count++)
                        (r, g, b) = (r + row[at], g + row[at + 1], b + row[at + 2]);
                }
                int dr = r / count - kr, dg = g / count - kg, db = b / count - kb;
                distance[by * bw + bx] = dr * dr + dg * dg + db * db;
            }
        }
        // Flood the background in from the edge.
        var near = keyTolerance * keyTolerance;
        Array.Fill(figure, true);
        int head = 0, tail = 0;
        void seed(int block)
        {
            if (figure[block] && distance[block] <= near)
                (figure[block], queue[tail++]) = (false, block);
        }
        for (var bx = 0; bx < bw; bx++)
        {
            seed(bx);
            seed((bh - 1) * bw + bx);
        }
        for (var by = 0; by < bh; by++)
        {
            seed(by * bw);
            seed(by * bw + bw - 1);
        }
        while (head < tail)
        {
            var block = queue[head++];
            int bx = block % bw, by = block / bw;
            if (bx > 0) seed(block - 1);
            if (bx < bw - 1) seed(block + 1);
            if (by > 0) seed(block - bw);
            if (by < bh - 1) seed(block + bw);
        }
        // Closing: grow twice, shrink twice (3x3 each time, as a row pass then a column pass). What it filled is
        // remembered: the key's own colour there is still background (a gap between hair and legs), the rest is not.
        var flooded = _flooded ??= new bool[blocks];
        Array.Copy(figure, flooded, blocks);
        for (var step = 0; step < 4; step++)
        {
            var grow = step < 2;
            for (var pass = 0; pass < 2; pass++)
            {
                int along = pass == 0 ? 1 : bw;
                for (var by = 0; by < bh; by++)
                    for (var bx = 0; bx < bw; bx++)
                    {
                        var block = by * bw + bx;
                        bool before = (pass == 0 ? bx > 0 : by > 0) ? figure[block - along] : figure[block];
                        bool after = (pass == 0 ? bx < bw - 1 : by < bh - 1) ? figure[block + along] : figure[block];
                        grown[block] = grow ? figure[block] | before | after : figure[block] & before & after;
                    }
                Array.Copy(grown, figure, blocks);
            }
        }
        for (var block = 0; block < blocks; block++)
            matte[block] = figure[block] ? 1 : 0;
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                float sum = 0, count = 0;
                for (var ny = Math.Max(0, by - 1); ny <= Math.Min(bh - 1, by + 1); ny++)
                    for (var nx = Math.Max(0, bx - 1); nx <= Math.Min(bw - 1, bx + 1); nx++, count++)
                        sum += matte[ny * bw + nx];
                soft[by * bw + bx] = sum / count;
            }
        // Full size: the matte scaled up (bilinear between block centres). A block whose neighbourhood is all figure is
        // left alone, all background cleared; only the edge's blocks are worked pixel by pixel.
        var clear = near / 16;
        // Each column's two block columns and its place between them, worked out once.
        var columns = _columns ??= [.. Enumerable.Range(0, width).Select(x =>
        {
            var fx = Math.Clamp((x + 0.5f) / Block - 0.5f, 0, bw - 1);
            return ((int)fx, Math.Min(bw - 1, (int)fx + 1), fx - (int)fx);
        })];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                float lowest = 1, highest = 0;
                for (var ny = Math.Max(0, by - 1); ny <= Math.Min(bh - 1, by + 1); ny++)
                    for (var nx = Math.Max(0, bx - 1); nx <= Math.Min(bw - 1, bx + 1); nx++)
                        (lowest, highest) = (Math.Min(lowest, soft[ny * bw + nx]), Math.Max(highest, soft[ny * bw + nx]));
                var closed = !flooded[by * bw + bx] && figure[by * bw + bx];
                if (lowest >= 1 && !closed)
                    continue;
                int x0 = bx * Block, x1 = Math.Min(width, x0 + Block), y0 = by * Block, y1 = Math.Min(height, y0 + Block);
                if (highest <= 0)
                {
                    for (var y = y0; y < y1; y++)
                        rgba.Slice((y * width + x0) * 4, (x1 - x0) * 4).Clear();
                    continue;
                }
                for (var y = y0; y < y1; y++)
                {
                    var fy = Math.Clamp((y + 0.5f) / Block - 0.5f, 0, bh - 1);
                    int ry0 = (int)fy, ry1 = Math.Min(bh - 1, ry0 + 1);
                    var ty = fy - ry0;
                    for (var x = x0; x < x1; x++)
                    {
                        var (rx0, rx1, tx) = columns[x];
                        var top = soft[ry0 * bw + rx0] + (soft[ry0 * bw + rx1] - soft[ry0 * bw + rx0]) * tx;
                        var bottom = soft[ry1 * bw + rx0] + (soft[ry1 * bw + rx1] - soft[ry1 * bw + rx0]) * tx;
                        var alpha = top + (bottom - top) * ty;
                        if (alpha >= 1 && !closed)
                            continue;
                        var at = (y * width + x) * 4;
                        int dr = rgba[at] - kr, dg = rgba[at + 1] - kg, db = rgba[at + 2] - kb;
                        var d = dr * dr + dg * dg + db * db;
                        // Where the closing filled in, the key's colour fades out over four times its clear distance.
                        if (closed && d < clear * 16)
                            alpha = Math.Min(alpha, (MathF.Sqrt(d) - MathF.Sqrt(clear)) / (3 * MathF.Sqrt(clear)));
                        if (alpha <= 0 || d <= clear)
                            rgba.Slice(at, 4).Clear();
                        else if (alpha < 1)
                            rgba[at + 3] = (byte)(rgba[at + 3] * alpha);
                    }
                }
            }
    }
}
