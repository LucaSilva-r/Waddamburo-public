namespace Waddamburo.Game.Scores;

/// <summary>
/// A player's Don as their profile has it: costume parts (kigurumi, head, body, face, puchi; 0 = none)
/// and face/body/limb colours as #rrggbb.
/// </summary>
public sealed record DonLook(int[] Costume, string Face, string Body, string Limb)
{
    /// <summary>The entry's three costume sets, [kigurumi, head, body, puchi] each; null: none served.</summary>
    public int[][]? Presets { get; init; }

    /// <summary>The displayed dan rank (0 none, 1-25 Beginner through Tatsujin): which dan costume model to draw.</summary>
    public int DanRank { get; init; }

    /// <summary>The puchi chara (costume slot 5; 0 = none). A whole costume does not hide it.</summary>
    public int Puchi => Costume is [_, _, _, _, var puchi, ..] ? puchi : 0;

    public (uint Face, uint Body, uint Limb) Colors => (rgb(Face), rgb(Body), rgb(Limb));

    private static uint rgb(string hex) => Convert.ToUInt32(hex.TrimStart('#'), 16);
}
