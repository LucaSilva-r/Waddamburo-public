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
internal sealed class CostumeIconTextures(SdlApplication application, string dataRoot)
{
    // Entry ICONTYPE_COSTUME / _HEAD / _BODY icons, then the matching name plates.
    private static readonly string[] SetNames =
        ["costume_icon", "costume_head_icon", "costume_body_icon", "costume_name", "costume_head_name", "costume_body_name"];

    private readonly SdlApplication _application = application;
    private readonly List<NutTexture>?[] _icons = new List<NutTexture>?[SetNames.Length];
    private readonly Dictionary<(int Type, int Id), RenderTextureId> _uploaded = [];
    // ponytail: Green's cabinet folder; other titles keep their icons under their own id.
    private readonly string _appendable = Path.Combine(dataRoot, "nutdata", "S11100-1", "appendable");

    public static LumenNativeSurfaceKey Key(int type, int id) => new($"costume-icon:{type}:{id}");

    /// <summary>The name plate of the same entry (sets 3-5).</summary>
    public static LumenNativeSurfaceKey NameKey(int type, int id) => Key(type + 3, id);

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        var parts = key.Value.Split(':');
        if (parts.Length != 3 || parts[0] != "costume-icon"
            || !int.TryParse(parts[1], out var type) || !int.TryParse(parts[2], out var id))
            return null;
        if (_uploaded.TryGetValue((type, id), out var texture))
            return texture;
        if (type < 0 || type >= SetNames.Length || icons(type) is not { } set || (uint)id >= (uint)set.Count)
            return null;
        var icon = set[id];
        return _uploaded[(type, id)] = _application.UploadRgba8(icon.Width, icon.Height, NutTextureDecoder.DecodeRgba8(icon));
    }

    private List<NutTexture>? icons(int type)
    {
        if (_icons[type] is { } loaded)
            return loaded;
        if (!Directory.Exists(_appendable))
            return null;
        var set = new List<NutTexture>();
        foreach (var folder in Directory.GetDirectories(_appendable).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, SetNames[type], SetNames[type] + ".nut");
            if (File.Exists(file))
                set.AddRange(NutFile.Parse(File.ReadAllBytes(file)).Textures);
        }
        return _icons[type] = set;
    }
}
