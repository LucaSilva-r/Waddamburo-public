using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game;

/// <summary>Source-independent, CPU-owned content for one archive movie.</summary>
public sealed class LumenMovieContent
{
    private LumenMovieContent(
        string name,
        LmbMovieDefinition definition,
        ImmutableArray<LumenTextureContent> textures,
        ImmutableArray<ParseDiagnostic> diagnostics)
    {
        Name = name;
        Definition = definition;
        Textures = textures;
        Diagnostics = diagnostics;
    }

    public string Name { get; }

    public LmbMovieDefinition Definition { get; private set; }

    public ImmutableArray<LumenTextureContent> Textures { get; private set; }

    public ImmutableArray<ParseDiagnostic> Diagnostics { get; }

    /// <param name="movie">The movie in its archive.</param>
    /// <param name="limits">Parser limits (the defaults when null).</param>
    /// <param name="keepCompressed">BC1/BC3 textures (sizes multiples of 4) stay as their blocks for the GPU,
    /// slices of the archive's buffer (<see cref="LumenTextureContent.Blocks"/>, alive until uploaded): no decode,
    /// no copy, a quarter of the memory.
    /// <see cref="LumenTextureContent.Pixels"/> decodes one when pixels are needed.</param>
    public static LumenMovieContent Load(DdpMovieView movie, ParserLimits? limits = null, bool keepCompressed = false)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var parserLimits = limits ?? ParserLimits.Default;
        var textures = ImmutableArray.CreateBuilder<LumenTextureContent>(movie.Textures.Length);
        foreach (var view in movie.Textures)
        {
            var pack = NutFile.Parse(view.Data, parserLimits);
            if (pack.Textures.Length != 1)
            {
                throw new FormatReadException(
                    $"Movie texture entry {view.Entry.Index} contains {pack.Textures.Length} NTP3 textures; the initial Green profile requires exactly one",
                    view.Entry.Offset);
            }
            textures.Add(LumenTextureContent.FromNut(view.Entry.Index - movie.Entry.TextureBegin, pack.Textures[0],
                keepCompressed, parserLimits));
        }

        var file = LmbFile.Parse(movie.Data, parserLimits);
        var semantic = LmbSemanticReader.Read(
            file,
            parserLimits,
            new LmbSemanticValidationContext(textures.Count));
        return new LumenMovieContent(
            movie.Entry.Name,
            semantic.Value,
            textures.MoveToImmutable(),
            semantic.Diagnostics);
    }

    /// <summary>
    /// A movie package (a folder with movie.json and its PNG textures; docs/development/free-movies-plan.md),
    /// loaded into the same content an archive movie gives.
    /// </summary>
    /// <param name="directory">The package folder; its name is the movie's.</param>
    /// <param name="decodeImage">Decodes a PNG file's bytes to straight RGBA rows; null when it cannot.</param>
    /// <param name="limits">Parser limits (the defaults when null).</param>
    /// <param name="scriptErrors">Given: scripts with errors are left out and reported here (see MoviePackage.Assemble).</param>
    /// <param name="movieJson">Optional working JSON for an unsaved editor preview.</param>
    /// <param name="readFile">Optional working asset reader; receives validated package-relative paths.</param>
    public static LumenMovieContent LoadPackage(string directory,
        Func<byte[], (int Width, int Height, byte[] Rgba)?> decodeImage, ParserLimits? limits = null,
        ICollection<Formats.Package.ActionScriptException>? scriptErrors = null, string? movieJson = null,
        Func<string, byte[]>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(decodeImage);
        var parserLimits = limits ?? ParserLimits.Default;
        var root = Path.GetFullPath(directory);
        var assembled = Formats.Package.MoviePackage.Assemble(
            movieJson ?? File.ReadAllText(Path.Combine(root, Formats.Package.MoviePackage.FileName)),
            readScript, scriptErrors);
        string readScript(string file)
        {
            using var stream = new MemoryStream(read(file));
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        byte[] read(string file)
        {
            var path = inside(file);
            if (readFile is null && new FileInfo(path).Length > parserLimits.MaxFileBytes)
                throw new InvalidDataException($"Movie package '{root}': file is too large: {file}");
            var bytes = readFile is null ? File.ReadAllBytes(path) : readFile(file);
            if (bytes.LongLength > parserLimits.MaxFileBytes)
                throw new InvalidDataException($"Movie package '{root}': file is too large: {file}");
            return bytes;
        }
        // A shared package is untrusted: its files stay inside its folder.
        string inside(string file)
        {
            var path = Path.GetFullPath(file, root);
            return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path
                : throw new InvalidDataException($"Movie package '{root}': a file path leaves the package: {file}");
        }
        var textures = ImmutableArray.CreateBuilder<LumenTextureContent>(assembled.TextureFiles.Length);
        foreach (var file in assembled.TextureFiles)
        {
            var image = decodeImage(read(file))
                ?? throw new InvalidDataException($"Movie package '{root}': texture is not a readable PNG: {file}");
            textures.Add(new LumenTextureContent(textures.Count, image.Width, image.Height,
                ImmutableCollectionsMarshal.AsImmutableArray(image.Rgba)));
        }
        var semantic = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb, parserLimits), parserLimits,
            new LmbSemanticValidationContext(textures.Count));
        var name = Path.GetFileName(root);
        return new LumenMovieContent($"{name}/{name}.lm", semantic.Value, textures.MoveToImmutable(), semantic.Diagnostics);
    }

    /// <summary>A movie that shows nothing and answers nothing: the stand-in for one that exists nowhere.</summary>
    public static LumenMovieContent Empty(string name)
    {
        var assembled = Formats.Package.MoviePackage.Assemble("""{ "format": 1, "root": "main", "sprites": { "main": { "frames": 1 } } }""");
        var semantic = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb), validationContext: new LmbSemanticValidationContext(0));
        return new LumenMovieContent(name, semantic.Value, [], semantic.Diagnostics) { IsStandIn = true };
    }

    /// <summary>Whether the movie has semantic errors (it would not load), without decoding its textures.</summary>
    public static bool HasErrors(DdpMovieView movie, ParserLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var parserLimits = limits ?? ParserLimits.Default;
        return LmbSemanticReader.Read(LmbFile.Parse(movie.Data, parserLimits), parserLimits,
                new LmbSemanticValidationContext(movie.Textures.Length))
            .Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    public LumenPlayer CreatePlayer(
        float stageWidth = 1280,
        float stageHeight = 720,
        ILumenHostBinding? hostBinding = null,
        Func<string, LumenLoadedMovie?>? movieLoader = null) =>
        new(Definition, stageWidth, stageHeight, hostBinding: hostBinding, movieLoader: movieLoader) { Name = Name, StandIn = IsStandIn };

    /// <summary>Made by <see cref="Empty"/>: its players accept any callback.</summary>
    public bool IsStandIn { get; private init; }

    /// <summary>Replaces textures before upload (upscaled copies; UVs are normalised, so any size fits).</summary>
    public void ReplaceTextures(Func<LumenTextureContent, int, LumenTextureContent> replace)
    {
        ArgumentNullException.ThrowIfNull(replace);
        Textures = [.. Textures.Select(replace)];
    }

    /// <summary>A patched definition and texture set (see Patching.LumenMovieEditor), before upload.</summary>
    public void Replace(LmbMovieDefinition definition, ImmutableArray<LumenTextureContent> textures)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        Textures = textures;
    }

    /// <summary>Releases decoded pixels after a renderer has uploaded every texture.</summary>
    public void ReleaseDecodedTexturePixels() =>
        Textures = [.. Textures.Select(static texture => texture with { Rgba8 = [], Bc7 = default, Blocks = default })];

    /// <summary>Every texture as RGBA pixels (a patch that edits pixels needs them all).</summary>
    public void DecodeAll() =>
        Textures = [.. Textures.Select(static texture => texture.Blocks.IsEmpty ? texture
            : texture with { Rgba8 = ImmutableCollectionsMarshal.AsImmutableArray(texture.Pixels()), Blocks = default })];
}

/// <summary>The GPU block formats a game texture can stay in (NTP3's own).</summary>
public enum LumenBlockFormat { Bc1, Bc3 }

public sealed record LumenTextureContent(
    int Index,
    int Width,
    int Height,
    ImmutableArray<byte> Rgba8)
{
    /// <summary>
    /// BC7 blocks (one byte per pixel) given in place of <see cref="Rgba8"/> (an upscaled copy from
    /// the cache); empty when the texture is RGBA.
    /// </summary>
    public ReadOnlyMemory<byte> Bc7 { get; init; }

    /// <summary>
    /// The texture's own BC1/BC3 blocks, kept in place of <see cref="Rgba8"/> (a slice of its archive's buffer: hold
    /// a copy, not this, past the upload); empty otherwise.
    /// </summary>
    public ReadOnlyMemory<byte> Blocks { get; init; }

    /// <summary>The format of <see cref="Blocks"/>.</summary>
    public LumenBlockFormat BlockFormat { get; init; }

    /// <summary>The upscale cache's name for this texture, when already known (it travels to the upload).</summary>
    public string? UpscaleKey { get; init; }

    /// <summary>
    /// One NUT texture: its BC1/BC3 blocks as they are when <paramref name="keepCompressed"/> (and whole blocks),
    /// RGBA otherwise. Movies' textures and the game's nutdata art alike.
    /// </summary>
    public static LumenTextureContent FromNut(int index, NutTexture texture, bool keepCompressed, ParserLimits? limits = null)
    {
        if (keepCompressed && texture.Format is NutPixelFormat.Bc1 or NutPixelFormat.Bc3
            && texture.Width % 4 == 0 && texture.Height % 4 == 0)
        {
            var format = texture.Format == NutPixelFormat.Bc1 ? LumenBlockFormat.Bc1 : LumenBlockFormat.Bc3;
            var length = texture.Width / 4 * (texture.Height / 4) * (format == LumenBlockFormat.Bc1 ? 8 : 16);
            if (texture.Data.Length >= length)
                return new LumenTextureContent(index, texture.Width, texture.Height, [])
                    { Blocks = texture.Data[..length], BlockFormat = format };
        }
        return new LumenTextureContent(index, texture.Width, texture.Height,
            ImmutableCollectionsMarshal.AsImmutableArray(NutTextureDecoder.DecodeRgba8(texture, limits ?? ParserLimits.Default)));
    }

    /// <summary>Decodes BC7 blocks to RGBA (the renderer's native library; set by the game).</summary>
    public static Func<ReadOnlyMemory<byte>, int, int, byte[]>? Bc7Decoder { get; set; }

    /// <summary>
    /// The texture's RGBA pixels: <see cref="Rgba8"/> itself, or decoded (a new array) from <see cref="Blocks"/> or
    /// from <see cref="Bc7"/> (an upscale, at its own size).
    /// </summary>
    public byte[] Pixels()
    {
        if (!Rgba8.IsDefaultOrEmpty)
            return ImmutableCollectionsMarshal.AsArray(Rgba8)!;
        if (!Bc7.IsEmpty && Bc7Decoder is { } decode)
            return decode(Bc7, Width, Height);
        if (Blocks.IsEmpty)
            throw new InvalidOperationException($"Texture {Index} has no pixels to decode.");
        return BlockFormat == LumenBlockFormat.Bc1
            ? BlockCompressionDecoder.DecodeBc1(Blocks.Span, Width, Height)
            : BlockCompressionDecoder.DecodeBc3(Blocks.Span, Width, Height);
    }
}
