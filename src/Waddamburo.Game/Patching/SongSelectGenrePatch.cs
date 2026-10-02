using System.Collections.Immutable;
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

    // Green's layout of what the patch uses (texture → size): another movie version with other numbering
    // fails the check and is left alone rather than patched with the wrong art.
    private static readonly (uint Texture, int Width, int Height)[] Layout =
    [
        (423, 8, 64), (424, 64, 64), (547, 24, 560), (548, 8, 560), (555, 8, 480), (556, 32, 480), (557, 32, 480),
        (643, 88, 24), (704, 8, 560), (705, 40, 560), (736, 8, 72), (737, 48, 72), (772, 640, 720),
        (642, 56, 400), (450, 256, 56), (508, 160, 400), (507, 192, 360),
    ];

    // The genre folder sprite (Green, 1392): its front is three pieces, the left half (depth 6) and right
    // half (7) moving apart over frames 4-18 as it opens. The event folder lays a pattern over the same
    // front (crossfading a closed-size and an open-size texture, none between frames 8 and 14). Ours is one
    // open-size pattern (the event's 368x448 quad, shape 1576) fixed in place under a clip mask that
    // follows the front inside its black outline (left half - 4 to right half + 3, y 19-470), so it is
    // revealed as the folder opens, never stretched. The mask is the opaque J-POP background shape (1928,
    // 640x720) scaled to that rectangle.
    private const uint Spine = 1392, OpenPattern = 1576, MaskShape = 1928;
    private const ushort LeftHalf = 6, RightHalf = 7, MaskDepth = 8, PatternDepth = 9;
    private const float MaskLeft = 4, MaskRight = 3, MaskTop = 19, MaskBottom = 470, MaskShapeWidth = 640, MaskShapeHeight = 720;

    // The open front is wider on screen than the event's 368x448 quad: the pattern overshoots it (the mask
    // trims it). Pattern images are stretched to 460x448 stage units: draw them in that aspect.
    private const float PatternWidthScale = 1.25f;

    /// <summary>The pattern's opacity (a colour entry's alpha, 256 = opaque).</summary>
    private const short PatternAlpha = 128;

    private static void addPattern(LumenMovieEditor editor, uint sprite, int slot, uint alpha)
    {
        var pattern = editor.Clone(OpenPattern, _ => new TextureRule.Fill(""));
        var name = editor.AddString(Slot("pattern", slot));
        var centre = editor.AddMatrix(new LmbMatrix(PatternWidthScale, 0, 0, 1, 4, 245));
        var result = ImmutableArray.CreateBuilder<LmbTimelineCommand>();
        float left = 0, right = 0;
        var placed = false;
        // The mask's matrix for the halves' latest positions in `commands`; whether any moved.
        bool track(IEnumerable<LmbTimelineCommand> commands)
        {
            var moved = false;
            foreach (var place in commands.OfType<LmbPlaceObjectCommand>().Where(static place => place.PositionKind == 0x8000))
            {
                var x = editor.Translation(place.PositionIndex).X;
                if (place.Depth == LeftHalf) { left = x - MaskLeft; moved = true; }
                if (place.Depth == RightHalf) { right = x + MaskRight; moved = true; }
            }
            return moved;
        }
        ushort mask() => editor.AddMatrix(new LmbMatrix((right - left) / MaskShapeWidth, 0, 0, (MaskBottom - MaskTop) / MaskShapeHeight,
            (left + right) / 2, (MaskTop + MaskBottom) / 2));
        LmbPlaceObjectCommand common(LmbPlaceObjectCommand template) => template with
        {
            NameStringIndex = 0, BlendMode = 0, PositionKind = 0,
            ColorMultiplyIndex = uint.MaxValue, ColorAddIndex = uint.MaxValue, ClipDepth = 0,
        };
        // Both placed afresh (a keyframe's display list, or the first frame).
        LmbTimelineCommand[] placeBoth(LmbPlaceObjectCommand template) =>
        [
            common(template) with { CharacterId = MaskShape, Mode = 1, Depth = MaskDepth, PlacementId = MaskDepth, PositionIndex = mask(), ClipDepth = PatternDepth },
            common(template) with
            {
                CharacterId = pattern, Mode = 1, Depth = PatternDepth, PlacementId = PatternDepth,
                NameStringIndex = name, PositionIndex = centre, ColorMultiplyIndex = alpha,
            },
        ];
        foreach (var (show, commands, before) in frames(editor.Sprite(sprite).Timeline))
        {
            result.AddRange(before);
            // A frame's own commands, then (on keyframes) FrameKey display lists used when seeking: each
            // list gets the mask (sized from its own halves) and the pattern.
            var head = commands.TakeWhile(static command => command is not LmbFrameKeyCommand).ToList();
            var extra = new List<LmbTimelineCommand>();
            if (track(head) && head.OfType<LmbPlaceObjectCommand>().FirstOrDefault() is { } template)
            {
                extra.AddRange(placed
                    ? [common(template) with { CharacterId = 0, Mode = 2, Depth = MaskDepth, PlacementId = MaskDepth, PositionIndex = mask() }]
                    : placeBoth(template));
                placed = true;
            }
            result.Add(show with { DeclaredCommandCount = show.DeclaredCommandCount + (uint)extra.Count });
            result.AddRange(head);
            result.AddRange(extra);
            var (frameLeft, frameRight) = (left, right);
            for (var index = head.Count; index < commands.Count;)
            {
                var key = (LmbFrameKeyCommand)commands[index];
                var entries = commands.Skip(index + 1).TakeWhile(static command => command is not LmbFrameKeyCommand).ToList();
                track(entries);
                var added = entries.OfType<LmbPlaceObjectCommand>().FirstOrDefault() is { } first ? placeBoth(first) : [];
                result.Add(key with { EntryCount = key.EntryCount + (uint)added.Length });
                result.AddRange(entries);
                result.AddRange(added);
                index += entries.Count + 1;
            }
            // Playing on continues from the frame's state, not the last display list's.
            (left, right) = (frameLeft, frameRight);
        }
        editor.SetTimeline(sprite, result.ToImmutable());
    }

    // A timeline as frames: each ShowFrame with the commands after it (and labels/keys before the first).
    private static List<(LmbShowFrameCommand Show, List<LmbTimelineCommand> Commands, List<LmbTimelineCommand> Before)> frames(
        ImmutableArray<LmbTimelineCommand> timeline)
    {
        var result = new List<(LmbShowFrameCommand, List<LmbTimelineCommand>, List<LmbTimelineCommand>)>();
        var before = new List<LmbTimelineCommand>();
        foreach (var command in timeline)
        {
            if (command is LmbShowFrameCommand show)
                result.Add((show, [], before.Count > 0 ? [.. before] : []));
            else if (result.Count == 0)
                before.Add(command);
            else
                result[^1].Item2.Add(command);
            if (command is LmbShowFrameCommand)
                before.Clear();
        }
        return result;
    }


    /// <summary>Each slot's colour-pool entry in the last patched song_select.lm (set at runtime).</summary>
    public static IReadOnlyList<uint> Colours { get; private set; } = [];

    /// <summary>Whether the last song_select.lm decoded was patched (its custom genres exist).</summary>
    public static bool LastApplied { get; private set; }

    public static bool AppliesTo(LumenMovieContent content)
    {
        if (content.Name != MovieName && !content.Name.EndsWith("/" + MovieName, StringComparison.Ordinal))
            return false;
        // ponytail: diagnostic switch, the movie's own event art for every named folder.
        if (Environment.GetEnvironmentVariable("WADDAMBURO_NO_GENRE_PATCH") == "1")
            return LastApplied = false;
        var matches = Layout.All(entry => entry.Texture < content.Textures.Length
            && content.Textures[(int)entry.Texture] is { } texture
            && texture.Width == entry.Width && texture.Height == entry.Height);
        if (!matches)
            Console.Error.WriteLine("Warning LUMEN_PATCH: song_select.lm is not Green's layout; custom genre colours are off.");
        LastApplied = matches;
        return matches;
    }

    public static void Apply(LumenMovieContent content)
    {
        var editor = new LumenMovieEditor(content);
        var sprites = editor.SpritesWithLabel(Source);
        var patternAlpha = editor.AddColorTransform(256, 256, 256, PatternAlpha);
        var colours = new uint[Slots];
        for (var slot = 0; slot < Slots; slot++)
            colours[slot] = editor.AddColorTransform(256, 256, 256);
        Colours = colours;
        for (var slot = 0; slot < Slots; slot++)
        {
            var colour = colours[slot];
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
            editor.SpriteCloned = (original, copy) =>
            {
                if (original == Spine)
                    addPattern(editor, copy, current, patternAlpha);
            };
            foreach (var sprite in sprites)
                editor.CloneLabel(sprite, Source, Label(slot), rule);
            editor.SpriteCloned = null;
        }
        editor.Commit();
    }
}
