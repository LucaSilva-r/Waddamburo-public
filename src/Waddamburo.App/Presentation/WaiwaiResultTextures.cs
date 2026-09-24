using System.Collections.Immutable;
using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

/// <summary>
/// waiwai_result's native fills: dummy_bg (the collabo's bg.nut) and dummy_sentence (one of text.nut's
/// 70 comments), dummy_rare_onp_0N (rareonp.nut, the collabo's rare note). Decoded and uploaded on first use.
/// </summary>
internal sealed class WaiwaiResultTextures(SdlApplication application, string dataRoot)
{
    public static readonly LumenNativeSurfaceKey Background = new("waiwai-result:bg");
    public static readonly LumenNativeSurfaceKey RareNote = new("waiwai-result:rare");

    // ponytail: the stock collabo (00_taiko) only; the licensed collabos (01_A3 ...) sit next to it.
    private readonly string _folder = Path.Combine(dataRoot, "nutdata", "S11100-1", "waiwaicollabo", "00_taiko");
    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId> _uploaded = [];
    private ImmutableArray<NutTexture>? _sentences;

    public static LumenNativeSurfaceKey Sentence(int index) => new($"waiwai-result:text:{index}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("waiwai-result:", StringComparison.Ordinal))
            return null;
        if (_uploaded.TryGetValue(key, out var texture))
            return texture;
        NutTexture? source = null;
        if (key == Background)
            source = load("bg.nut") is [var bg, ..] ? bg : null;
        else if (key == RareNote)
            source = load("rareonp.nut") is [var rare, ..] ? rare : null;
        else if (int.TryParse(key.Value["waiwai-result:text:".Length..], out var index)
                 && (_sentences ??= load("text.nut")) is { } sentences && (uint)index < (uint)sentences.Length)
            source = sentences[index];
        if (source is null)
            return null;
        return _uploaded[key] = application.UploadRgba8(source.Width, source.Height, NutTextureDecoder.DecodeRgba8(source));
    }

    private ImmutableArray<NutTexture> load(string name)
    {
        var file = Path.Combine(_folder, name);
        return File.Exists(file) ? NutFile.Parse(File.ReadAllBytes(file)).Textures : [];
    }
}
