using Waddamburo.Formats.Lmb;

namespace Waddamburo.Game.Patching;

/// <summary>Matches native tab artwork to its category body's colour, retaining the original shading and silhouette.</summary>
public static class SongSelectHeaderColours
{
    public static string Label(string genre) => "wd_header:" + genre;

    public static void Apply(LumenMovieContent content)
    {
        var movie = content.Definition;
        if (!movie.Strings.Any(s => s.Value == "wd_vertical_rows_v2")) return;
        string name(uint index) => movie.Strings[(int)index].Value;
        var frames = movie.Sprites.FirstOrDefault(s => s.Timeline.OfType<LmbPlaceObjectCommand>()
            .Any(p => name(p.NameStringIndex) == "wd_peg_fill"));
        var header = movie.Sprites.FirstOrDefault(s => s.ExportStringIndex is { } export && name(export) == "GenreFolderTab");
        if (frames is null || header is null) return;
        var tabId = header.Timeline.OfType<LmbPlaceObjectCommand>().First(p => name(p.NameStringIndex) == "tab").CharacterId;
        var tab = movie.Sprites.Single(s => s.CharacterId == tabId);
        var editor = new LumenMovieEditor(content);
        foreach (var label in frames.Timeline.OfType<LmbFrameLabelCommand>())
        {
            var text = name(label.StringIndex);
            if (!tab.Timeline.OfType<LmbFrameLabelCommand>().Any(l => name(l.StringIndex) == text)) continue;
            var fill = placements(frames, text).FirstOrDefault(p => name(p.NameStringIndex) == "part_4");
            if (fill is null) continue;
            var geometry = movie.Shapes.Single(s => s.CharacterId == fill.CharacterId).Geometry[0];
            var body = content.Textures[(int)geometry.TextureIndex];
            if (body.Rgba8.IsDefaultOrEmpty) continue;
            var x = Math.Clamp((int)(geometry.Vertices.Average(v => v.U) * body.Width), 0, body.Width - 1);
            var y = Math.Clamp((int)(geometry.Vertices.Average(v => v.V) * body.Height), 0, body.Height - 1);
            var at = (body.Width * y + x) * 4;
            var colour = (body.Rgba8[at], body.Rgba8[at + 1], body.Rgba8[at + 2]);
            var indices = placements(tab, "J-POP").SelectMany(p => textureIndices(p.CharacterId, [])).ToHashSet();
            if (indices.Count == 0) continue;
            var reference = indices.Min();
            var tint = editor.AddColorTransform((short)(colour.Item1 * 256 / 255),
                (short)(colour.Item2 * 256 / 255), (short)(colour.Item3 * 256 / 255));
            // Use the same native silhouette for every tab; genre-specific originals
            // have differently coloured outlines and compressed edge noise.
            editor.CloneLabel(tabId, "J-POP", Label(text), texture => indices.Contains(texture)
                ? new TextureRule.Recolour(tint, reference, BlackOutline: true) : null);
        }
        editor.Commit();

        IEnumerable<LmbPlaceObjectCommand> placements(LmbSpriteDefinition sprite, string label)
        {
            var labels = sprite.Timeline.OfType<LmbFrameLabelCommand>().ToArray();
            var start = labels.First(l => name(l.StringIndex) == label).Frame;
            var end = labels.Where(l => l.Frame > start).Select(l => l.Frame).DefaultIfEmpty(sprite.DeclaredFrameCount).Min();
            uint frame = 0;
            foreach (var command in sprite.Timeline)
            {
                frame = command switch { LmbShowFrameCommand show => show.Frame, LmbFrameKeyCommand key => key.Frame, _ => frame };
                if (frame >= start && frame < end && command is LmbPlaceObjectCommand { CharacterId: > 0 } place) yield return place;
            }
        }
        IEnumerable<uint> textureIndices(uint id, HashSet<uint> seen)
        {
            if (!seen.Add(id)) yield break;
            var shape = movie.Shapes.FirstOrDefault(s => s.CharacterId == id);
            if (shape is not null) foreach (var geometry in shape.Geometry) yield return geometry.TextureIndex;
            var sprite = movie.Sprites.FirstOrDefault(s => s.CharacterId == id);
            if (sprite is null) yield break;
            foreach (var child in sprite.Timeline.OfType<LmbPlaceObjectCommand>().Where(p => p.CharacterId != 0))
                foreach (var texture in textureIndices(child.CharacterId, seen)) yield return texture;
        }
    }

}
