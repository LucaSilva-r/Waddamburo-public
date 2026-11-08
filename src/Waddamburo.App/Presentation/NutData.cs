using System.Collections.Immutable;
using Waddamburo.Formats.Nut;
using Waddamburo.Game;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The game's nutdata textures the host draws itself (tone names, dan plates, credits, costume plates, Waiwai
/// results): read once per file and uploaded like the movies' textures, through the same path (the game's own
/// BC1/BC3 blocks, cached upscales, queued for the upscaler).
/// </summary>
internal sealed class NutData(SdlApplication application, string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "nutdata");
    private readonly Dictionary<string, ImmutableArray<NutTexture>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, int), RenderTextureId?> _uploaded = [];

    /// <summary>
    /// Swaps a texture for its cached upscale before upload (set with the upscaler; null: upscaling off).
    /// </summary>
    public static Func<LumenTextureContent, LumenTextureContent>? Cached { get; set; }

    /// <summary>
    /// The textures of <paramref name="path"/> (relative to nutdata): a .nut's, or a nutdatapack's NUTs' in order.
    /// Empty when the file is missing or unreadable.
    /// </summary>
    // ponytail: a pack (NUT_PACK_TYPE1) is taken as its NTP3 textures in order, not by its index's names and offsets.
    public ImmutableArray<NutTexture> Textures(string path)
    {
        if (_files.TryGetValue(path, out var loaded))
            return loaded;
        var textures = ImmutableArray<NutTexture>.Empty;
        try
        {
            var file = Path.Combine(_root, path);
            if (File.Exists(file))
                textures = read(File.ReadAllBytes(file));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or UnauthorizedAccessException or Waddamburo.Formats.IO.FormatReadException)
        {
            Console.Error.WriteLine($"Warning NUTDATA: {path}: {exception.Message}");
        }
        return _files[path] = textures;
    }

    /// <summary>Texture <paramref name="index"/> of <paramref name="path"/> on the GPU (uploaded once), or null.</summary>
    public RenderTextureId? Texture(string path, int index)
    {
        if (_uploaded.TryGetValue((path, index), out var cached))
            return cached;
        var textures = Textures(path);
        RenderTextureId? uploaded = null;
        if ((uint)index < (uint)textures.Length)
        {
            var content = LumenTextureContent.FromNut(index, textures[index], keepCompressed: true);
            uploaded = SceneTextures.UploadOne(application, Cached?.Invoke(content) ?? content);
        }
        return _uploaded[(path, index)] = uploaded;
    }

    private static ImmutableArray<NutTexture> read(byte[] data)
    {
        if (!data.AsSpan().StartsWith("NUT_PACK"u8))
            return NutFile.Parse(data).Textures;
        var builder = ImmutableArray.CreateBuilder<NutTexture>();
        var starts = new List<int>();
        for (var offset = data.AsSpan().IndexOf("NTP3"u8); offset >= 0;)
        {
            starts.Add(offset);
            var next = data.AsSpan(offset + 4).IndexOf("NTP3"u8);
            offset = next < 0 ? -1 : offset + 4 + next;
        }
        for (var index = 0; index < starts.Count; index++)
        {
            var end = index + 1 < starts.Count ? starts[index + 1] : data.Length;
            builder.AddRange(NutFile.Parse(data.AsMemory(starts[index], end - starts[index])).Textures);
        }
        return builder.ToImmutable();
    }
}
