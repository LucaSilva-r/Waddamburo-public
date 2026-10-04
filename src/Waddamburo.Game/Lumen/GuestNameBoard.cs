using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>The player_name board calls the game makes for a player (traced call sequence).</summary>
public static class GuestNameBoard
{
    /// <summary>
    /// Fills the board for <paramref name="side"/> (2: a card not yet taken by a drum) with the side's
    /// name or <paramref name="name"/>, then shows it (<paramref name="visible"/> false: left for a Fadein).
    /// The title and plate are the side's profile's, or <paramref name="profile"/>'s (a card not yet on a side).
    /// </summary>
    public static void Show(LumenPlayer board, int side, string? name = null, bool visible = true, bool cover = false,
        Scores.ScoreProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        name ??= Gameplay.TaikoGuest.Name(side);
        call(board, "SetPlayer", number(side));
        call(board, "SetKinotake", number(-1));
        call(board, "SetTitleName", LumenHostValue.FromString(profile is null ? Gameplay.TaikoGuest.Title(side) : profile.Title ?? ""));
        call(board, "SetTitlePanelID", number(profile is null ? Gameplay.TaikoGuest.TitlePlate(side) : profile.TitlePlate));
        call(board, "SetDani", number(0), LumenHostValue.FromBoolean(false));
        // Cover: greyed, a card given to a drum that has not joined yet (traced session13-card).
        call(board, "SetCover", LumenHostValue.FromBoolean(cover));
        // The board's own glyphs are kana only: the name is host text over its names clip (see NameText).
        call(board, "SetNameSize", number(0));
        call(board, "Apply");
        NameText.Show(board, "board/names", name);
        if (visible)
            call(board, "SetVisible", LumenHostValue.FromBoolean(true));
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Name board is missing callback '{name}'.");
    }
}

/// <summary>
/// A name board's name as host text (Latin and Japanese alike, in the game font) over the board's
/// <c>names</c> clip, where its kana glyphs would go: centred at (155, 45) in the clip, glyph rows 29-61
/// (measured on Green's player_name.lm, the same with and without a title).
/// </summary>
public static class NameText
{
    public const string Prefix = "name:";

    /// <summary>The name's box in the names clip: 187x26, the entry overlay's 176x24 stage box at the board's scale.</summary>
    public static readonly LumenNativeSurfacePlacement Box = new(155 - 93.5f, 45 - 13, 187, 26);

    public static LumenNativeSurfaceKey Key(string name) => new(Prefix + name);

    public static bool TryParse(LumenNativeSurfaceKey key, out string name)
    {
        name = key.Value.StartsWith(Prefix, StringComparison.Ordinal) ? key.Value[Prefix.Length..] : "";
        return name.Length > 0;
    }

    /// <summary>Puts <paramref name="name"/> over the clip at <paramref name="path"/> (empty: nothing, e.g. the entry's own overlay).</summary>
    public static void Show(LumenPlayer board, string path, string name) =>
        board.SetInstanceOverlay(path, string.IsNullOrWhiteSpace(name) ? null : Key(name), Box);
}
