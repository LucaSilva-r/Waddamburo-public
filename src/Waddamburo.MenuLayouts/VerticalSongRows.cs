using System.Globalization;
using System.Text;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.MenuLayouts;

/// <summary>Native row highlight and a top-anchored category frame that grows with its song list.</summary>
internal static class VerticalSongRows
{
    public static void Apply(VerticalLayoutDocument session, VerticalSongSelectProfile profile)
    {
        var document = session.Document;
        var placements = new List<LmbPlacementEdit>();
        void place(uint sprite, int frame, ushort depth, uint character, LmbMatrix matrix,
            float opacity = 1, string? name = null, ushort blendMode = 0, ushort clipDepth = 0) =>
            placements.Add(new(sprite, frame, depth, character, matrix, opacity, name, blendMode, clipDepth));
        document.AddLabel(profile.Main, 0, "wd_vertical_rows_v2");
        var source = document.Definition;
        var highlight = document.Placements(profile.Selection, 0).Where(p => p.Depth < 14)
            .Select(p => (Place: p, Matrix: document.Matrix(p), Alpha: document.Opacity(p))).ToArray();
        var row = document.AddSprite("wd_vertical_selection", 1);
        foreach (var (p, matrix, alpha) in highlight)
            place(row, 0, p.Depth, p.CharacterId, matrix with { Y = matrix.Y + 10 / 1.1f }, alpha,
                source.Strings[(int)p.NameStringIndex].Value, p.BlendMode, p.ClipDepth);
        place(row, 0, 14, profile.CompactTitle, new(1, 0, 0, 1, 4, 244), name: "wd_selected_title");
        var context = document.AddSprite("wd_vertical_context", 1);
        place(context, 0, 0, profile.Base, new(1, 0, 0, -1, 42, 494), name: "base_");
        place(context, 0, 2, profile.CompactTitle, new(1, 0, 0, 1, 46, 240), name: "wd_context_title");

        var frame = source.Sprites.Single(s => s.CharacterId == profile.CenterFrame);
        var templates = frame.Timeline.OfType<LmbFrameLabelCommand>().Select(label => (
            Frame: (int)label.Frame, Name: source.Strings[(int)label.StringIndex].Value,
            Parts: document.Placements(profile.CenterFrame, (int)label.Frame)
                .Select(p => (Place: p, Matrix: document.Matrix(p), Alpha: document.Opacity(p))).ToArray())).ToArray();
        var folder = document.AddSprite("wd_vertical_rows_frame", (int)frame.DeclaredFrameCount);
        // Nine independent native slices. Texture pixels in the corners and across each
        // border retain their final screen size; only the flat spans grow with the list.
        var slices = new Dictionary<(uint Shape, int Row, int Column), uint>();
        var pattern = document.AddShape(0, 1, 1, nativeSurface: true, centered: true);
        var shadow = source.Shapes.Single(s => s.CharacterId == profile.Outline).Geometry.Single();
        var texture = session.Textures[(int)shadow.TextureIndex];
        var pixels = texture.Rgba.ToArray();
        var black = Enumerable.Range(0, pixels.Length / 4).First(i => pixels[i * 4 + 3] == 255
            && pixels[i * 4] < 5 && pixels[i * 4 + 1] < 5 && pixels[i * 4 + 2] < 5);
        var blackShape = document.CopyShape(profile.Outline, vertex => new(vertex.U - .5f, vertex.V - .5f,
            (black % texture.Width + .5f) / texture.Width, (black / texture.Width + .5f) / texture.Height));
        var expandedSprites = new HashSet<uint>();
        foreach (var template in templates)
        {
            document.AddLabel(folder, template.Frame, template.Name);
            var genre = source.Sprites.Single(s => s.CharacterId == profile.Base).Timeline
                .OfType<LmbFrameLabelCommand>().First(l => source.Strings[(int)l.StringIndex].Value == template.Name);
            var expanded = document.Placements(profile.Base, (int)genre.Frame)
                .Single(p => source.Strings[(int)p.NameStringIndex].Value == "expand_").CharacterId;
            var art = document.Placements(expanded, 0);
            if (expandedSprites.Add(expanded))
                document.NamePlacements(expanded, p => p.Depth switch
                {
                    6 => "wd_native_top", 7 => "wd_native_bottom", _ => null,
                });
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                {
                    var original = art.Single(p => p.Depth == (r == 0 ? 6 : r == 1 ? 5 : 7)).CharacterId;
                    if (!slices.TryGetValue((original, r, c), out var slice))
                    {
                        var left = r == 0 ? 6f / 32 : 0;
                        var right = r == 2 ? 25f / 32 : 1;
                        var upper = (c == 0 ? 10f : c == 1 ? 32f : 448f) / 480;
                        var lower = (c == 0 ? 32f : c == 1 ? 448f : 470f) / 480;
                        // The native centre strip's bevel is stored one texel below its end caps.
                        // Align the samples as well as the geometry, including the inner highlight.
                        if (r == 1) { upper += 1f / 480; lower += 1f / 480; }
                        slice = document.CopyShape(original, vertex => new(
                            vertex.U - .5f, vertex.V - .5f,
                            left + vertex.U * (right - left), upper + vertex.V * (lower - upper)));
                        slices.Add((original, r, c), slice);
                    }
                    var pose = Waddamburo.Lumen.Rendering.VerticalCategoryFrame.Slice(r, c, 350);
                    place(folder, template.Frame, (ushort)(r * 3 + c), slice,
                        new(pose.Width, 0, 0, pose.Height, pose.X, pose.Y), name: $"part_{r * 3 + c}");
                }
            // This clips ONLY the decorative pattern to the inside of its own frame.
            // Song boards and neighbouring categories have no viewport mask.
            place(folder, template.Frame, 9, profile.Mask,
                new(1, 0, 0, 1, 130, 288), name: "wd_pattern_mask", clipDepth: 12);
            for (ushort tile = 0; tile < 3; tile++)
                place(folder, template.Frame, (ushort)(10 + tile), pattern,
                    new(0, 598f / 1.1f, -600f / .65f, 0, 130, 288), .5f, name: $"wd_rows_pattern_{tile}");
            var colorShape = document.CopyShape(art.Single(p => p.Depth == 5).CharacterId,
                vertex => new(vertex.U - .5f, vertex.V - .5f, .5f, .5f));
            var capLabel = source.Sprites.Single(s => s.CharacterId == profile.CategoryAccessory).Timeline
                .OfType<LmbFrameLabelCommand>().First(l => source.Strings[(int)l.StringIndex].Value == template.Name);
            var capArt = document.Placements(profile.CategoryAccessory, (int)capLabel.Frame).First(p =>
                source.Shapes.FirstOrDefault(s => s.CharacterId == p.CharacterId)?.Geometry is { Length: 1 } geometry
                && session.Textures[(int)geometry[0].TextureIndex] is { Width: 88, Height: 24 }).CharacterId;
            // Keep the native rounded/sloping tab. Crop its transparent padding and the
            // old horizontal tail, which the new frame's continuous outline already supplies.
            var nativeCap = document.CopyShape(capArt, vertex => new(vertex.U - .5f, vertex.V - .5f,
                (4 + vertex.U * 44) / 88, (2 + vertex.V * 20) / 24));
            foreach (var (name, depth) in new[] { ("wd_border_top", 13), ("wd_border_bottom", 14),
                         ("wd_border_start", 15), ("wd_border_end", 16) })
                place(folder, template.Frame, (ushort)depth, blackShape,
                    new(1, 0, 0, 1, 0, 0), name: name);
            place(folder, template.Frame, 17, colorShape, new(1, 0, 0, 1, 0, 0), name: "wd_peg_fill");
            place(folder, template.Frame, 18, nativeCap, new(1, 0, 0, 1, 0, 0), name: "wd_peg_outline");
        }
        document.RemapDepths(profile.Board, static depth => depth == 0 ? depth : checked((ushort)(depth + 1)));
        place(profile.Board, 0, 1, folder, new(1, 0, 0, -1, 42, 494), name: "wd_category_frame");
        place(context, 0, 1, folder, new(1, 0, 0, -1, 42, 494), name: "wd_category_frame");
        place(profile.Folder, 0, 3, folder, new(1, 0, 0, 1, 114.46f, 0), name: "wd_rows_frame");
        // Keep the original instance names and placement identities: its controller still owns navigation.
        document.RemapDepths(profile.MusicSelect, static depth => depth == 0 ? depth : checked((ushort)(depth + 1)));
        place(profile.MusicSelect, 0, 25, row, new(1, 0, 0, 1, 238.154f, -4), name: "wd_flat_selection");
        place(profile.MusicSelect, 0, 26, context, new(1, 0, 0, 1, -192.154f, -50), name: "wd_context_folder");
        place(profile.MusicSelect, 0, 27, context, new(1, 0, 0, 1, -192.154f, -13.636f), name: "wd_category_previous");
        place(profile.MusicSelect, 0, 28, context, new(1, 0, 0, 1, 515.538f, -13.636f), name: "wd_category_next");
        var self = document.InternString("this");
        var visible = document.InternString("_visible");
        var initialize = new StringBuilder();
        foreach (var name in new[] { "wd_flat_selection", "wd_context_folder",
                     "wd_category_previous", "wd_category_next", "genreFolder_/wd_rows_frame" })
        {
            initialize.AppendLine(CultureInfo.InvariantCulture, $"Push string16:{self}").AppendLine("GetVariable");
            foreach (var part in name.Split('/'))
                initialize.AppendLine(CultureInfo.InvariantCulture, $"Push string16:{document.InternString(part)}").AppendLine("GetMember");
            initialize.AppendLine(CultureInfo.InvariantCulture, $"Push string16:{visible} bool:false").AppendLine("SetMember");
        }
        initialize.AppendLine("End");
        document.SetPlacements(placements);
        document.AddAction(profile.MusicSelect, 0, Avm1Assembly.Assemble(initialize.ToString()));
    }
}
