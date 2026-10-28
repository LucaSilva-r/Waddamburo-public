using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.MenuLayouts;

/// <summary>CPU-owned layout edits and replacement pixels; never writes the user's source assets.</summary>
public sealed class VerticalLayoutDocument(LmbEditDocument document, string? movieName,
    IReadOnlyList<LumenSoftwareTexture> textures)
{
    public LmbEditDocument Document { get; } = document;
    public string? MovieName { get; } = movieName;
    public IReadOnlyList<LumenSoftwareTexture> Textures { get; } = textures;
    public Dictionary<int, byte[]> Replacements { get; } = [];

    internal void ReplacePixels(int index, byte[] pixels)
    {
        var texture = Textures[index];
        if (pixels.Length != checked(texture.Width * texture.Height * 4))
            throw new ArgumentException("Replacement pixels must keep the original dimensions.", nameof(pixels));
        Replacements[index] = pixels;
    }
}
