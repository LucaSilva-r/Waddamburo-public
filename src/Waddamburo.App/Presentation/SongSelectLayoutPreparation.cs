using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Rendering;
using Waddamburo.MenuLayouts;

namespace Waddamburo.App.Presentation;

/// <summary>Derives the optional menu from installed assets; caches only on the user's PC.</summary>
internal static class SongSelectLayoutPreparation
{
    private const int CacheVersion = 3;
    private static readonly ConcurrentDictionary<string, Lazy<Prepared>> Memory = new();
    private sealed record Prepared(byte[] Movie, Dictionary<int, byte[]> Replacements);

    public static void Apply(DdpMovieView movie, LumenMovieContent content, ArcadeSettings settings,
        string font, string? cacheRoot)
    {
        if (settings.SongSelectScroll != SongSelectScrollMode.Vertical
            || content.Name is not ("song_select/song_select.lm" or "waiwai_song_select/waiwai_song_select.lm")
            || content.Definition.Strings.Any(s => s.Value == "wd_vertical_rows_v2")) return;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(movie.Data.Span);
        foreach (var texture in movie.Textures) hash.AppendData(texture.Data.Span);
        hash.AppendData(File.ReadAllBytes(font));
        var english = Strings.Language != "ja";
        var key = $"v{CacheVersion}-{(english ? "en" : "ja")}-" + Convert.ToHexString(hash.GetHashAndReset());
        var cache = cacheRoot is null ? null : Path.Combine(cacheRoot, "menu-layouts", key + ".bin");
        var watch = Stopwatch.StartNew();
        var prepared = Memory.GetOrAdd(key, _ => new Lazy<Prepared>(() =>
            read(cache, content) ?? build(movie, content, font, cache, english))).Value;
        var definition = LmbSemanticReader.Read(LmbFile.Parse(prepared.Movie), validationContext:
            new LmbSemanticValidationContext(content.Textures.Length));
        if (definition.Diagnostics.Any(d => d.Severity == Waddamburo.Formats.Diagnostics.DiagnosticSeverity.Error))
            throw new InvalidDataException("The generated Song Select layout is invalid.");
        content.Replace(definition.Value, [.. content.Textures.Select((texture, index) =>
            prepared.Replacements.TryGetValue(index, out var pixels)
                ? texture with { Rgba8 = ImmutableCollectionsMarshal.AsImmutableArray(pixels) } : texture)]);
        Console.WriteLine($"Song Select layout: vertical/{settings.VerticalScrollPath.ToString().ToLowerInvariant()} "
            + $"({content.Name}), {watch.ElapsedMilliseconds} ms.");
    }

    private static Prepared build(DdpMovieView movie, LumenMovieContent content, string font, string? cache, bool english)
    {
        Console.WriteLine($"Preparing vertical Song Select for the first time ({content.Name})...");
        var document = new VerticalLayoutDocument(new LmbEditDocument(movie.Data.Span), content.Name,
            content.Textures.Select(t => new LumenSoftwareTexture(t.Width, t.Height,
                ImmutableCollectionsMarshal.AsArray(t.Rgba8)!)).ToArray());
        VerticalSongSelectLayout.Apply(document, font, english);
        var prepared = new Prepared(document.Document.Data.ToArray(), document.Replacements);
        if (cache is not null)
        {
            var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                using (var file = File.Create(temporary))
                using (var writer = new BinaryWriter(file))
                {
                    writer.Write(CacheVersion);
                    writer.Write(prepared.Movie.Length);
                    writer.Write(prepared.Movie);
                    writer.Write(prepared.Replacements.Count);
                    foreach (var (index, pixels) in prepared.Replacements)
                    {
                        writer.Write(index); writer.Write(pixels.Length); writer.Write(pixels);
                    }
                }
                File.Move(temporary, cache, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Song Select layout cache unavailable: {e.Message}");
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return prepared;
    }

    private static Prepared? read(string? cache, LumenMovieContent content)
    {
        if (cache is null || !File.Exists(cache)) return null;
        try
        {
            using var file = File.OpenRead(cache);
            if (file.Length > 128 * 1024 * 1024) return null;
            using var reader = new BinaryReader(file);
            if (reader.ReadInt32() != CacheVersion) return null;
            var length = reader.ReadInt32();
            if (length < 1 || length > file.Length - file.Position) return null;
            var movie = reader.ReadBytes(length);
            var count = reader.ReadInt32();
            if (count < 0 || count > content.Textures.Length) return null;
            var replacements = new Dictionary<int, byte[]>();
            for (var i = 0; i < count; i++)
            {
                var index = reader.ReadInt32(); var size = reader.ReadInt32();
                if ((uint)index >= (uint)content.Textures.Length) return null;
                var texture = content.Textures[index];
                if (size != checked(texture.Width * texture.Height * 4) || size > file.Length - file.Position) return null;
                if (!replacements.TryAdd(index, reader.ReadBytes(size))) return null;
            }
            if (file.Position != file.Length) return null;
            var definition = LmbSemanticReader.Read(LmbFile.Parse(movie), validationContext:
                new LmbSemanticValidationContext(content.Textures.Length));
            if (definition.Diagnostics.Any(d => d.Severity == Waddamburo.Formats.Diagnostics.DiagnosticSeverity.Error)
                || !definition.Value.Strings.Any(s => s.Value == "wd_vertical_rows_v2")) return null;
            return new(movie, replacements);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Ignoring Song Select layout cache: {e.Message}");
            return null;
        }
    }
}
