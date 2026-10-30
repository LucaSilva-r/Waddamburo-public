using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The name art of a series' challenge courses: nutdata/(cabinet)/appendable/NN/dojochallenge_(long|short|mini)
/// NUTs, concatenated in the order they shipped. The counts match the courses above level 100 in the course
/// lists (Green 28, Blue 14, Red 13), so the n-th challenge course has the n-th texture.
/// </summary>
internal sealed class DanChallengeArt(SdlApplication application, string dataRoot)
{
    private static readonly Dictionary<string, (string Cabinet, string[] Folders)> Series = new(StringComparer.Ordinal)
    {
        ["green"] = ("S11100-1", ["00", "01", "10", "11", "12"]),
        ["blue"] = ("S10100-1", ["00"]),
        ["yellow"] = ("ST9100-1", ["00", "04", "06", "08"]),
        ["red"] = ("ST8100-1", ["05", "06", "07"]),
    };

    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId?> _uploaded = [];
    private readonly Dictionary<(string, string), List<NutTexture>> _sets = [];

    /// <summary>A course's art, <paramref name="challenge"/> counting from 1 in its series.</summary>
    public static LumenNativeSurfaceKey Key(string series, int challenge, string kind) => new($"dan-challenge:{series}:{challenge}:{kind}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("dan-challenge:", StringComparison.Ordinal))
            return null;
        if (_uploaded.TryGetValue(key, out var texture))
            return texture;
        var parts = key.Value.Split(':');
        RenderTextureId? uploaded = null;
        if (parts.Length == 4 && int.TryParse(parts[2], out var challenge) && challenge > 0
            && set(parts[1], parts[3]) is { } textures && challenge <= textures.Count)
        {
            var art = textures[challenge - 1];
            uploaded = application.UploadRgba8((uint)art.Width, (uint)art.Height, NutTextureDecoder.DecodeRgba8(art));
        }
        return _uploaded[key] = uploaded;
    }

    private List<NutTexture>? set(string series, string kind)
    {
        if (_sets.TryGetValue((series, kind), out var loaded))
            return loaded;
        if (!Series.TryGetValue(series, out var source))
            return null;
        var textures = new List<NutTexture>();
        foreach (var folder in source.Folders)
        {
            var file = Path.Combine(dataRoot, "nutdata", source.Cabinet, "appendable", folder, $"dojochallenge_{kind}", $"dojochallenge_{kind}.nut");
            if (File.Exists(file))
                textures.AddRange(NutFile.Parse(File.ReadAllBytes(file)).Textures);
        }
        return _sets[(series, kind)] = textures;
    }
}
