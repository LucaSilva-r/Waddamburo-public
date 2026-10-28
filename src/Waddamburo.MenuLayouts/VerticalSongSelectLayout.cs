using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.MenuLayouts;

/// <summary>Builds the vertical menu from the installed native art, on Windows and Linux.</summary>
public static class VerticalSongSelectLayout
{
    public static void ValidateSource(VerticalLayoutDocument session) => _ = VerticalSongSelectProfile.Read(session);

    public static VerticalLayoutResult Apply(VerticalLayoutDocument session, string fontPath, bool english = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        var profile = VerticalSongSelectProfile.Read(session);
        if (session.Document.Definition.Strings.Any(s => s.Value == "wd_vertical_rows_v2"))
            throw new InvalidDataException("The vertical layout has already been applied.");
        session.Document.InternString("wd_song_select_vertical_v1");
        // The unnamed confirmation effect otherwise remains at the portrait
        // menu's coordinates. Name it so the presenter can attach its pulse
        // to the selected category's final rectangle, including the D curve.
        session.Document.NamePlacements(profile.MusicSelect, placement =>
            placement.CharacterId != 0 && session.Document.Definition.Strings[(int)placement.NameStringIndex].Value.Length == 0
                ? "wd_category_confirm" : null);
        session.Document.AddLabel(profile.Main, 0, "wd_vertical_v1");
        session.Document.MapPlacementMatrices(profile.Main, (_, p, m) => p.Depth switch
        {
            3 => new(-m.M12 * 1.1f, m.M11 * .65f, -m.M22 * 1.1f, m.M21 * .65f,
                240 - (m.Y + 267) * 1.1f, -175 + (m.X + 197) * .65f),
            4 => new(0, .5f, -.5f, 0, 330, -72),
            _ => m,
        });
        session.Document.MapPlacementMatrices(profile.FolderTab, (_, _, m) => then(m,
            new(0, -.8f / 1.1f, .8f / .65f, 0, -137.327f, 543.07f)));
        session.Document.MapPlacementMatrices(profile.MusicSelect, (_, p, m) => p.Depth switch
        {
            17 or 18 => then(m, new(0, -.8f / 1.1f, .8f / .65f, 0, -7.692f, 496.727f)),
            // Leave room for the upright tab above the expanded selection. The movie captures
            // these authored positions for both its recycled boards and its move animations.
            >= 1 and <= 16 => m with { X = m.X + (m.X < 150 ? -20 : m.X > 150 ? 20 : 0) },
            _ => m,
        });
        // Keep the native tab visible when the open-folder controller displays its name.
        var tabAction = session.Document.Definition.Actions[profile.TabAction];
        var tabAssembly = Avm1Assembly.Disassemble(tabAction, session.Document.Definition.Strings);
        var displayStart = tabAssembly.IndexOf("Push 0402" + profile.DisplayNamePush, StringComparison.Ordinal);
        var displayEnd = tabAssembly.IndexOf("Push 0402" + profile.VisiblePush, displayStart, StringComparison.Ordinal);
        var displayMethod = tabAssembly[displayStart..displayEnd];
        if (!displayMethod.Contains("Push " + profile.VisibilityPush + "0500", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected GenreFolderTab.DisplayName implementation.");
        tabAssembly = tabAssembly[..displayStart] + displayMethod.Replace("Push " + profile.VisibilityPush + "0500",
                "Push " + profile.VisibilityPush + "0501", StringComparison.Ordinal)
            + tabAssembly[displayEnd..];
        session.Document.SetActionBytecode(profile.TabAction, Avm1Assembly.Assemble(tabAssembly));
        // The old portrait folder frame bakes its tab into the top 72 pixels of its strips.
        // Reuse the straight section at its own UV coordinates; the upright native tab is separate.
        var croppedShapes = new Dictionary<uint,uint>();
        foreach (var spriteId in new[] { profile.CenterFrame, profile.FoldBody, profile.FoldEdge })
        {
            var replacements = new Dictionary<uint, uint>();
            foreach (var place in session.Document.Definition.Sprites.Single(s => s.CharacterId == spriteId).Timeline
                         .OfType<LmbPlaceObjectCommand>().Where(p => p.CharacterId != 0))
            {
                if (replacements.ContainsKey(place.CharacterId)) continue;
                if (!croppedShapes.TryGetValue(place.CharacterId, out var cropped))
                {
                    cropped = session.Document.CopyShape(place.CharacterId, v => v.V < .5f
                        ? v with { Y = v.Y + 72, V = v.V + 72f / 560 } : v);
                    croppedShapes.Add(place.CharacterId, cropped);
                }
                replacements.Add(place.CharacterId, cropped);
            }
            session.Document.RemapCharacters(spriteId, replacements);
        }

        const float artScale = .55f;
        session.Document.MapPlacementMatrices(profile.GenreDetail, (_, _, m) => then(m,
            new(0, -artScale / 1.1f, artScale / .65f, 0,
                103 - artScale / .65f * 190, 190 + artScale / 1.1f * 103)));
        var descriptions = VerticalGenreArt.Apply(session, fontPath, profile, english);
        session.Document.MapPlacementMatrices(profile.Board, (_, p, m) => p.Depth == 0
            ? m with { M21 = -m.M21, M22 = -m.M22, Y = 490 - m.Y } : m);
        session.Document.MapPlacementMatrices(profile.Selection, (_, p, m) => p.Depth == 15 ? m with { X = m.X - 248.5f } : m);
        session.Document.MapPlacementMatrices(profile.MusicDetail, (_, p, m) => p.Depth switch
        {
            <= 3 => new(0, -.5f / 1.1f, .5f / .65f, 0, -44.615f, 31.8f + p.Depth * 90.909f),
            4 => then(m, new(0, -.5f / 1.1f, .5f / .65f, 0, 0, 150)),
            5 => new(0, -.55f / 1.1f, .55f / .65f, 0, 45, 170),
            _ => m,
        });
        foreach (var (field, width) in new (uint, float)[] { (profile.ExpandedTitle, 96), (profile.CompactTitle, 56) })
        {
            var surface = session.Document.AddShape(0, width, 400, nativeSurface: true, centered: true);
            session.Document.RemovePlacement(field, 0, 0);
            session.Document.SetPlacement(field, 0, 1, surface, new(1,0,0,1,0,0));
            session.Document.SetPlacement(field, 0, 0, profile.Mask, new(width / 640,0,0,400f / 720,0,0),
                name: "wd_title_mask", clipDepth: 1);
        }
        var definition = session.Document.Definition;
        var names = new Dictionary<uint,string> {
            [0] = "How to play", [10] = "J-POP", [20] = "Anime", [30] = "Kids", [40] = "Variety",
            [50] = "Classical Music", [60] = "Game Music", [70] = "Namco Original", [80] = "Recommended",
            [90] = "Favorites", [100] = "Recently Played", [110] = "Dojo", [130] = "VOCALOID",
            [160] = "Event", [200] = "Change Mode"
        };
        if (profile.IsWaiwai) names.Add(210, "How to play Waiwai");
        var changed = new HashSet<uint>();
        foreach (var label in definition.Sprites.Single(s => s.CharacterId == profile.CategoryAccessory).Timeline.OfType<LmbFrameLabelCommand>())
        {
            var name = definition.Strings[(int)label.StringIndex].Value;
            if (!names.TryGetValue(label.Frame, out var caption)) continue;
            foreach(var p in session.Document.Placements(profile.CategoryAccessory, (int)label.Frame).Where(p => p.Depth == 0))
            {
                var shape = definition.Shapes.FirstOrDefault(s => s.CharacterId == p.CharacterId);
                if (shape is null) continue;
                foreach(var g in shape.Geometry)
                {
                    var texture = session.Textures[(int)g.TextureIndex];
                    if (texture.Width != 56 || texture.Height != 400 || !changed.Add(g.TextureIndex)) continue;
                    var surface = HorizontalSongTitleRasterizer.RotateCounterClockwise(HorizontalSongTitleRasterizer.Render(
                        fontPath, SongSelectText.Name(caption, english), null, 400, 56, 0,
                        horizontalAspect: HorizontalSongTitleRasterizer.VerticalBoardAspect));
                    session.ReplacePixels((int)g.TextureIndex, surface.Pixels);
                }
            }
        }
        foreach(var id in profile.ReturnShapes)
        {
            var shape = definition.Shapes.FirstOrDefault(s => s.CharacterId == id);
            if(shape is null) continue;
            foreach(var g in shape.Geometry)
            {
                var texture = session.Textures[(int)g.TextureIndex];
                if(texture.Width > 96 || texture.Height < 300 || !changed.Add(g.TextureIndex)) continue;
                var surface = HorizontalSongTitleRasterizer.RotateCounterClockwise(HorizontalSongTitleRasterizer.Render(
                    fontPath, SongSelectText.Name("Return", english), null, texture.Height, texture.Width, 0,
                    horizontalAspect: HorizontalSongTitleRasterizer.VerticalBoardAspect));
                session.ReplacePixels((int)g.TextureIndex, surface.Pixels);
            }
        }
        VerticalSongRows.Apply(session, profile);
        VerticalDifficultyLayout.Apply(session, profile);
        return new(changed.Count, croppedShapes.Count, descriptions);
    }

    private static LmbMatrix then(LmbMatrix a, LmbMatrix b)
    {
        var result = new LumenMatrix(a.M11,a.M12,a.M21,a.M22,a.X,a.Y).Then(new(b.M11,b.M12,b.M21,b.M22,b.X,b.Y));
        return new(result.M11,result.M12,result.M21,result.M22,result.X,result.Y);
    }

}

public sealed record VerticalLayoutResult(int TitleTextures, int FrameShapes, int Descriptions);
