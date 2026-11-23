using System.Globalization;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Home;

/// <summary>
/// The overlay menus' one text field (song search, Send's code and search, an online room's code, name and password):
/// typed text goes in at the cursor, ←/→ move it, Backspace and Delete remove a whole character (surrogate pairs,
/// combining marks), at most <paramref name="maxLength"/> characters; <paramref name="accept"/> may change or refuse
/// each typed one (a code takes letters and digits, upper-cased). The owner starts and stops the system's text input.
/// </summary>
internal sealed class OverlayTextField(int maxLength = int.MaxValue, Func<char, char?>? accept = null)
{
    private int _blink;

    public string Text { get; private set; } = "";

    public int Caret { get; private set; }

    private string? _replacement;

    private bool _passEnter;

    /// <summary>
    /// On a console with a keyboard of its own (the Switch), Enter on the field opens it (the screen's hints stay readable
    /// until then): it edits the whole text, the next <see cref="Edit"/> takes the result as typing, then
    /// <paramref name="confirm"/> is pressed for the owner (when given; that Enter passes through). True when it took
    /// this Enter. Cancelling changes nothing. Elsewhere false: typing goes on as usual.
    /// </summary>
    public bool OpenSystemKeyboard(Waddamburo.Platform.Sdl.SdlApplication application, string hint, SdlKeyboardSnapshot keys,
        SdlKeyboardKey? confirm = SdlKeyboardKey.Enter)
    {
        if (!Waddamburo.Platform.Sdl.SdlApplication.HasSystemKeyboard || !keys.IsDown(SdlKeyboardKey.Enter))
            return false;
        if (_passEnter)
        {
            _passEnter = false;
            return false;
        }
        _replacement = application.EditWithSystemKeyboard(Text, hint, maxLength == int.MaxValue ? 0 : maxLength);
        if (_replacement is not null && confirm is { } key)
        {
            _passEnter = key == SdlKeyboardKey.Enter;
            application.InjectPress(key);
        }
        return true;
    }

    /// <summary>Replaces the text (filtered as typing is), the cursor at its end.</summary>
    public void Set(string text)
    {
        Text = "";
        Caret = 0;
        insert(text);
    }

    /// <summary>One tick of typing: <paramref name="typed"/> (the system's text) and the editing keys; true when the text changed.</summary>
    public bool Edit(string typed, SdlKeyboardSnapshot keys)
    {
        _blink++;
        var before = Text;
        if (_replacement is { } replacement)
        {
            _replacement = null;
            Set(replacement);
        }
        insert(typed);
        var starts = StringInfo.ParseCombiningCharacters(Text);
        int previous() => starts.LastOrDefault(start => start < Caret);
        int next() => starts.FirstOrDefault(start => start > Caret, Text.Length);
        if (keys.IsDown(SdlKeyboardKey.Backspace) && Caret > 0)
            (Text, Caret) = (Text[..previous()] + Text[Caret..], previous());
        else if (keys.IsDown(SdlKeyboardKey.Delete) && Caret < Text.Length)
            Text = Text[..Caret] + Text[next()..];
        else if (keys.IsDown(SdlKeyboardKey.Left))
            (Caret, _blink) = (previous(), 0);
        else if (keys.IsDown(SdlKeyboardKey.Right))
            (Caret, _blink) = (next(), 0);
        if (Text != before)
            _blink = 0;
        return Text != before;
    }

    /// <summary>The text for an overlay row (its braces would colour it), with the blinking cursor while typed in.</summary>
    public string Shown(bool typing, string? masked = null)
    {
        var text = (masked ?? Text).Replace('{', '(').Replace('}', ')');
        return typing ? text[..Caret] + (_blink % 36 < 24 ? "|" : " ") + text[Caret..] : text;
    }

    private void insert(string typed)
    {
        foreach (var letter in typed)
        {
            if (char.IsControl(letter) || Text.Length >= maxLength || (accept is null ? letter : accept(letter)) is not { } kept)
                continue;
            Text = Text[..Caret] + kept + Text[Caret..];
            Caret++;
        }
    }
}
