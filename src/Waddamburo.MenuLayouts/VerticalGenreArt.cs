using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.MenuLayouts;

/// <summary>Reflows the existing genre illustration and caption slots without enlarging the illustrations.</summary>
internal static class VerticalGenreArt
{
    // The supplied Green archive's English captions, rewrapped into the horizontal text area.
    private static readonly Dictionary<uint, string> Captions = new()
    {
        [0] = "Learn how to play the game!!",
        [5] = "Choose between the latest hits and the most popular songs!!",
        [10] = "Play your favorite Anime songs!!",
        [15] = "Choose between exciting, upbeat and fun songs!!",
        [20] = "Choose between various songs that everyone knows!!",
        [25] = "Play famous Classical songs from the past!!",
        [30] = "Play the songs from your favorite games!!",
        [35] = "Play the original songs from the Taiko series!!",
        [40] = "Play songs that everyone likes!!",
        [45] = "Play your favorite songs!!",
        [50] = "Choose between your recently played songs!!",
        [55] = "Complete objectives and test your skills!!",
        [60] = "Complete objectives and test your skills!!",
        [65] = "Play your favorite Vocaloid songs!!",
        [70] = "Complete daily songs suited to your skill level!!",
        [75] = "Complete daily songs suited to your skill level!!",
        [85] = "Practice for the Dan-i Dojo based on your skill level!!",
        [90] = "Choose songs by difficulty and ★ ratings!!",
        [95] = "Learn how to play Training Mode!!",
        [100] = "Team up with a friend for high energy drumming!!",
    };

    public static int Apply(VerticalLayoutDocument session, string font, VerticalSongSelectProfile profile, bool english)
    {
        var definition = session.Document.Definition;
        var shapes = definition.Shapes.ToDictionary(s => s.CharacterId);
        (int Width, int Height)? dimensions(uint id) => shapes.TryGetValue(id, out var shape) && shape.Geometry.Length == 1
            ? (session.Textures[(int)shape.Geometry[0].TextureIndex].Width,
               session.Textures[(int)shape.Geometry[0].TextureIndex].Height) : null;
        session.Document.MapPlacementMatrices(profile.GenreArt, (_, p, m) => dimensions(p.CharacterId) switch
        {
            (192, 360) => m with { X = -300, Y = 0 },
            (160, 400) => new(0, 1.2f, -1.2f, 0, 170, -35),
            _ => m,
        });
        session.Document.MapPlacementMatrices(profile.GenreDetail, (_, p, m) => p.Depth is 1 or 2
            ? new(0, -.55f / 1.1f, .55f / .65f, 0, 235.77f, 236.36f) : m);
        session.Document.MapPlacementMatrices(profile.CategoryAccessory, (_, p, m) => dimensions(p.CharacterId) == (88, 24)
            ? new(0, -.8f / 1.1f, .8f / .65f, 0, -45, 442) : m);
        session.Document.NamePlacements(profile.CategoryAccessory,
            p => dimensions(p.CharacterId) == (88, 24) ? "wd_legacy_spine_tab" : null);
        // Keep icons and score markers upright in both menus. Waiwai's 500-song badge
        // belongs to this group too, rather than to the spine's title/accessory clip.
        session.Document.MapPlacementMatrices(profile.Board, (frame, p, m) =>
            (frame < 20 && p.Depth == 2) || (frame >= 20 && p.Depth == 1)
                ? new(0, -.55f / 1.1f, .55f / .65f, 0, 42, 0) : m);
        session.Document.MapPlacementMatrices(profile.FolderAccessory, (_, p, m) =>
            definition.Strings[(int)p.NameStringIndex].Value switch
            {
                "crown_" => m with { X = -20, Y = -16 },
                "rank_" => m with { X = -20, Y = 18 },
                _ => m,
            });

        var changed = new HashSet<uint>();
        foreach (var (frame, authoredCaption) in Captions.Append(new KeyValuePair<uint, string>(105, "Learn how to play Waiwai mode!!"))
                     .Where(pair => profile.IsWaiwai || pair.Key != 105))
        {
            var caption = profile.IsWaiwai && frame == 100
                ? "Play the normal game with over 500 songs to choose from!!" : authoredCaption;
            var notice = frame is 55 or 60 or 70 or 75;
            var muted = frame is 60 or 75;
            foreach (var place in session.Document.Placements(profile.GenreArt, (int)frame))
            {
                if (dimensions(place.CharacterId) != (160, 400)) continue;
                var texture = shapes[place.CharacterId].Geometry[0].TextureIndex;
                if (!changed.Add(texture)) continue;
                var bodyHeight = notice ? 120 : 160;
                var body = HorizontalTextParagraph.Render(font, SongSelectText.Description(caption, english), 400, bodyHeight,
                    fillRgb: muted ? 0x555555U : 0xffffffU);
                var pixels = new byte[400 * 160 * 4];
                body.Pixels.CopyTo(pixels, 0);
                if (notice)
                {
                    var note = HorizontalTextParagraph.Render(font, SongSelectText.FirstSongNotice(english), 400, 40,
                        fillRgb: 0xff5500, preferredLines: 2);
                    note.Pixels.CopyTo(pixels, 400 * bodyHeight * 4);
                }
                var portrait = HorizontalSongTitleRasterizer.RotateCounterClockwise(new(400, 160, pixels));
                session.ReplacePixels((int)texture, portrait.Pixels);
            }
        }
        return changed.Count;
    }
}
