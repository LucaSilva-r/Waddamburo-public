using Waddamburo.Formats.Lmb;

namespace Waddamburo.Game.Patching;

/// <summary>
/// Adds <see cref="Slots"/> custom genres to song_select.lm (a Lumen patch): each is a copy of the J-POP
/// art under its own label (<see cref="Label"/>) in every sprite that has genre frames, recoloured by its
/// own colour-pool entry (the last <see cref="Slots"/> entries, set at runtime) with its highlights and
/// shading kept (TextureRule.Recolour), its names, description and mascot drawn from host slots
/// (<see cref="Slot"/>). The host registers the labels as genres (GenreResource.MUSICINFO_KEY) when
/// Song Select starts.
/// </summary>
public static class SongSelectGenrePatch
{
    public const int Slots = 32;
    public const string MovieName = "song_select/song_select.lm";
    private const string Source = "J-POP";

    // song_select's J-POP textures (Green): its art (folder tab, board, spine tab, centre folder, header
    // tab, background), then what names the genre.
    private static readonly uint[] Art = [423, 424, 547, 548, 555, 556, 557, 643, 704, 705, 736, 737, 772];
    // The art's colour reference: the spine (every piece is split against one colour, so they match).
    private const uint Reference = 547;
    private const uint SpineName = 642, HeaderName = 450, Description = 508, Mascot = 507;

    /// <summary>How far left of the J-POP header quad's centre a custom genre's header name sits (stage units).</summary>
    public const float HeaderShift = 8;

    public static string Label(int slot) => $"wd:{slot:00}";

    /// <summary>A custom genre's host surface slot: tate (spine name), yoko (header name), desc, image.</summary>
    public static string Slot(string part, int slot) => $"wd_{part}_{slot:00}";

    public static bool AppliesTo(LumenMovieContent content) =>
        content.Name == MovieName || content.Name.EndsWith("/" + MovieName, StringComparison.Ordinal);

    public static void Apply(LumenMovieContent content)
    {
        var editor = new LumenMovieEditor(content);
        var sprites = editor.SpritesWithLabel(Source);
        for (var slot = 0; slot < Slots; slot++)
        {
            var colour = editor.AddColorTransform(256, 256, 256);
            var current = slot;
            TextureRule? rule(uint texture) => texture switch
            {
                SpineName => new TextureRule.Fill(Slot("tate", current)),
                HeaderName => new TextureRule.Fill(Slot("yoko", current)),
                Description => new TextureRule.Fill(Slot("desc", current)),
                Mascot => new TextureRule.Fill(Slot("image", current)),
                _ when Art.Contains(texture) => new TextureRule.Recolour(colour, Reference),
                _ => null,
            };
            foreach (var sprite in sprites)
                editor.CloneLabel(sprite, Source, Label(slot), rule);
        }
        editor.Commit();
    }
}
