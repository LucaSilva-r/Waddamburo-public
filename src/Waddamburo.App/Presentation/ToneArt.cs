using Waddamburo.Formats.Nut;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The drum sounds' (音色) names and icons for Song Select's tone board (<see cref="ToneSurfaces"/>): the
/// game's nutdata/tone_name and tone_icon packs, read the first time one is drawn.
/// </summary>
/// <remarks>
/// ponytail: a pack (NUT_PACK_TYPE1) is taken as its NTP3 textures in order (tone_name_000-005,
/// icon_neiro_001-005), not by its index's names and offsets.
/// </remarks>
internal sealed class ToneArt(SdlApplication application, string dataRoot) : IDisposable
{
    private readonly Dictionary<string, RenderTextureId?> _uploaded = [];

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!ToneSurfaces.TryParse(key, out var icon, out var tone))
            return null;
        var name = $"{(icon ? "icon" : "name")}:{tone}";
        if (_uploaded.TryGetValue(name, out var cached))
            return cached;
        // Icons start at tone 1 (the plain drum has none).
        var texture = load(icon ? "tone_icon" : "tone_name", icon ? tone - 1 : tone);
        return _uploaded[name] = texture is null ? null
            : application.UploadRgba8((uint)texture.Width, (uint)texture.Height, NutTextureDecoder.DecodeRgba8(texture));
    }

    private NutTexture? load(string pack, int index)
    {
        try
        {
            var data = File.ReadAllBytes(Path.Combine(dataRoot, "nutdata", pack, "nutdatapack.ndp"));
            var starts = new List<int>();
            for (var offset = data.AsSpan().IndexOf("NTP3"u8); offset >= 0;)
            {
                starts.Add(offset);
                var next = data.AsSpan(offset + 4).IndexOf("NTP3"u8);
                offset = next < 0 ? -1 : offset + 4 + next;
            }
            if ((uint)index >= (uint)starts.Count)
                return null;
            var end = index + 1 < starts.Count ? starts[index + 1] : data.Length;
            return NutFile.Parse(data.AsMemory(starts[index], end - starts[index])).Textures[0];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Drum sound art unavailable ({pack}): {exception.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var texture in _uploaded.Values.OfType<RenderTextureId>())
            application.ReleaseTexture(texture);
    }
}
