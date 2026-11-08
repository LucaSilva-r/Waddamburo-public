using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Costume picker icons (entry UpdateFillrect): Green's nutdata/S11100-1/appendable/NN/costume*_icon
/// NUTs, concatenated in set order so the texture index is the costume / head / body id (the counts
/// match the model files). Decoded and uploaded on first use.
/// </summary>
internal sealed class CostumeIconTextures(SdlApplication application, NutData nutData, string dataRoot)
{
    // Entry ICONTYPE_COSTUME / _HEAD / _BODY icons, then the matching name plates.
    private static readonly string[] SetNames =
        ["costume_icon", "costume_head_icon", "costume_body_icon", "costume_name", "costume_head_name", "costume_body_name"];

    private readonly Dictionary<(int Type, int Id), RenderTextureId?> _icons = [];
    // ponytail: Green's cabinet folder; other titles keep their icons under their own id.
    private readonly string[] _folders = Directory.Exists(Path.Combine(dataRoot, "nutdata", "S11100-1", "appendable"))
        ? [.. Directory.GetDirectories(Path.Combine(dataRoot, "nutdata", "S11100-1", "appendable")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
        : [];

    public static LumenNativeSurfaceKey Key(int type, int id) => new($"costume-icon:{type}:{id}");

    /// <summary>The name plate of the same entry (sets 3-5).</summary>
    public static LumenNativeSurfaceKey NameKey(int type, int id) => Key(type + 3, id);

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        var parts = key.Value.Split(':');
        if (parts.Length != 3 || parts[0] != "costume-icon" || !int.TryParse(parts[1], out var type)
            || (uint)type >= (uint)SetNames.Length || !int.TryParse(parts[2], out var id) || id < 0)
            return null;
        // The id counts through the sets' NUTs, folder after folder.
        var index = id;
        foreach (var folder in _folders)
        {
            var path = $"S11100-1/appendable/{folder}/{SetNames[type]}/{SetNames[type]}.nut";
            var textures = nutData.Textures(path);
            if (index >= textures.Length)
            {
                index -= textures.Length;
                continue;
            }
            if (type >= 3)
                return nutData.Texture(path, index);
            // The icons (not the name plates) are pixel art: whole pixels, smoothed only in between, never upscaled.
            if (_icons.TryGetValue((type, id), out var icon))
                return icon;
            var (width, height, pixels) = PixelArt.Enlarge(textures[index].Width, textures[index].Height,
                NutTextureDecoder.DecodeRgba8(textures[index]));
            return _icons[(type, id)] = application.UploadRgba8((uint)width, (uint)height, pixels);
        }
        return null;
    }
}
