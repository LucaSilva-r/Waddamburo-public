using Waddamburo.Game;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Waddamburo.Skins.Green.Patching;

/// <summary>
/// Lets the restricted course board (MusicInfo.HasInvalidCourse: a song missing a course) switch Oni to Ura. Its
/// right-move skips invalid boards and, with no valid board to the right, returns before counting the right-ka
/// presses that switch to Ura (normal boards count them at the last board). One branch becomes a jump so the count
/// runs there too; the switch itself (CourseBoard.Tween_Switch) needs the Ura stars, which the host sends.
/// Tween_Switch flips Ura back to Oni unconditionally, which an Ura-only song (no Oni) must not do: its body is
/// rewritten, same length, so the flip back needs Oni stars as the flip forward needs Ura stars.
/// </summary>
public static class SongSelectCoursePatch
{
    // `if (r4 < r9) skip; return false; if (r4 < r9) ...`: the first If (byte 10) becomes a Jump.
    private static readonly byte[] Signature = Convert.FromHexString(
        "96040004040409481212" + "9D0200060096020005003E" + "960400040404094812129D");
    private const int BranchAt = 10;
    private const byte Jump = 0x99;

    // CourseBoard.Tween_Switch (160-byte body after this DefineFunction2 header, the same in the three song selects up
    // to constant-pool indices):
    //   switch (state) { case STATE_TOP: if (star[selfCource + 4] > 0) return Tween_ChangeBack(); return false;
    //                    case STATE_BACK: return Tween_ChangeTop(); } return false;
    private static readonly byte[] SwitchHeader = Convert.FromHexString("8E090000000000022900A000");
    private const int SwitchBodySize = 160;

    // Rebuilt from its own pieces, the same length (hand-assembled 2026-11-24):
    //   var top = state === STATE_TOP;
    //   if (!top && state !== STATE_BACK) return false;
    //   if (!star[selfCource + top * 4]) return false;
    //   return top ? Tween_ChangeBack() : Tween_ChangeTop();
    // (PushDuplicate/StackSwap keep `top` on the stack; unreachable Pops pad to the old size.)
    public static byte[] GuardedSwitch(ReadOnlySpan<byte> body)
    {
        var bytes = body.ToArray();
        byte[] piece(int from, int to) => bytes[from..to];
        var isTop = piece(0, 28);       // state -> r0, === STATE_TOP
        var isBack = piece(33, 50);     // r0 === STATE_BACK
        var star = piece(60, 83);       // this.resource.selected.star
        var self = piece(83, 92);       // this.selfCource
        var four = piece(92, 100);      // push 4
        var changeBack = piece(117, 132);
        var returnFalse = piece(132, 138);
        var changeTop = piece(138, 153);
        byte[] If(short to) => [0x9D, 0x02, 0x00, (byte)to, (byte)(to >> 8)];
        byte[] dup = [0x4C], not = [0x12], multiply = [0x0C], add = [0x47], swap = [0x4D], getMember = [0x4E], pop = [0x17];
        var code = new List<byte>(SwitchBodySize);
        foreach (var part in new[]
        {
            isTop, dup, If(23), isBack, not, If(86),
            dup, four, multiply, self, add, star, swap, getMember, not, If(35),
            If(15), changeTop, changeBack, pop, returnFalse,
        })
            code.AddRange(part);
        while (code.Count < SwitchBodySize - 1)
            code.Add(pop[0]);
        code.Add(0x00);
        return [.. code];
    }

    private static bool isSwitch(ReadOnlySpan<byte> body) =>
        body.Length >= SwitchBodySize && body[28..33].SequenceEqual((byte[])[0x9D, 0x02, 0x00, 0x1B, 0x00])
        && body[50..60].SequenceEqual((byte[])[0x9D, 0x02, 0x00, 0x53, 0x00, 0x99, 0x02, 0x00, 0x5D, 0x00])
        && body[SwitchBodySize - 1] == 0x00;

    /// <summary>Patches song select movie <paramref name="content"/> (no-op for other movies).</summary>
    public static void Apply(LumenMovieContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!SongSelectGenrePatch.MovieNames.Any(name =>
                content.Name == name || content.Name.EndsWith("/" + name, StringComparison.Ordinal)))
            return;
        if (!patch(content, Signature, static (bytes, at) =>
            {
                bytes[at + BranchAt] = Jump;
                return true;
            }))
            Console.Error.WriteLine($"Warning LUMEN_PATCH: {content.Name} has no course-switch branch; Ura stays hidden on restricted boards.");
        if (!patch(content, SwitchHeader, static (bytes, at) =>
            {
                var body = bytes.AsSpan(at + SwitchHeader.Length);
                if (!isSwitch(body))
                    return false;
                GuardedSwitch(body).CopyTo(body);
                return true;
            }))
            Console.Error.WriteLine($"Warning LUMEN_PATCH: {content.Name} has no Tween_Switch body; Ura-only songs can flip to Oni.");
    }

    // The first match of signature that edit accepts (edit sees a copy; a rejected match is skipped).
    private static bool patch(LumenMovieContent content, byte[] signature, Func<byte[], int, bool> edit)
    {
        var definition = content.Definition;
        for (var index = 0; index < definition.Actions.Length; index++)
        {
            var action = definition.Actions[index];
            var bytes = action.Bytecode.ToArray();
            var at = -1;
            while ((at = nextMatch(bytes, signature, at + 1)) >= 0 && !edit(bytes, at))
            {
            }
            if (at < 0)
                continue;
            content.Replace(definition with
            {
                Actions = definition.Actions.SetItem(index, action.WithBytecode(ImmutableCollectionsMarshal.AsImmutableArray(bytes))),
            }, content.Textures);
            return true;
        }
        return false;
    }

    private static int nextMatch(byte[] bytes, byte[] signature, int from)
    {
        var at = bytes.AsSpan(from).IndexOf(signature);
        return at < 0 ? -1 : from + at;
    }
}
