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

    /// <summary>
    /// The name's box in the names clip: 176x24, the player setup overlay's own box, so names are the same
    /// size everywhere (the boards show at about their authored scale; user-preferred over a fitted 187x26).
    /// </summary>
    public static readonly LumenNativeSurfacePlacement Box = new(155 - 88, 45 - 12, 176, 24);

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

/// <summary>
/// A play speed's text (1.3x) on Song Select's option board, drawn in place of Green's speed art as the
/// name tags' names are (white in a black outline, their box). Speed 1.0 keeps Green's ふつう: null.
/// </summary>
public static class SpeedText
{
    public const string Prefix = "speed:";

    public static LumenNativeSurfaceKey? Key(int index, LumenNativeSurfacePlacement box) => index <= 0 ? null
        : new(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Prefix}{(int)Math.Round(box.Width)}x{(int)Math.Round(box.Height)}:{Gameplay.TaikoPlayOptions.SpeedText(index)}"));

    public static bool TryParse(LumenNativeSurfaceKey key, out int width, out int height, out string text)
    {
        (width, height, text) = (0, 0, "");
        if (!key.Value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var rest = key.Value[Prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var x = rest.IndexOf('x', StringComparison.Ordinal);
        return colon > x && x > 0 && int.TryParse(rest[..x], out width) && int.TryParse(rest[(x + 1)..colon], out height)
            && (text = rest[(colon + 1)..]).Length > 0;
    }
}

/// <summary>
/// A drum sound's (音色) name or icon on Song Select's tone board: the game's own art, resolved by the host
/// (ToneArt). Green has tones 0-5; tone 0 (the plain drum) has no icon.
/// </summary>
public static class ToneSurfaces
{
    public const string Prefix = "tone-art:";

    public static LumenNativeSurfaceKey Name(int tone) => new($"{Prefix}name:{tone}");

    public static LumenNativeSurfaceKey? Icon(int tone) => tone <= 0 ? null : new($"{Prefix}icon:{tone}");

    public static bool TryParse(LumenNativeSurfaceKey key, out bool icon, out int tone)
    {
        (icon, tone) = (false, 0);
        if (!key.Value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var rest = key.Value[Prefix.Length..];
        icon = rest.StartsWith("icon:", StringComparison.Ordinal);
        return int.TryParse(rest[(rest.IndexOf(':', StringComparison.Ordinal) + 1)..], out tone);
    }
}
