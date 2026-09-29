using Waddamburo.Platform.Sdl.Rendering;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Waddamburo.Game;
using Waddamburo.Upscale;

namespace Waddamburo.App.Presentation;

/// <summary>A texture to upscale: 8-bit RGBA pixels.</summary>
internal readonly record struct UpscaleSource(uint Width, uint Height, byte[] Rgba);

/// <summary>An upscaled texture from the cache: BC7 blocks (sizes multiples of 4).</summary>
internal readonly record struct UpscaledTexture(uint Width, uint Height, ReadOnlyMemory<byte> Bc7);

/// <summary>
/// Texture upscaling on the CPU with the realesr-animevideov3 model (3x, <see cref="CompactUpscaler"/>)
/// and the cache of its results: one memory-mapped tar (<see cref="UpscaleCache"/>) of BC7 textures
/// keyed by pixel hash, handed to the GPU as they are. Nothing derived leaves the user's machine.
/// The model's ncnn files (realesr-animevideov3-x3.param / .bin) are embedded, or WADDAMBURO_UPSCALE_MODEL
/// names a folder of them; BC7 needs the native
/// waddamburo_texture library. (GPU runs of the same model through realesrgan-ncnn-vulkan stalled
/// the desktop and returned corrupt images on an RTX 5080.)
/// </summary>
internal sealed class UpscaleTool : IDisposable
{
    private const string ModelName = "realesr-animevideov3-x3";
    private const string CacheName = "textures.tar";
    private const int Header = 8; // entry: width, height (uint32 LE), then the BC7 blocks
    private readonly CompactUpscaler _model;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly UpscaleCache _textures;
    // Pixel arrays that are upscaled copies decoded from BC7 (renderers without BC7), so the live
    // upscaler leaves them alone.
    private readonly ConditionalWeakTable<byte[], object> _upscaled = new();

    private UpscaleTool(CompactUpscaler model, string cache)
    {
        _model = model;
        Cache = cache;
        Directory.CreateDirectory(cache);
        if (File.Exists(UsedListPath))
            _used.UnionWith(File.ReadAllLines(UsedListPath));
        foreach (var work in Directory.GetDirectories(cache, "*.work"))
            Directory.Delete(work, recursive: true);
        var cachePath = Path.Combine(cache, CacheName);
        convertPngCache(cache, cachePath);
        _textures = new UpscaleCache(cachePath);
    }

    /// <summary>The folder holding textures.tar and used.txt.</summary>
    public string Cache { get; }

    public int CachedCount => _textures.Count;

    /// <summary>Movies the game has loaded ("archive|movie" lines): what a batch upscales.</summary>
    public string UsedListPath => Path.Combine(Cache, "used.txt");

    /// <summary>Worker threads for live upscaling while the game runs (WADDAMBURO_UPSCALE_THREADS, default 2).</summary>
    public static int LiveThreads => int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_UPSCALE_THREADS"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0 ? threads : 2;

    /// <summary>
    /// The tool, or null when the model files or the BC7 library are not installed. The cache lives in
    /// <paramref name="cacheFolder"/> (USRDIR/waddamburo/cache/upscaled), or in the user cache folder
    /// when there is no game folder (diagnostic runs).
    /// </summary>
    public static UpscaleTool? Find(string? cacheFolder)
    {
        // The model is embedded; WADDAMBURO_UPSCALE_MODEL names a folder of other ncnn files to try.
        CompactUpscaler model;
        if (Environment.GetEnvironmentVariable("WADDAMBURO_UPSCALE_MODEL") is { Length: > 0 } folder)
        {
            var param = Path.Combine(folder, ModelName + ".param");
            var bin = Path.Combine(folder, ModelName + ".bin");
            if (!File.Exists(param) || !File.Exists(bin))
                return null;
            model = CompactUpscaler.Load(param, bin);
        }
        else
        {
            var assembly = typeof(UpscaleTool).Assembly;
            using var param = assembly.GetManifestResourceStream($"Waddamburo.Upscale.{ModelName}.param");
            using var bin = assembly.GetManifestResourceStream($"Waddamburo.Upscale.{ModelName}.bin");
            if (param is null || bin is null)
                return null;
            model = CompactUpscaler.Load(param, bin);
        }
        if (!Bc7.Available)
        {
            Console.Error.WriteLine("Texture upscaling off: the waddamburo_texture library (BC7) is missing.");
            return null;
        }
        var userCache = Path.Combine(OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "Waddamburo", "upscaled");
        var cache = cacheFolder ?? userCache;
        // A cache from before it moved next to the game data comes along once.
        if (cache != userCache && !File.Exists(Path.Combine(cache, CacheName)) && Directory.Exists(userCache))
        {
            Directory.CreateDirectory(cache);
            foreach (var file in Directory.GetFiles(userCache))
                File.Move(file, Path.Combine(cache, Path.GetFileName(file)));
            Console.WriteLine($"Moved the upscaled-texture cache from {userCache} to {cache}.");
        }
        return new UpscaleTool(model, cache);
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
    public static string Key(UpscaleSource source) => hash(source) + ".bc7";

    private static string hash(UpscaleSource source)
    {
        using var hash = SHA256.Create();
        hash.TransformBlock(BitConverter.GetBytes(source.Width), 0, 4, null, 0);
        hash.TransformBlock(BitConverter.GetBytes(source.Height), 0, 4, null, 0);
        hash.TransformFinalBlock(source.Rgba, 0, source.Rgba.Length);
        return Convert.ToHexString(hash.Hash!)[..32];
    }

    public bool IsCached(string key) => _textures.Contains(key);

    /// <summary>A cached result (its blocks a slice of the mapped cache), or null.</summary>
    public UpscaledTexture? Load(string key)
    {
        var entry = _textures.Read(key);
        if (entry.Length < Header)
            return null;
        var span = entry.Span;
        return new UpscaledTexture(BinaryPrimitives.ReadUInt32LittleEndian(span),
            BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), entry[Header..]);
    }

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
        _textures.Write(Key(source), encode(outWidth, outHeight, rgba));
    }

    /// <summary>Whether these pixels are an upscaled copy decoded by <see cref="ApplyCached"/>.</summary>
    public bool IsUpscaled(byte[] rgba) => _upscaled.TryGetValue(rgba, out _);

    /// <summary>
    /// Swaps a freshly decoded movie's textures for their cached upscales before upload (on the
    /// decoding thread), so the game shows them at once: BC7 slices of the mapped cache, or decoded
    /// to RGBA when the renderer has no BC7.
    /// </summary>
    public void ApplyCached(LumenMovieContent content, bool bc7)
    {
        var textures = content.Textures;
        var upscaled = new LumenTextureContent?[textures.Length];
        Parallel.For(0, textures.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var texture = textures[index];
            var rgba = ImmutableCollectionsMarshal.AsArray(texture.Rgba8);
            if (rgba is not { Length: > 0 } || texture.Width * texture.Height < 256
                || Load(Key(new UpscaleSource((uint)texture.Width, (uint)texture.Height, rgba))) is not { } cached)
                return;
            if (bc7)
            {
                upscaled[index] = texture with { Width = (int)cached.Width, Height = (int)cached.Height, Rgba8 = [], Bc7 = cached.Bc7 };
                return;
            }
            var pixels = Bc7.Decode(cached.Bc7.Span, (int)cached.Width, (int)cached.Height);
            _upscaled.AddOrUpdate(pixels, this);
            upscaled[index] = texture with
            {
                Width = (int)cached.Width,
                Height = (int)cached.Height,
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

    // Sizes rounded up to multiples of 4 (BC7 blocks; UVs are normalised, so a resize is invisible),
    // then the entry: width, height, blocks.
    private static byte[] encode(int width, int height, byte[] rgba)
    {
        var (w, h) = ((width + 3) / 4 * 4, (height + 3) / 4 * 4);
        if (w != width || h != height)
            rgba = CompactUpscaler.ResizeRgba(rgba, width, height, w, h);
        var blocks = Bc7.Encode(rgba, w, h);
        var entry = new byte[Header + blocks.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)w);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)h);
        blocks.CopyTo(entry, Header);
        return entry;
    }

    // One-time move of the PNG cache (upscaled.tar, loose PNGs) to BC7: no upscaling runs again.
    private static void convertPngCache(string folder, string target)
    {
        var old = Path.Combine(folder, "upscaled.tar");
        var loose = Directory.GetFiles(folder, "*.png");
        if (!File.Exists(old) && loose.Length == 0)
            return;
        Console.WriteLine("Converting the upscaled-texture cache to BC7 (once; a minute or two)...");
        using var textures = new UpscaleCache(target);
        void convert(string name, byte[] png)
        {
            var bc7 = Path.GetFileNameWithoutExtension(name) + ".bc7";
            if (textures.Contains(bc7))
                return;
            var (width, height, rgba) = PngFile.Decode(png, name);
            textures.Write(bc7, encode((int)width, (int)height, rgba));
        }
        if (File.Exists(old))
        {
            using var pngs = new UpscaleCache(old);
            Parallel.ForEach(pngs.Names.Where(static name => name.EndsWith(".png", StringComparison.Ordinal)),
                name => convert(name, pngs.Read(name).ToArray()));
        }
        Parallel.ForEach(loose, file => convert(Path.GetFileName(file), File.ReadAllBytes(file)));
        File.Delete(old);
        foreach (var file in loose)
            File.Delete(file);
        Console.WriteLine($"Converted: {textures.Count} textures in {target}.");
    }

    private static bool opaque(byte[] rgba)
    {
        for (var pixel = 3; pixel < rgba.Length; pixel += 4)
            if (rgba[pixel] != 255)
                return false;
        return true;
    }
}
