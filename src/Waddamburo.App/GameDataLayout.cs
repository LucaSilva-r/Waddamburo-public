/// <summary>Resolves the stable paths used by a normal game boot from one USRDIR root.</summary>
internal sealed record GameDataLayout(
    string Root,
    string LumenRoot,
    string DonRoot,
    string TjaRoot,
    string SoundRoot,
    string FontPath)
{
    public static GameDataLayout Resolve(string root, string? fontOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        requireDirectory(fullRoot, "game data root");

        var lumen = Path.Combine(fullRoot, "data", "lumendata", "packed");
        var don = Path.Combine(fullRoot, "data", "don3d");
        var tja = Path.Combine(fullRoot, "custom_songs");
        var sound = Path.Combine(fullRoot, "data", "sound");
        requireDirectory(lumen, "Lumen asset root");
        requireDirectory(don, "Don asset root");
        requireDirectory(tja, "custom-song root");
        requireDirectory(sound, "sound root");

        var font = fontOverride is null ? findTitleFont(fullRoot) : Path.GetFullPath(fontOverride);
        if (!File.Exists(font))
            throw new FileNotFoundException($"The title font does not exist: {font}", font);
        return new GameDataLayout(fullRoot, lumen, don, tja, sound, font);
    }

    private static string findTitleFont(string root)
    {
        var fontDirectory = Path.Combine(root, "data", "font");
        if (Directory.Exists(fontDirectory))
        {
            var preferredNames = new[] { "font.ttf", "font.otf", "font.ttc" };
            foreach (var name in preferredNames)
            {
                var candidate = Path.Combine(fontDirectory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            var suppliedFont = Directory.EnumerateFiles(fontDirectory)
                .Where(path => isSupportedFontExtension(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (suppliedFont is not null)
                return suppliedFont;
        }

        return findSystemFont();
    }

    private static bool isSupportedFontExtension(string extension)
        => extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase);

    private static void requireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The {description} does not exist: {path}");
    }

    private static string findSystemFont()
    {
        var candidates = OperatingSystem.IsWindows()
            ? windowsFonts()
            : OperatingSystem.IsMacOS()
                ? [
                    "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
                    "/System/Library/Fonts/Helvetica.ttc",
                ]
                : [
                    "/usr/share/fonts/google-noto-sans-cjk-vf-fonts/NotoSansCJK-VF.ttc",
                    "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
                    "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
                    "/usr/share/fonts/opentype/ipafont-gothic/ipag.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "No supported system title font was found. Install Noto Sans CJK or pass --font=/path/to/font.");
    }

    private static string[] windowsFonts()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return [
            Path.Combine(directory, "YuGothM.ttc"),
            Path.Combine(directory, "meiryo.ttc"),
            Path.Combine(directory, "msgothic.ttc"),
            Path.Combine(directory, "arial.ttf"),
        ];
    }
}
