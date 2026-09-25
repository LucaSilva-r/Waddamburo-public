using System.Globalization;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>The player_name board calls the game makes for a player (traced call sequence).</summary>
public static class GuestNameBoard
{
    /// <summary>
    /// Fills the board for <paramref name="side"/> (2: a card not yet taken by a drum) with the side's
    /// name or <paramref name="name"/>, then shows it (<paramref name="visible"/> false: left for a Fadein).
    /// </summary>
    public static void Show(LumenPlayer board, int side, string? name = null, bool visible = true)
    {
        ArgumentNullException.ThrowIfNull(board);
        name ??= Gameplay.TaikoGuest.Name(side);
        var characters = StringInfo.GetTextElementEnumerator(name);
        call(board, "SetPlayer", number(side));
        call(board, "SetKinotake", number(-1));
        call(board, "SetTitleName", LumenHostValue.FromString(""));
        call(board, "SetTitlePanelID", number(0));
        call(board, "SetDani", number(0), LumenHostValue.FromBoolean(false));
        call(board, "SetCover", LumenHostValue.FromBoolean(false));
        call(board, "SetNameSize", number(new StringInfo(name).LengthInTextElements));
        for (var index = 0; characters.MoveNext(); index++)
            call(board, "SetChar", number(index), LumenHostValue.FromString(characters.GetTextElement()));
        call(board, "Apply");
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
