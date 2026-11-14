using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Game.Patching;

/// <summary>
/// Adds <see cref="Slots"/> custom genres to the song selects (<see cref="MovieNames"/>, a Lumen patch): each is a copy of the J-POP
/// art under its own label (<see cref="Label"/>) in every sprite that has genre frames, recoloured by its
/// own colour-pool entry (the last <see cref="Slots"/> entries, set at runtime) with its highlights and
/// shading kept (TextureRule.Recolour), its names, description and mascot drawn from host slots
/// (<see cref="Slot"/>). The host registers the labels as genres (GenreResource.MUSICINFO_KEY) when
/// Song Select starts.
/// </summary>
public static class SongSelectGenrePatch
{
    public const int Slots = 32;
    private const string Source = "J-POP", EventLabel = "イベント";

    /// <summary>
    /// The song selects patched: Green's song_select, Waiwai's and AI battle's are the same movie with their characters
    /// and textures renumbered, so each one's J-POP pieces are found in it (<see cref="Roles"/>).
    /// </summary>
    public static readonly string[] MovieNames =
        ["song_select/song_select.lm", "waiwai_song_select/waiwai_song_select.lm", "ghost_song_select/ghost_song_select.lm"];

    // J-POP's textures by role, as song_select has them (Green): each found by its size and its place among the J-POP
    // frames' textures of that size (by number), which is the same in the three movies. Its art (folder tab, board,
    // spine tab, centre folder, header tab, background), the art's colour reference (the spine: every piece is split
    // against one colour, so they match), then what names the genre.
    private enum Role { Art, Reference, SpineName, HeaderName, Description, Mascot }
    private static readonly (int Width, int Height, int Rank, Role Role)[] Roles =
    [
        (8, 64, 0, Role.Art), (64, 64, 0, Role.Art), (8, 560, 0, Role.Art), (8, 480, 1, Role.Art), (32, 480, 2, Role.Art),
        (32, 480, 3, Role.Art), (88, 24, 0, Role.Art), (8, 560, 1, Role.Art), (40, 560, 0, Role.Art), (8, 72, 0, Role.Art),
        (48, 72, 0, Role.Art), (640, 720, 0, Role.Art), (24, 560, 0, Role.Reference),
        (56, 400, 0, Role.SpineName), (256, 56, 0, Role.HeaderName), (160, 400, 0, Role.Description), (192, 360, 0, Role.Mascot),
    ];

    // A movie's J-POP pieces (song_select's numbers: art 423-772, reference 547, names 642/450/508/507, folder sprite
    // 1392, pattern 1576, mask 1928).
    private sealed record Profile(HashSet<uint> Art, uint Reference, uint SpineName, uint HeaderName, uint Description,
        uint Mascot, uint Spine, uint OpenPattern, uint MaskShape);

    // Found by AppliesTo for Apply; each movie's slot colours, by movie name (they may be decoded on any thread).
    private static readonly ConditionalWeakTable<LumenMovieContent, Profile> Found = [];
    private static readonly ConcurrentDictionary<string, IReadOnlyList<uint>> ColoursByMovie = new();

    /// <summary>How far left of the J-POP header quad's centre a custom genre's header name sits (stage units).</summary>
    public const float HeaderShift = 8;

    public static string Label(int slot) => $"wd:{slot:00}";

    /// <summary>A custom genre's host surface slot: tate (spine name), yoko (header name), desc, image.</summary>
    public static string Slot(string part, int slot) => $"wd_{part}_{slot:00}";

    // The genre folder sprite (song_select's 1392): its front is three pieces, the left half (depth 6) and right
    // half (7) moving apart over frames 4-18 as it opens. The event folder lays a pattern over the same
    // front (crossfading a closed-size and an open-size texture, none between frames 8 and 14). Ours is one
    // open-size pattern (the event's 368x448 quad, song_select's shape 1576) fixed in place under a clip mask that
    // follows the front inside its black outline (left half - 4 to right half + 3, y 19-470), so it is
    // revealed as the folder opens, never stretched. The mask is the opaque J-POP background shape (song_select's
    // 1928, 640x720) scaled to that rectangle.
    private const ushort LeftHalf = 6, RightHalf = 7, MaskDepth = 8, PatternDepth = 9;
    private const float MaskLeft = 4, MaskRight = 3, MaskTop = 19, MaskBottom = 470, MaskShapeWidth = 640, MaskShapeHeight = 720;

    // The open front is wider on screen than the event's 368x448 quad: the pattern overshoots it (the mask
    // trims it). Pattern images are stretched to 460x448 stage units: draw them in that aspect.
    private const float PatternWidthScale = 1.25f;

    /// <summary>The pattern's opacity (a colour entry's alpha, 256 = opaque).</summary>
    private const short PatternAlpha = 128;

    private static void addPattern(LumenMovieEditor editor, Profile profile, uint sprite, int slot, uint alpha)
    {
        var pattern = editor.Clone(profile.OpenPattern, _ => new TextureRule.Fill(""));
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
            common(template) with { CharacterId = profile.MaskShape, Mode = 1, Depth = MaskDepth, PlacementId = MaskDepth, PositionIndex = mask(), ClipDepth = PatternDepth },
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


    /// <summary>Each slot's colour-pool entry in song select movie <paramref name="movie"/> (empty: not patched).</summary>
    public static IReadOnlyList<uint> Colours(string movie) => ColoursByMovie.GetValueOrDefault(movie) ?? [];

    /// <summary>Whether song select movie <paramref name="movie"/> was patched (its custom genres exist).</summary>
    public static bool Applied(string movie) => ColoursByMovie.ContainsKey(movie);

    private static string? movieName(LumenMovieContent content) => MovieNames.FirstOrDefault(name =>
        content.Name == name || content.Name.EndsWith("/" + name, StringComparison.Ordinal));

    public static bool AppliesTo(LumenMovieContent content)
    {
        if (movieName(content) is not { } movie)
            return false;
        ColoursByMovie.TryRemove(movie, out _);
        // ponytail: diagnostic switch, the movie's own event art for every named folder.
        if (Environment.GetEnvironmentVariable("WADDAMBURO_NO_GENRE_PATCH") == "1")
            return false;
        if (find(content) is not { } profile)
        {
            Console.Error.WriteLine($"Warning LUMEN_PATCH: {movie} is not Green's layout; custom genres are off there.");
            return false;
        }
        Found.AddOrUpdate(content, profile);
        return true;
    }

    // The movie's J-POP pieces, null when any is missing or ambiguous (another release's movie: left alone rather than
    // patched with the wrong art).
    private static Profile? find(LumenMovieContent content)
    {
        try
        {
            var editor = new LumenMovieEditor(content);
            var sprites = editor.SpritesWithLabel(Source).ToArray();
            if (sprites.Length == 0)
                return null;
            HashSet<uint> reached(string label) => editor.Reached(sprites.Where(sprite => editor.Labels(sprite).Contains(label))
                .SelectMany(sprite => editor.PlacedAt(sprite, label)));
            var jpop = reached(Source);
            var others = editor.Reached(sprites.SelectMany(sprite => editor.Labels(sprite).Where(static label => label != Source)
                .Distinct().SelectMany(label => editor.PlacedAt(sprite, label))));
            var bySize = editor.Textures(jpop).GroupBy(editor.TextureSize)
                .ToDictionary(static group => group.Key, static group => group.Order().ToArray());
            var roles = Roles.Select(role => (role.Role, Texture: bySize.GetValueOrDefault((role.Width, role.Height)) is { } list
                && role.Rank < list.Length ? list[role.Rank] : (uint?)null)).ToArray();
            if (roles.Any(static role => role.Texture is null))
                return null;
            uint one(Role role) => roles.Single(entry => entry.Role == role).Texture!.Value;
            var background = Roles.Index().Where(static entry => entry.Item is (640, 720, _, _)).Select(entry => roles[entry.Index].Texture!.Value).Single();
            // The folder that opens in two halves (depths 6 and 7), J-POP's own (another one is shared with other genres).
            var spines = jpop.Where(character => !others.Contains(character) && editor.IsSprite(character)
                && editor.Sprite(character).Timeline.OfType<LmbPlaceObjectCommand>().Select(static place => place.Depth).ToHashSet() is var depths
                && depths.Contains(LeftHalf) && depths.Contains(RightHalf)).ToArray();
            var masks = jpop.Where(character => editor.IsShape(character) && editor.ShapeTextures(character).Contains(background)).ToArray();
            var patterns = reached(EventLabel).Where(character => editor.IsShape(character)
                && editor.ShapeSize(character) is var (width, height) && Math.Abs(width - 368) < 1 && Math.Abs(height - 448) < 1).ToArray();
            if (spines.Length != 1 || masks.Length != 1 || patterns.Length != 1)
                return null;
            return new([.. roles.Where(static role => role.Role == Role.Art).Select(static role => role.Texture!.Value)], one(Role.Reference),
                one(Role.SpineName), one(Role.HeaderName), one(Role.Description), one(Role.Mascot), spines[0], patterns[0], masks[0]);
        }
        catch (InvalidOperationException)
        {
            return null; // a label twice in one sprite, or the like: not the movie expected
        }
    }

    public static void Apply(LumenMovieContent content)
    {
        if (movieName(content) is not { } movie || !Found.TryGetValue(content, out var profile))
            return;
        var editor = new LumenMovieEditor(content);
        var sprites = editor.SpritesWithLabel(Source);
        var patternAlpha = editor.AddColorTransform(256, 256, 256, PatternAlpha);
        var colours = new uint[Slots];
        for (var slot = 0; slot < Slots; slot++)
            colours[slot] = editor.AddColorTransform(256, 256, 256);
        ColoursByMovie[movie] = colours;
        for (var slot = 0; slot < Slots; slot++)
        {
            var colour = colours[slot];
            var current = slot;
            TextureRule? rule(uint texture) => texture switch
            {
                _ when texture == profile.SpineName => new TextureRule.Fill(Slot("tate", current)),
                _ when texture == profile.HeaderName => new TextureRule.Fill(Slot("yoko", current)),
                _ when texture == profile.Description => new TextureRule.Fill(Slot("desc", current)),
                _ when texture == profile.Mascot => new TextureRule.Fill(Slot("image", current)),
                _ when texture == profile.Reference || profile.Art.Contains(texture) => new TextureRule.Recolour(colour, profile.Reference),
                _ => null,
            };
            editor.SpriteCloned = (original, copy) =>
            {
                if (original == profile.Spine)
                    addPattern(editor, profile, copy, current, patternAlpha);
            };
            foreach (var sprite in sprites)
                editor.CloneLabel(sprite, Source, Label(slot), rule);
            editor.SpriteCloned = null;
        }
        editor.Commit();
    }
}
