using Waddamburo.Formats.Lmb;

namespace Waddamburo.MenuLayouts;

/// <summary>Resolves the two Green menu variants by their native exports and named children.</summary>
internal sealed class VerticalSongSelectProfile
{
    private VerticalSongSelectProfile(VerticalLayoutDocument session)
    {
        var document = session.Document;
        var movie = document.Definition;
        if (session.MovieName is not ("song_select/song_select.lm" or "waiwai_song_select/waiwai_song_select.lm"))
            throw new InvalidDataException("Unsupported song-select movie.");
        IsWaiwai = session.MovieName == "waiwai_song_select/waiwai_song_select.lm";
        uint export(string name) => movie.Sprites.FirstOrDefault(s => s.ExportStringIndex is { } index
            && movie.Strings[(int)index].Value == name)?.CharacterId
            ?? throw new InvalidDataException($"Unsupported song-select profile: missing {name} export.");
        uint child(uint parent, string name) => movie.Sprites.Single(s => s.CharacterId == parent).Timeline
            .OfType<LmbPlaceObjectCommand>().FirstOrDefault(p => p.CharacterId != 0
                && movie.Strings[(int)p.NameStringIndex].Value == name)?.CharacterId
            ?? throw new InvalidDataException($"Unsupported song-select profile: missing {name} child.");
        uint part(uint parent, ushort depth) => movie.Sprites.Single(s => s.CharacterId == parent).Timeline
            .OfType<LmbPlaceObjectCommand>().FirstOrDefault(p => p.Depth == depth && p.CharacterId != 0)?.CharacterId
            ?? throw new InvalidDataException($"Unsupported song-select profile: missing part at depth {depth}.");

        Main = export("Main");
        CoursePanel = child(Main, "coursePanel_");
        CourseSelect = child(Main, "courseSelect_");
        MusicSelect = export("MusicSelect");
        Board = export("MusicBoard");
        FolderTab = export("GenreFolderTab");
        GenreDetail = export("GenreDetail");
        MusicDetail = export("MusicDetail");
        Folder = export("GenreFolder");
        CenterFrame = export("CenterFrame");
        if (!document.Placements(Main, 0).Any(p => p.Depth == 3 && p.CharacterId == MusicSelect
                && movie.Strings[(int)p.NameStringIndex].Value == "genreSelect_")
            || movie.Sprites.Single(s => s.CharacterId == MusicSelect).DeclaredFrameCount != 168
            || child(MusicSelect, "genreFolder_") != Folder || child(Folder, "center_") != CenterFrame)
            throw new InvalidDataException("Unsupported song-select main timeline or folder structure.");

        GenreArt = child(GenreDetail, "type_");
        Base = child(Board, "base_");
        CategoryAccessory = child(Board, "accessory_");
        FolderAccessory = child(Board, "folderaccessory_");
        Selection = child(MusicSelect, "expand_");
        CompactTitle = child(Selection, "song_name_center");
        ExpandedTitle = child(Selection, "song_name_detail");
        var hinge = child(child(Folder, "left_"), "folder_");
        FoldBody = child(hinge, "body_");
        FoldEdge = child(hinge, "edge_");
        Outline = part(child(Base, "expand_"), 6);
        var masks = movie.Shapes.Where(shape =>
        {
            var vertices = shape.Geometry.SelectMany(geometry => geometry.Vertices).ToArray();
            return vertices.Length >= 4
                && Math.Abs(vertices.Max(v => v.X) - vertices.Min(v => v.X) - 640) < .01f
                && Math.Abs(vertices.Max(v => v.Y) - vertices.Min(v => v.Y) - 720) < .01f;
        }).Select(shape => shape.CharacterId).ToArray();
        var background = child(child(Main, "backGround_behind_"), "genre_");
        var backgroundFrame = movie.Sprites.Single(s => s.CharacterId == background).Timeline
            .OfType<LmbFrameLabelCommand>().FirstOrDefault(label => movie.Strings[(int)label.StringIndex].Value == "J-POP")
            ?? throw new InvalidDataException("Missing J-POP background for the native mask.");
        Mask = document.Placements(background, (int)backgroundFrame.Frame).Single(p => p.Depth == 0).CharacterId;
        if (!masks.Contains(Mask)) throw new InvalidDataException("The native mask is not a 640x720 rectangle.");
        var backword = child(Board, "backword_");
        var shapes = movie.Shapes.Select(s => s.CharacterId).ToHashSet();
        ReturnShapes = new[] { part(MusicDetail, 6) }.Concat(movie.Sprites.Single(s => s.CharacterId == backword).Timeline
                .OfType<LmbPlaceObjectCommand>().Select(p => p.CharacterId).Where(shapes.Contains)).Distinct().ToArray();
        if (ReturnShapes.Length != 2 || !shapes.Contains(Mask) || !shapes.Contains(Outline))
            throw new InvalidDataException($"Unsupported song-select artwork: mask {Mask} (shape={shapes.Contains(Mask)}), "
                + $"outline {Outline} (shape={shapes.Contains(Outline)}), Return [{string.Join(",", ReturnShapes)}].");

        DisplayNamePush = constant("DisplayName");
        VisiblePush = constant("Visible");
        VisibilityPush = constant("_visible");
        var method = "Push 0402" + DisplayNamePush;
        TabAction = Array.FindIndex(movie.Actions.ToArray(), action =>
        {
            var assembly = Avm1Assembly.Disassemble(action, movie.Strings);
            return assembly.Contains(method, StringComparison.Ordinal)
                && assembly.Contains("\"GenreFolderTab\"", StringComparison.Ordinal);
        });
        if (TabAction < 0) throw new InvalidDataException("Unsupported GenreFolderTab controller.");
        string constant(string name)
        {
            var index = Array.FindIndex(movie.Strings.ToArray(), entry => entry.Value == name);
            if (index is < 0 or > ushort.MaxValue) throw new InvalidDataException($"Missing native symbol {name}.");
            return $"09{index & 255:X2}{index >> 8:X2}";
        }
    }

    public bool IsWaiwai { get; }
    public uint Main { get; }
    public uint CoursePanel { get; }
    public uint CourseSelect { get; }
    public uint MusicSelect { get; }
    public uint Board { get; }
    public uint FolderTab { get; }
    public uint GenreDetail { get; }
    public uint GenreArt { get; }
    public uint MusicDetail { get; }
    public uint Folder { get; }
    public uint CenterFrame { get; }
    public uint Base { get; }
    public uint CategoryAccessory { get; }
    public uint FolderAccessory { get; }
    public uint Selection { get; }
    public uint CompactTitle { get; }
    public uint ExpandedTitle { get; }
    public uint FoldBody { get; }
    public uint FoldEdge { get; }
    public uint Mask { get; }
    public uint Outline { get; }
    public uint[] ReturnShapes { get; }
    public int TabAction { get; }
    public string DisplayNamePush { get; }
    public string VisiblePush { get; }
    public string VisibilityPush { get; }

    public static VerticalSongSelectProfile Read(VerticalLayoutDocument session) => new(session);
}
