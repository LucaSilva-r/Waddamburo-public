using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Waddamburo.Game;
using System.Security.Cryptography;
using Waddamburo.Upscale;

namespace Waddamburo.App.Presentation;

/// <summary>A texture to upscale: 8-bit RGBA pixels.</summary>
internal readonly record struct UpscaleSource(uint Width, uint Height, byte[] Rgba);

/// <summary>
/// Texture upscaling on the CPU with the realesr-animevideov3 model (3x, <see cref="CompactUpscaler"/>)
/// and the cache of its results (<see cref="UpscaleCache"/>, one tar keyed by pixel hash); nothing
/// derived leaves the user's machine.
/// The model's ncnn files (realesr-animevideov3-x3.param / .bin) sit in an <c>upscale</c> folder next
/// to Waddamburo, or WADDAMBURO_UPSCALE_MODEL names their folder. (GPU runs of the same model through
/// realesrgan-ncnn-vulkan stalled the desktop and returned corrupt images on an RTX 5080.)
/// </summary>
internal sealed class UpscaleTool : IDisposable
{
    private const string ModelName = "realesr-animevideov3-x3";
    private readonly CompactUpscaler _model;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly UpscaleCache _textures;
    // Pixel arrays that are already upscaled (so the live upscaler leaves them alone).
    private readonly ConditionalWeakTable<byte[], object> _upscaled = new();

    private UpscaleTool(CompactUpscaler model, string cache)
    {
        _model = model;
        Cache = cache;
        Directory.CreateDirectory(cache);
        if (File.Exists(UsedListPath))
            _used.UnionWith(File.ReadAllLines(UsedListPath));
        _textures = new UpscaleCache(Path.Combine(cache, "upscaled.tar"));
        // Loose PNGs from before the tar move in once; leftovers of interrupted writes go.
        foreach (var file in Directory.GetFiles(cache, "*.png"))
        {
            _textures.Write(Path.GetFileName(file), File.ReadAllBytes(file));
            File.Delete(file);
        }
        foreach (var partial in Directory.GetFiles(cache, "*.part"))
            File.Delete(partial);
        foreach (var work in Directory.GetDirectories(cache, "*.work"))
            Directory.Delete(work, recursive: true);
    }

    /// <summary>The folder holding upscaled.tar and used.txt.</summary>
    public string Cache { get; }

    public int CachedCount => _textures.Count;

    /// <summary>Movies the game has loaded ("archive|movie" lines): what a batch upscales.</summary>
    public string UsedListPath => Path.Combine(Cache, "used.txt");

    /// <summary>Worker threads for live upscaling while the game runs (WADDAMBURO_UPSCALE_THREADS, default 2).</summary>
    public static int LiveThreads => int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_UPSCALE_THREADS"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0 ? threads : 2;

    /// <summary>The tool, or null when the model files are not installed.</summary>
    public static UpscaleTool? Find()
    {
        var folder = Environment.GetEnvironmentVariable("WADDAMBURO_UPSCALE_MODEL") is { Length: > 0 } set ? set
            : Path.Combine(AppContext.BaseDirectory, "upscale");
        var param = Path.Combine(folder, ModelName + ".param");
        var bin = Path.Combine(folder, ModelName + ".bin");
        if (!File.Exists(param) || !File.Exists(bin))
            return null;
        var cache = Path.Combine(OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "Waddamburo", "upscaled");
        return new UpscaleTool(CompactUpscaler.Load(param, bin), cache);
    }

    /// <summary>Notes a movie the game loaded (any thread).</summary>
    public void RecordUsed(string archive, string movie)
    {
        var line = $"{archive}|{movie}";
        lock (_used)
        {
            if (_used.Add(line))
                File.AppendAllLines(UsedListPath, [line]);
        }
    }

    /// <summary>The cache entry name of a texture: its size and pixels hashed.</summary>
    public static string Key(UpscaleSource source)
    {
        using var hash = SHA256.Create();
        hash.TransformBlock(BitConverter.GetBytes(source.Width), 0, 4, null, 0);
        hash.TransformBlock(BitConverter.GetBytes(source.Height), 0, 4, null, 0);
        hash.TransformFinalBlock(source.Rgba, 0, source.Rgba.Length);
        return Convert.ToHexString(hash.Hash!)[..32] + ".png";
    }

    public bool IsCached(string key) => _textures.Contains(key);

    /// <summary>A cached result, or null.</summary>
    public (uint Width, uint Height, byte[] Rgba)? Load(string key) =>
        _textures.Read(key) is { } png ? PngFile.Decode(png, key) : null;

    /// <summary>
    /// Upscales one texture into the cache. Alpha goes through the network too, as a grey image
    /// (crisper edges than resizing it); fully opaque textures skip that pass.
    /// <paramref name="pause"/> is checked between tiles (live upscaling holds during gameplay).
    /// </summary>
    public void Upscale(UpscaleSource source, int threads, Func<bool>? pause = null)
    {
        var width = (int)source.Width;
        var height = (int)source.Height;
        var (outWidth, outHeight, rgba) = _model.Upscale(width, height, source.Rgba, threads, pause);
        if (!opaque(source.Rgba))
        {
            var grey = new byte[source.Rgba.Length];
            for (var pixel = 0; pixel < grey.Length; pixel += 4)
                grey[pixel] = grey[pixel + 1] = grey[pixel + 2] = source.Rgba[pixel + 3];
            var alpha = _model.Upscale(width, height, grey, threads, pause).Rgba;
            for (var pixel = 3; pixel < rgba.Length; pixel += 4)
                rgba[pixel] = alpha[pixel - 3];
        }
        _textures.Write(Key(source), PngFile.Encode((uint)outWidth, (uint)outHeight, rgba));
    }

    /// <summary>Whether these pixels are an upscaled copy put in by <see cref="ApplyCached"/>.</summary>
    public bool IsUpscaled(byte[] rgba) => _upscaled.TryGetValue(rgba, out _);

    /// <summary>
    /// Swaps a freshly decoded movie's textures for their cached upscales before upload (on the
    /// decoding thread, a few PNGs at a time), so the game shows them at once.
    /// </summary>
    public void ApplyCached(LumenMovieContent content)
    {
        var textures = content.Textures;
        var upscaled = new LumenTextureContent?[textures.Length];
        Parallel.For(0, textures.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var texture = textures[index];
            var rgba = ImmutableCollectionsMarshal.AsArray(texture.Rgba8);
            if (rgba is not { Length: > 0 } || texture.Width * texture.Height < 256
                || Load(Key(new UpscaleSource((uint)texture.Width, (uint)texture.Height, rgba))) is not var (width, height, pixels))
                return;
            _upscaled.AddOrUpdate(pixels, this);
            upscaled[index] = texture with
            {
                Width = (int)width,
                Height = (int)height,
                Rgba8 = ImmutableCollectionsMarshal.AsImmutableArray(pixels),
            };
        });
        content.ReplaceTextures((texture, index) => upscaled[index] ?? texture);
    }

    public void Dispose()
    {
        _textures.Dispose();
        _model.Dispose();
    }

    private static bool opaque(byte[] rgba)
    {
        for (var pixel = 3; pixel < rgba.Length; pixel += 4)
            if (rgba[pixel] != 255)
                return false;
        return true;
    }
}
