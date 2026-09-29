using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Waddamburo.Upscale;

/// <summary>
/// Runs a Real-ESRGAN "compact" network (SRVGGNetCompact: 3x3 convolutions with PReLU, a pixel
/// shuffle, the input added back nearest-upscaled, and an optional final bicubic resize) on the CPU,
/// from its ncnn .param/.bin files (realesr-animevideov3, and community models of the same shape).
/// Tiles run in parallel on <c>threads</c> cores; a tile carries a halo as wide as the network is
/// deep, and every layer zeroes what lies outside the image, so tiles match a whole-image run.
/// </summary>
public sealed class CompactUpscaler : IDisposable
{
    private const uint Fp16Tag = 0x01306B47;
    private const int TileSize = 256;
    private sealed record Layer(int Inputs, int Outputs, float[] Weights, float[] Bias, float[]? Slopes)
    {
        /// <summary>Weights as [output block of 8][input][tap][8].</summary>
        public float[] Packed { get; } = pack(Inputs, Outputs, Weights);

        private static float[] pack(int inputs, int outputs, float[] weights)
        {
            if (outputs % 8 != 0)
                throw new InvalidDataException("Output channel counts must be multiples of 8.");
            var packed = new float[weights.Length];
            var at = 0;
            for (var block = 0; block < outputs / 8; block++)
                for (var ci = 0; ci < inputs; ci++)
                    for (var tap = 0; tap < 9; tap++)
                        for (var k = 0; k < 8; k++)
                            packed[at++] = weights[((block * 8 + k) * inputs + ci) * 9 + tap];
            return packed;
        }
    }

    private readonly ThreadLocal<(float[] A, float[] B)> _buffers = new(static () => ([], []));
    private readonly Layer[] _layers;
    private readonly int _shuffle;
    private readonly double _resize;

    private CompactUpscaler(Layer[] layers, int shuffle, double resize)
    {
        _layers = layers;
        _shuffle = shuffle;
        _resize = resize;
    }

    public void Dispose() => _buffers.Dispose();

    /// <summary>The output size over the input size (3 for realesr-animevideov3-x3).</summary>
    public double Scale => _shuffle * _resize;

    public static CompactUpscaler Load(string paramPath, string binPath)
    {
        var lines = File.ReadAllLines(paramPath).Skip(2).Select(static line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        static Dictionary<int, string> options(string[] parts) => parts.Skip(4 + int.Parse(parts[2], CultureInfo.InvariantCulture)
                + int.Parse(parts[3], CultureInfo.InvariantCulture))
            .Select(static option => option.Split('=')).ToDictionary(static kv => int.Parse(kv[0], CultureInfo.InvariantCulture), static kv => kv[1]);
        var data = File.ReadAllBytes(binPath).AsSpan();
        var offset = 0;
        var layers = new List<Layer>();
        int shuffle = 1;
        double resize = 1;
        for (var index = 0; index < lines.Length; index++)
        {
            var parts = lines[index];
            switch (parts[0])
            {
                case "Convolution":
                {
                    var o = options(parts);
                    var outputs = int.Parse(o[0], CultureInfo.InvariantCulture);
                    var count = int.Parse(o[6], CultureInfo.InvariantCulture);
                    if (o.GetValueOrDefault(1) != "3" || o.GetValueOrDefault(4) != "1")
                        throw new InvalidDataException("Only 3x3 convolutions with padding 1 are supported.");
                    var tag = BitConverter.ToUInt32(data[offset..]);
                    offset += 4;
                    var weights = new float[count];
                    if (tag == Fp16Tag)
                    {
                        for (var i = 0; i < count; i++)
                            weights[i] = (float)BitConverter.ToHalf(data[(offset + i * 2)..]);
                        offset += (count * 2 + 3) / 4 * 4;
                    }
                    else if (tag == 0)
                    {
                        for (var i = 0; i < count; i++)
                            weights[i] = BitConverter.ToSingle(data[(offset + i * 4)..]);
                        offset += count * 4;
                    }
                    else
                        throw new InvalidDataException($"Unsupported ncnn weight storage 0x{tag:X8}.");
                    var bias = new float[outputs];
                    for (var i = 0; i < outputs; i++)
                        bias[i] = BitConverter.ToSingle(data[(offset + i * 4)..]);
                    offset += outputs * 4;
                    float[]? slopes = null;
                    if (index + 1 < lines.Length && lines[index + 1][0] == "PReLU")
                    {
                        slopes = new float[outputs];
                        for (var i = 0; i < outputs; i++)
                            slopes[i] = BitConverter.ToSingle(data[(offset + i * 4)..]);
                        offset += outputs * 4;
                        index++;
                    }
                    layers.Add(new Layer(count / (outputs * 9), outputs, weights, bias, slopes));
                    break;
                }
                case "PixelShuffle":
                    shuffle = int.Parse(options(parts)[0], CultureInfo.InvariantCulture);
                    break;
                case "Interp":
                    // One upscales the input for the skip connection (by the shuffle factor); a
                    // shrinking one resizes the result.
                    var scale = double.Parse(options(parts)[1], CultureInfo.InvariantCulture);
                    if (scale < 1)
                        resize = scale;
                    break;
            }
        }
        if (offset != data.Length || layers.Count < 2 || layers[0].Inputs != 3 || layers[^1].Outputs != 3 * shuffle * shuffle)
            throw new InvalidDataException("Not a compact 3-channel upscaling network.");
        return new CompactUpscaler([.. layers], shuffle, resize);
    }

    /// <summary>
    /// Upscales 8-bit RGB (the first three bytes of each 4-byte pixel) to Scale x the size; returns
    /// RGBA with alpha 255. <paramref name="pause"/> holds the work between tiles while it returns true.
    /// </summary>
    public (int Width, int Height, byte[] Rgba) Upscale(int width, int height, ReadOnlySpan<byte> rgba, int threads,
        Func<bool>? pause = null)
    {
        // The network's output (shuffle x) as float RGB planes, then resized.
        var bigWidth = width * _shuffle;
        var bigHeight = height * _shuffle;
        var big = new float[3 * bigWidth * bigHeight];
        var source = new float[3 * width * height];
        for (var i = 0; i < width * height; i++)
            for (var c = 0; c < 3; c++)
                source[c * width * height + i] = rgba[i * 4 + c] / 255f;
        var tiles = new List<(int X, int Y)>();
        for (var y = 0; y < height; y += TileSize)
            for (var x = 0; x < width; x += TileSize)
                tiles.Add((x, y));
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            tile =>
            {
                while (pause?.Invoke() == true)
                    Thread.Sleep(100);
                runTile(source, width, height, tile.X, tile.Y, big, bigWidth);
            });

        var outWidth = (int)Math.Round(width * Scale);
        var outHeight = (int)Math.Round(height * Scale);
        var result = new byte[outWidth * outHeight * 4];
        Parallel.For(0, 3, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, c =>
        {
            var plane = big.AsSpan(c * bigWidth * bigHeight, bigWidth * bigHeight);
            var resized = outWidth == bigWidth ? plane.ToArray() : Bicubic.Resize(plane, bigWidth, bigHeight, outWidth, outHeight);
            for (var i = 0; i < resized.Length; i++)
                result[i * 4 + c] = (byte)Math.Clamp(resized[i] * 255f + 0.5f, 0, 255);
        });
        for (var i = 3; i < result.Length; i += 4)
            result[i] = 255;
        return (outWidth, outHeight, result);
    }

    private void runTile(float[] source, int width, int height, int tileX, int tileY, float[] big, int bigWidth)
    {
        var halo = _layers.Length;
        var innerWidth = Math.Min(TileSize, width - tileX);
        var innerHeight = Math.Min(TileSize, height - tileY);
        // The tile with its halo; planes have a one-pixel zero ring (the convolution's padding) and
        // rows rounded up to whole vectors.
        var x0 = tileX - halo;
        var y0 = tileY - halo;
        var w = innerWidth + 2 * halo;
        var h = innerHeight + 2 * halo;
        var lanes = Vector<float>.Count;
        var stride = (w + lanes - 1) / lanes * lanes + 2 + lanes;
        var planeSize = stride * (h + 2);
        var channels = _layers.Max(static layer => Math.Max(layer.Inputs, layer.Outputs));
        // Per-thread buffers, reused: fresh ~20 MB arrays per tile kept the GC stopping every thread.
        var size = channels * planeSize;
        var buffers = _buffers.Value!;
        if (buffers.A.Length < size)
            buffers = _buffers.Value = (new float[size], new float[size]);
        var current = buffers.A;
        var next = buffers.B;
        // Zeroed: the planes' ring and the columns past the tile are the convolution's padding.
        current.AsSpan(0, size).Clear();
        next.AsSpan(0, size).Clear();
        for (var c = 0; c < 3; c++)
            for (var y = 0; y < h; y++)
            {
                var sy = y0 + y;
                if (sy < 0 || sy >= height) continue;
                for (var x = 0; x < w; x++)
                {
                    var sx = x0 + x;
                    if (sx >= 0 && sx < width)
                        current[c * planeSize + (y + 1) * stride + x + 1] = source[c * width * height + sy * width + sx];
                }
            }
        foreach (var layer in _layers)
        {
            convolve(layer, current, next, w, h, stride, planeSize);
            // Outside the image stays zero (the next layer's padding), as in a whole-image run.
            for (var c = 0; c < layer.Outputs; c++)
                for (var y = 0; y < h; y++)
                {
                    var row = next.AsSpan(c * planeSize + (y + 1) * stride + 1, w);
                    var sy = y0 + y;
                    if (sy < 0 || sy >= height)
                    {
                        row.Clear();
                        continue;
                    }
                    if (x0 < 0) row[..Math.Min(w, -x0)].Clear();
                    if (x0 + w > width) row[Math.Max(0, width - x0)..].Clear();
                    // Columns past w (vector overrun) back to zero: they are the next layer's padding.
                    next.AsSpan(c * planeSize + (y + 1) * stride + 1 + w, stride - 1 - w).Clear();
                }
            (current, next) = (next, current);
        }
        // Pixel shuffle (channel c*r*r + i*r + j -> row i, column j of each block) plus the input.
        var r = _shuffle;
        for (var c = 0; c < 3; c++)
            for (var y = 0; y < innerHeight; y++)
                for (var x = 0; x < innerWidth; x++)
                {
                    var inputValue = source[c * width * height + (tileY + y) * width + tileX + x];
                    for (var i = 0; i < r; i++)
                        for (var j = 0; j < r; j++)
                        {
                            var value = current[(c * r * r + i * r + j) * planeSize + (y + halo + 1) * stride + x + halo + 1];
                            big[c * big.Length / 3 + ((tileY + y) * r + i) * bigWidth + (tileX + x) * r + j] = value + inputValue;
                        }
                }
    }

    // 3x3 convolution, eight output channels at a time in registers, each input load shared by all
    // eight; weights packed [block][input][tap][8] in load order.
    private static void convolve(Layer layer, float[] input, float[] output, int w, int h, int stride, int planeSize)
    {
        var lanes = Vector<float>.Count;
        var packed = layer.Packed;
        var slopes = layer.Slopes;
        // Row by row, all output blocks per row: the three input rows (all channels) stay in cache.
        for (var y = 0; y < h; y++)
            for (var block = 0; block < layer.Outputs / 8; block++)
            {
                var o0 = block * 8;
                for (var x = 0; x < w; x += lanes)
                {
                    var a0 = new Vector<float>(layer.Bias[o0]);
                    var a1 = new Vector<float>(layer.Bias[o0 + 1]);
                    var a2 = new Vector<float>(layer.Bias[o0 + 2]);
                    var a3 = new Vector<float>(layer.Bias[o0 + 3]);
                    var a4 = new Vector<float>(layer.Bias[o0 + 4]);
                    var a5 = new Vector<float>(layer.Bias[o0 + 5]);
                    var a6 = new Vector<float>(layer.Bias[o0 + 6]);
                    var a7 = new Vector<float>(layer.Bias[o0 + 7]);
                    var wi = block * layer.Inputs * 9 * 8;
                    for (var ci = 0; ci < layer.Inputs; ci++)
                    {
                        var at = ci * planeSize + y * stride + x;
                        for (var ky = 0; ky < 3; ky++, at += stride)
                            for (var kx = 0; kx < 3; kx++, wi += 8)
                            {
                                var v = Unsafe.As<float, Vector<float>>(ref input[at + kx]);
                                a0 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi]), v, a0);
                                a1 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 1]), v, a1);
                                a2 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 2]), v, a2);
                                a3 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 3]), v, a3);
                                a4 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 4]), v, a4);
                                a5 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 5]), v, a5);
                                a6 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 6]), v, a6);
                                a7 = Vector.FusedMultiplyAdd(new Vector<float>(packed[wi + 7]), v, a7);
                            }
                    }
                    var target = (y + 1) * stride + x + 1;
                    store(output, o0 * planeSize + target, a0, slopes, o0);
                    store(output, (o0 + 1) * planeSize + target, a1, slopes, o0 + 1);
                    store(output, (o0 + 2) * planeSize + target, a2, slopes, o0 + 2);
                    store(output, (o0 + 3) * planeSize + target, a3, slopes, o0 + 3);
                    store(output, (o0 + 4) * planeSize + target, a4, slopes, o0 + 4);
                    store(output, (o0 + 5) * planeSize + target, a5, slopes, o0 + 5);
                    store(output, (o0 + 6) * planeSize + target, a6, slopes, o0 + 6);
                    store(output, (o0 + 7) * planeSize + target, a7, slopes, o0 + 7);
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void store(float[] output, int at, Vector<float> value, float[]? slopes, int channel)
    {
        if (slopes is not null)
            value = Vector.ConditionalSelect(Vector.GreaterThan(value, Vector<float>.Zero), value,
                value * new Vector<float>(slopes[channel]));
        Unsafe.As<float, Vector<float>>(ref output[at]) = value;
    }

    /// <summary>Bicubic resize of RGBA8 (BC7 needs sizes that are multiples of 4).</summary>
    public static byte[] ResizeRgba(ReadOnlySpan<byte> rgba, int width, int height, int outWidth, int outHeight)
    {
        var result = new byte[outWidth * outHeight * 4];
        var plane = new float[width * height];
        for (var c = 0; c < 4; c++)
        {
            for (var i = 0; i < plane.Length; i++)
                plane[i] = rgba[i * 4 + c];
            var resized = Bicubic.Resize(plane, width, height, outWidth, outHeight);
            for (var i = 0; i < resized.Length; i++)
                result[i * 4 + c] = (byte)Math.Clamp(resized[i] + 0.5f, 0, 255);
        }
        return result;
    }

    /// <summary>Separable bicubic resize (a = -0.75, as ncnn's Interp), edges clamped.</summary>
    private static class Bicubic
    {
        public static float[] Resize(ReadOnlySpan<float> source, int width, int height, int outWidth, int outHeight)
        {
            var rows = new float[outWidth * height];
            for (var y = 0; y < height; y++)
                pass(source.Slice(y * width, width), rows.AsSpan(y * outWidth, outWidth));
            var result = new float[outWidth * outHeight];
            var column = new float[height];
            var resized = new float[outHeight];
            for (var x = 0; x < outWidth; x++)
            {
                for (var y = 0; y < height; y++)
                    column[y] = rows[y * outWidth + x];
                pass(column, resized);
                for (var y = 0; y < outHeight; y++)
                    result[y * outWidth + x] = resized[y];
            }
            return result;
        }

        private static void pass(ReadOnlySpan<float> source, Span<float> target)
        {
            var scale = (double)source.Length / target.Length;
            for (var i = 0; i < target.Length; i++)
            {
                var position = (i + 0.5) * scale - 0.5;
                var start = (int)Math.Floor(position);
                var t = position - start;
                double sum = 0;
                for (var k = -1; k <= 2; k++)
                {
                    var index = Math.Clamp(start + k, 0, source.Length - 1);
                    sum += source[index] * weight(k - t);
                }
                target[i] = (float)sum;
            }
        }

        private static double weight(double x)
        {
            const double A = -0.75;
            x = Math.Abs(x);
            return x < 1 ? ((A + 2) * x - (A + 3)) * x * x + 1
                : x < 2 ? ((A * x - 5 * A) * x + 8 * A) * x - 4 * A
                : 0;
        }
    }
}
