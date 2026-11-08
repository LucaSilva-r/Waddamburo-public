using Waddamburo.Platform.Sdl.Rendering;
using System.Buffers.Binary;
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
        loadIndex();
        Directory.CreateDirectory(cache);
        if (File.Exists(UsedListPath))
            _used.UnionWith(File.ReadAllLines(UsedListPath));
        foreach (var work in Directory.GetDirectories(cache, "*.work"))
            Directory.Delete(work, recursive: true);
        var cachePath = Path.Combine(cache, CacheName);
        convertPngCache(cache, cachePath);
        _textures = new UpscaleCache(cachePath);
        _sized = _textures.Names.Count(static name => name.Contains('@', StringComparison.Ordinal));
    }

    /// <summary>The folder holding textures.tar and used.txt.</summary>
    public string Cache { get; }

    /// <summary>Upscaled textures in the cache (the smaller copies of <see cref="Scale"/> not counted).</summary>
    public int CachedCount => _textures.Count - _sized;

    private int _sized;

    /// <summary>Movies the game has loaded ("archive|movie" lines): what a batch upscales.</summary>
    public string UsedListPath => Path.Combine(Cache, "used.txt");

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

    // Where each texture's cache name is known (archive, movie, texture index), so a load needs neither
    // decoded pixels nor a hash: index.tsv, one line per texture with its archive's size and time.
    // An archive that changed (an update, another release, a mod) has its lines ignored and learned again.
    private const string IndexName = "index.tsv";
    private readonly Dictionary<(string Archive, string Movie, int Texture), (long Size, long Time, string Key)> _index = new();
    private readonly Dictionary<string, (long Size, long Time)?> _archiveStamps = new(StringComparer.Ordinal);

    /// <summary>The lumendata/packed folder the archive ids in <see cref="ApplyCached(string, LumenMovieContent, bool)"/> are relative to.</summary>
    public string? LumenRoot { get; set; }

    private void loadIndex()
    {
        var path = Path.Combine(Cache, IndexName);
        if (!File.Exists(path))
            return;
        foreach (var line in File.ReadLines(path))
            if (line.Split('\t') is [var archive, var size, var time, var movie, var texture, var key]
                && long.TryParse(size, out var bytes) && long.TryParse(time, out var ticks) && int.TryParse(texture, out var index))
                _index[(archive, movie, index)] = (bytes, ticks, key); // later lines win
    }

    // An archive's size and write time, read once a session (null: missing).
    private (long Size, long Time)? stamp(string archive)
    {
        lock (_archiveStamps)
        {
            if (_archiveStamps.TryGetValue(archive, out var known))
                return known;
            var file = LumenRoot is null ? null : new FileInfo(Path.Combine(LumenRoot, archive));
            return _archiveStamps[archive] = file is { Exists: true } ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
    }

    private string? knownKey(string archive, string movie, int texture)
    {
        if (stamp(archive) is not { } current)
            return null;
        lock (_index)
            return _index.TryGetValue((archive, movie, texture), out var entry) && (entry.Size, entry.Time) == current ? entry.Key : null;
    }

    private void remember(string archive, string movie, IReadOnlyList<(int Texture, string Key)> keys)
    {
        if (keys.Count == 0 || stamp(archive) is not { } current)
            return;
        lock (_index)
        {
            foreach (var (texture, key) in keys)
                _index[(archive, movie, texture)] = (current.Size, current.Time, key);
            File.AppendAllLines(Path.Combine(Cache, IndexName),
                keys.Select(entry => $"{archive}\t{current.Size}\t{current.Time}\t{movie}\t{entry.Texture}\t{entry.Key}"));
        }
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

    /// <summary>
    /// The upscale factor textures are loaded at (1-3, from the display resolution): below the model's 3x,
    /// a smaller copy is made from the 3x one the first time and kept in the cache beside it.
    /// </summary>
    public double Scale { get; set; } = 3;

    /// <summary>A cached result at <see cref="Scale"/> (its blocks a slice of the mapped cache), or null.</summary>
    public UpscaledTexture? Load(string key)
    {
        var result = read(key);
        if (result is not { } full || Scale >= 3)
            return result;
        var (width, height) = ((int)(full.Width * Scale / 3 + 3) / 4 * 4, (int)(full.Height * Scale / 3 + 3) / 4 * 4);
        if (width >= full.Width || height >= full.Height)
            return full;
        var sized = $"{key[..^4]}@{width}x{height}.bc7";
        if (!_textures.Contains(sized))
        {
            Interlocked.Increment(ref _sized);
            // ponytail: plain bicubic, which aliases a little shrinking past 2x; box-filter first if that shows.
            var rgba = Bc7.Decode(full.Bc7.Span, (int)full.Width, (int)full.Height);
            _textures.Write(sized, encode(width, height, CompactUpscaler.ResizeRgba(rgba, (int)full.Width, (int)full.Height, width, height)));
        }
        return read(sized);
    }

    private UpscaledTexture? read(string key)
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

    /// <summary>Whether these pixels are an upscaled copy decoded by <see cref="ApplyCached(string, LumenMovieContent, bool)"/>.</summary>
    public bool IsUpscaled(byte[] rgba) => _upscaled.TryGetValue(rgba, out _);

    /// <summary>
    /// Marks pixels as needing no upscale (textures a Lumen patch derived from upscaled ones: upscaling a
    /// derived layer on its own would sharpen its edges).
    /// </summary>
    public void MarkUpscaled(byte[] rgba) => _upscaled.AddOrUpdate(rgba, this);

    /// <summary>
    /// Swaps a freshly decoded movie's textures for their cached upscales before upload (on the
    /// decoding thread), so the game shows them at once: BC7 slices of the mapped cache, or decoded
    /// to RGBA when the renderer has no BC7. Each texture's cache name comes from the index; only a
    /// texture not seen before is decoded and hashed (once), and every texture leaves with its name.
    /// </summary>
    public void ApplyCached(string archive, LumenMovieContent content, bool bc7)
    {
        var textures = content.Textures;
        var replaced = new LumenTextureContent?[textures.Length];
        var learned = new System.Collections.Concurrent.ConcurrentBag<(int, string)>();
        Parallel.For(0, textures.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var texture = textures[index];
            if (texture.Width * texture.Height < 256 || texture.Rgba8.IsDefaultOrEmpty && texture.Blocks.IsEmpty)
                return;
            var key = knownKey(archive, content.Name, texture.Index);
            if (key is null)
            {
                key = Key(new UpscaleSource((uint)texture.Width, (uint)texture.Height, texture.Pixels()));
                learned.Add((texture.Index, key));
            }
            replaced[index] = ApplyCached(texture, key, bc7);
        });
        remember(archive, content.Name, [.. learned]);
        content.ReplaceTextures((texture, index) => replaced[index] ?? texture);
    }

    /// <summary>
    /// One texture swapped for its cached upscale (BC7, or decoded to RGBA without BC7), or left as it is with its
    /// cache name (<paramref name="key"/>; null: hashed from its pixels) for the upscaler to fill.
    /// </summary>
    public LumenTextureContent ApplyCached(LumenTextureContent texture, string? key, bool bc7)
    {
        key ??= Key(new UpscaleSource((uint)texture.Width, (uint)texture.Height, texture.Pixels()));
        if (Load(key) is not { } cached)
            return texture with { UpscaleKey = key };
        if (bc7)
            return texture with
            {
                Width = (int)cached.Width, Height = (int)cached.Height, Rgba8 = [], Blocks = default, Bc7 = cached.Bc7, UpscaleKey = key,
            };
        var pixels = Bc7.Decode(cached.Bc7.Span, (int)cached.Width, (int)cached.Height);
        _upscaled.AddOrUpdate(pixels, this);
        return texture with
        {
            Width = (int)cached.Width,
            Height = (int)cached.Height,
            Rgba8 = ImmutableCollectionsMarshal.AsImmutableArray(pixels),
            Blocks = default,
            UpscaleKey = key,
        };
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
