using System.Text;

namespace Waddamburo.Editor;

/// <summary>The editor's embedded Lucide subset. Originals and license are recorded in the provenance notes.</summary>
public static class EditorIcons
{
    public static readonly string[] Names = 
    [
        "folder", "image", "shapes", "clapperboard", "type",
        "skip-back", "step-back", "play", "pause", "step-forward", "skip-forward",
        "eye", "eye-off", "move", "rotate-cw", "scaling", "maximize", "file-code", "plus", "search", "circle-dot", "square-dashed-mouse-pointer",
    ];

    public static byte[] Svg(string name)
    {
        if (!Names.Contains(name)) throw new ArgumentException("Unknown editor icon.", nameof(name));
        using var stream = typeof(EditorIcons).Assembly.GetManifestResourceStream($"Waddamburo.Editor.Lucide.{name}.svg")
            ?? throw new InvalidOperationException($"Missing editor icon: {name}.");
        using var reader = new StreamReader(stream);
        return Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("currentColor", "#ffffff", StringComparison.Ordinal)
            .Replace("width=\"24\"", "width=\"64\"", StringComparison.Ordinal)
            .Replace("height=\"24\"", "height=\"64\"", StringComparison.Ordinal));
    }
}
