using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The name art of a series' challenge courses: nutdata/(cabinet)/appendable/NN/dojochallenge_(long|short|mini)
/// NUTs, concatenated in the order they shipped. The counts match the courses above level 100 in the course
/// lists (Green 28, Blue 14, Red 13), so the n-th challenge course has the n-th texture.
/// </summary>
internal sealed class DanChallengeArt(NutData nutData)
{
    private static readonly Dictionary<string, (string Cabinet, string[] Folders)> Series = new(StringComparer.Ordinal)
    {
        ["green"] = ("S11100-1", ["00", "01", "10", "11", "12"]),
        ["blue"] = ("S10100-1", ["00"]),
        ["yellow"] = ("ST9100-1", ["00", "04", "06", "08"]),
        ["red"] = ("ST8100-1", ["05", "06", "07"]),
    };

    /// <summary>A course's art, <paramref name="challenge"/> counting from 1 in its series.</summary>
    public static LumenNativeSurfaceKey Key(string series, int challenge, string kind) => new($"dan-challenge:{series}:{challenge}:{kind}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("dan-challenge:", StringComparison.Ordinal))
            return null;
        var parts = key.Value.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var challenge) || challenge <= 0
            || !Series.TryGetValue(parts[1], out var source))
            return null;
        // The n-th challenge is the n-th texture of the folders' NUTs, one after another.
        var index = challenge - 1;
        foreach (var folder in source.Folders)
        {
            var path = $"{source.Cabinet}/appendable/{folder}/dojochallenge_{parts[3]}/dojochallenge_{parts[3]}.nut";
            var count = nutData.Textures(path).Length;
            if (index < count)
                return nutData.Texture(path, index);
            index -= count;
        }
        return null;
    }
}
