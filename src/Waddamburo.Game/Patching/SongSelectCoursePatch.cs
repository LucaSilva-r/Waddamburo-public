using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Waddamburo.Game.Patching;

/// <summary>
/// Lets the restricted course board (MusicInfo.HasInvalidCourse: a song missing a course) switch Oni to Ura. Its
/// right-move skips invalid boards and, with no valid board to the right, returns before counting the right-ka
/// presses that switch to Ura (normal boards count them at the last board). One branch becomes a jump so the count
/// runs there too; the switch itself (CourseBoard.Tween_Switch) needs the Ura stars, which the host sends.
/// </summary>
public static class SongSelectCoursePatch
{
    // `if (r4 < r9) skip; return false; if (r4 < r9) ...`: the first If (byte 10) becomes a Jump.
    private static readonly byte[] Signature = Convert.FromHexString(
        "96040004040409481212" + "9D0200060096020005003E" + "960400040404094812129D");
    private const int BranchAt = 10;
    private const byte Jump = 0x99;

    /// <summary>Patches song select movie <paramref name="content"/> (no-op for other movies).</summary>
    public static void Apply(LumenMovieContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!SongSelectGenrePatch.MovieNames.Any(name =>
                content.Name == name || content.Name.EndsWith("/" + name, StringComparison.Ordinal)))
            return;
        var definition = content.Definition;
        for (var index = 0; index < definition.Actions.Length; index++)
        {
            var action = definition.Actions[index];
            var at = action.Bytecode.AsSpan().IndexOf(Signature);
            if (at < 0)
                continue;
            var bytes = action.Bytecode.ToArray();
            bytes[at + BranchAt] = Jump;
            content.Replace(definition with
            {
                Actions = definition.Actions.SetItem(index, action.WithBytecode(ImmutableCollectionsMarshal.AsImmutableArray(bytes))),
            }, content.Textures);
            return;
        }
        Console.Error.WriteLine($"Warning LUMEN_PATCH: {content.Name} has no course-switch branch; Ura stays hidden on restricted boards.");
    }
}
