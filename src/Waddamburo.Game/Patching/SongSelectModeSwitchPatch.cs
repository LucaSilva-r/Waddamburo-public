using Waddamburo.Formats.Lmb;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Patching;

/// <summary>Supplies the vertical mode-switch states from the movie's own native blue artwork.</summary>
public static class SongSelectModeSwitchPatch
{
    public static void Apply(LumenMovieContent content)
    {
        var movie = content.Definition;
        string name(uint index) => index < movie.Strings.Length ? movie.Strings[(int)index].Value : "";
        if (!movie.Strings.Any(s => s.Value == "wd_vertical_rows_v2")) return;
        var frames = movie.Sprites.FirstOrDefault(s => s.Timeline.OfType<LmbPlaceObjectCommand>()
            .Any(p => name(p.NameStringIndex) == "wd_peg_fill"));
        var header = movie.Sprites.FirstOrDefault(s => s.ExportStringIndex is { } e && name(e) == "GenreFolderTab");
        if (frames is null || header is null) return;
        const string source = "J-POP";
        var target = SongSelectCatalogView.ModeSwitchLabel;
        bool has(LmbSpriteDefinition sprite, string label) => sprite.Timeline.OfType<LmbFrameLabelCommand>()
            .Any(l => name(l.StringIndex) == label);
        var tabPlace = header.Timeline.OfType<LmbPlaceObjectCommand>().FirstOrDefault(p => name(p.NameStringIndex) == "tab");
        if (tabPlace is null) return;
        var tab = movie.Sprites.Single(s => s.CharacterId == tabPlace.CharacterId);
        if (has(frames, target) && has(tab, target)) return;
        var editor = new LumenMovieEditor(content);
        if (!has(frames, target))
        {
            var nativeBase = movie.Sprites.FirstOrDefault(s => has(s, target)
                && placements(s, target).Any(p => name(p.NameStringIndex) == "expand_"));
            if (nativeBase is null || !has(frames, source)) return;
            var expandedId = placements(nativeBase, target).Single(p => name(p.NameStringIndex) == "expand_").CharacterId;
            var expanded = movie.Sprites.Single(s => s.CharacterId == expandedId);
            var native = at(expanded, 0).GroupBy(p => p.Depth).Select(group => group.Last()).ToArray();
            uint texture(uint character) => movie.Shapes.Single(s => s.CharacterId == character).Geometry[0].TextureIndex;
            var rowTextures = new[] { texture(native.Single(p => p.Depth == 6).CharacterId),
                texture(native.Single(p => p.Depth == 5).CharacterId), texture(native.Single(p => p.Depth == 7).CharacterId) };
            var swaps = new Dictionary<uint, uint>();
            var sourceParts = placements(frames, source).ToArray();
            foreach (var part in sourceParts)
            {
                var partName = name(part.NameStringIndex);
                if (partName.StartsWith("part_", StringComparison.Ordinal)
                    && int.TryParse(partName.AsSpan(5), out var index) && index is >= 0 and < 9)
                    swaps[texture(part.CharacterId)] = rowTextures[index / 3];
            }
            var nativeCap = movie.Sprites.Where(s => has(s, target)).SelectMany(s => placements(s, target))
                .Select(p => movie.Shapes.FirstOrDefault(s => s.CharacterId == p.CharacterId))
                .OfType<LmbShapeDefinition>().SelectMany(s => s.Geometry)
                .FirstOrDefault(g => content.Textures[(int)g.TextureIndex] is { Width: 88, Height: 24 });
            var sourceCap = sourceParts.FirstOrDefault(p => name(p.NameStringIndex) == "wd_peg_outline");
            if (nativeCap is not null && sourceCap is not null) swaps[texture(sourceCap.CharacterId)] = nativeCap.TextureIndex;
            editor.CloneLabel(frames.CharacterId, source, target,
                index => swaps.TryGetValue(index, out var replacement) ? new TextureRule.Swap(replacement) : null);
        }
        // The original switch is a button, so the original folder-tab clip has
        // no matching label either. HeaderColours will tint this state from its body.
        if (!has(tab, target) && has(tab, source)) editor.CloneLabel(tab.CharacterId, source, target, _ => null);
        editor.Commit();

        IEnumerable<LmbPlaceObjectCommand> placements(LmbSpriteDefinition sprite, string label)
        {
            var labels = sprite.Timeline.OfType<LmbFrameLabelCommand>().ToArray();
            var start = labels.First(l => name(l.StringIndex) == label).Frame;
            return at(sprite, start).GroupBy(p => p.Depth).Select(group => group.Last());
        }
        IEnumerable<LmbPlaceObjectCommand> at(LmbSpriteDefinition sprite, uint selected)
        {
            uint frame = 0;
            foreach (var command in sprite.Timeline)
            {
                frame = command switch { LmbShowFrameCommand show => show.Frame, LmbFrameKeyCommand key => key.Frame, _ => frame };
                if (frame == selected && command is LmbPlaceObjectCommand { CharacterId: > 0 } place) yield return place;
            }
        }
    }
}
