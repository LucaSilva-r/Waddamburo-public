namespace Waddamburo.App.Presentation;

/// <summary>
/// A background video's see-through parts, cut on its decoding thread: black bars (rows and columns black in every
/// frame so far: osu!taiko videos leave the top black for osu!'s playfield) and a keyed colour (a green screen).
/// </summary>
internal sealed class VideoMask(int width, int height, int? keyColor = null, int keyTolerance = 60)
{
    // Brighter than this (r + g + b) is picture: compressed black stays well under it.
    private const int BlackSum = 48;

    // Rows and columns that have shown picture: from then on they are never a bar again.
    private readonly bool[] _rowSeen = new bool[height];
    private readonly bool[] _columnSeen = new bool[width];
    private bool _allSeen;

    /// <summary>Makes the frame's bars and keyed colour transparent (RGBA8, in place).</summary>
    public void Apply(Span<byte> rgba)
    {
        if (!_allSeen)
            cutBars(rgba);
        if (keyColor is { } key)
            cutKey(rgba, key);
    }

    private void cutBars(Span<byte> rgba)
    {
        for (var y = 0; y < height; y++)
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

    // The key's matte: a pixel within the tolerance is background where it joins the frame's edge through such pixels,
    // or anywhere when it is within a third of it (gaps between arms); the rest (dark stockings, hair shadows on a black
    // key) stays opaque. A 3x3 average of the matte softens its edge.
    private bool[]? _background;
    private float[]? _matte, _rows;
    private int[]? _queue;

    private void cutKey(Span<byte> rgba, int key)
    {
        int kr = key >> 16 & 0xFF, kg = key >> 8 & 0xFF, kb = key & 0xFF, pixels = width * height;
        var background = _background ??= new bool[pixels];
        var queue = _queue ??= new int[pixels];
        Array.Clear(background);
        int near = keyTolerance * keyTolerance, close = near / 9, head = 0, tail = 0;
        int distance(Span<byte> frame, int pixel)
        {
            int dr = frame[pixel * 4] - kr, dg = frame[pixel * 4 + 1] - kg, db = frame[pixel * 4 + 2] - kb;
            return dr * dr + dg * dg + db * db;
        }
        void seed(Span<byte> frame, int pixel)
        {
            if (!background[pixel] && distance(frame, pixel) <= near)
                (background[pixel], queue[tail++]) = (true, pixel);
        }
        for (var x = 0; x < width; x++)
        {
            seed(rgba, x);
            seed(rgba, (height - 1) * width + x);
        }
        for (var y = 0; y < height; y++)
        {
            seed(rgba, y * width);
            seed(rgba, y * width + width - 1);
        }
        for (var pixel = 0; pixel < pixels; pixel++)
            if (!background[pixel] && distance(rgba, pixel) <= close)
                (background[pixel], queue[tail++]) = (true, pixel);
        while (head < tail)
        {
            var pixel = queue[head++];
            int x = pixel % width, y = pixel / width;
            if (x > 0) seed(rgba, pixel - 1);
            if (x < width - 1) seed(rgba, pixel + 1);
            if (y > 0) seed(rgba, pixel - width);
            if (y < height - 1) seed(rgba, pixel + width);
        }
        // The soft matte: a box blur, rows then columns.
        var matte = _matte ??= new float[pixels];
        var rows = _rows ??= new float[pixels];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float sum = 0, count = 0;
                for (var dx = Math.Max(0, x - 1); dx <= Math.Min(width - 1, x + 1); dx++, count++)
                    sum += background[y * width + dx] ? 0 : 1;
                rows[y * width + x] = sum / count;
            }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float sum = 0, count = 0;
                for (var dy = Math.Max(0, y - 1); dy <= Math.Min(height - 1, y + 1); dy++, count++)
                    sum += rows[dy * width + x];
                matte[y * width + x] = sum / count;
            }
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            if (matte[pixel] >= 1)
                continue;
            if (matte[pixel] <= 0)
                rgba.Slice(pixel * 4, 4).Clear();
            else
                rgba[pixel * 4 + 3] = (byte)(rgba[pixel * 4 + 3] * matte[pixel]);
        }
    }
}
