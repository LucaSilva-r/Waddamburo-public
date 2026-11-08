using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// waiwai_result's native fills: dummy_bg (the collabo's bg.nut) and dummy_sentence (one of text.nut's
/// 70 comments), dummy_rare_onp_0N (rareonp.nut, the collabo's rare note). Decoded and uploaded on first use.
/// </summary>
internal sealed class WaiwaiResultTextures(NutData nutData)
{
    public static readonly LumenNativeSurfaceKey Background = new("waiwai-result:bg");
    public static readonly LumenNativeSurfaceKey RareNote = new("waiwai-result:rare");

    // ponytail: the stock collabo (00_taiko) only; the licensed collabos (01_A3 ...) sit next to it.
    private const string Folder = "S11100-1/waiwaicollabo/00_taiko/";

    public static LumenNativeSurfaceKey Sentence(int index) => new($"waiwai-result:text:{index}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key) =>
        !key.Value.StartsWith("waiwai-result:", StringComparison.Ordinal) ? null
        : key == Background ? nutData.Texture(Folder + "bg.nut", 0)
        : key == RareNote ? nutData.Texture(Folder + "rareonp.nut", 0)
        : int.TryParse(key.Value["waiwai-result:text:".Length..], out var index) ? nutData.Texture(Folder + "text.nut", index)
        : null;
}
