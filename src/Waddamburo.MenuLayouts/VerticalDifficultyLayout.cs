namespace Waddamburo.MenuLayouts;

/// <summary>Fits the original difficulty panel, leaving the Ranking artwork untouched.</summary>
internal static class VerticalDifficultyLayout
{
    public static void Apply(VerticalLayoutDocument session, VerticalSongSelectProfile profile)
    {
        var document = session.Document;
        document.AddLabel(profile.Main, 0, "wd_vertical_refined_v3");
        document.MapPlacementMatrices(profile.Main, (_, p, m) =>
            document.Definition.Strings[(int)p.NameStringIndex].Value == "courseSelect_"
                ? m with { M11 = m.M11 * .82f, M12 = m.M12 * .82f,
                    M21 = m.M21 * .82f, M22 = m.M22 * .82f, Y = m.Y - 10 } : m);
        document.MapPlacementMatrices(profile.CourseSelect, (_, p, m) =>
            document.Definition.Strings[(int)p.NameStringIndex].Value == "modeTitle_"
                ? m with { M11 = m.M11 / .82f, M22 = m.M22 / .82f,
                    X = m.X / .82f, Y = (m.Y + 10) / .82f } : m);
        // Move the corners and only resize the flat strips. Border images keep their own thickness.
        document.MapPlacementMatrices(profile.CoursePanel, (_, p, m) => p.Depth < 14
            ? m with
            {
                Y = 465 + (m.Y - 465) * .9f,
                M22 = p.Depth is 3 or 5 or 7 or 8 ? m.M22 * .9f : m.M22,
            } : m);
        var header = document.AddSprite("wd_course_header");
        document.SetPlacement(header, 0, 0, profile.Mask, new(560f / 640, 0, 0, 48f / 720, 0, 0),
            name: "wd_course_title_mask", clipDepth: 1);
        var title = document.AddShape(0, 560, 48, nativeSurface: true, centered: true);
        document.SetPlacement(header, 0, 1, title, new(1, 0, 0, 1, 0, 0), name: "wd_course_title");
        document.SetPlacement(profile.CoursePanel, 0, 15, header, new(1, 0, 0, 1, 47, -61), name: "wd_course_header");
    }
}
