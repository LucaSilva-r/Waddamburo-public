using System.Text;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Home;

/// <summary>
/// A row of an <see cref="OverlayPage"/>: its label at x, its value (a {braced} part takes the value colour) at valueX.
/// An option has <paramref name="Step"/> (Enter edits it, then the arrows or rims change it, Enter sets it); a command
/// has <paramref name="Enter"/>; a row with neither is information and isn't selectable.
/// </summary>
internal sealed record OverlayRow(string Label, float X = 480, Func<string>? Value = null, float ValueX = 690,
    Action<int>? Step = null, Func<bool>? IsDefault = null, Action? Enter = null, bool Gap = false, bool Cyan = false,
    string Hint = "", Func<RenderColor>? Colour = null)
{
    public bool Selectable => Step is not null || Enter is not null;
}

/// <summary>
/// A page of the overlay menu. <paramref name="Defaults"/>: the "[DEFAULT IN GREEN]" header (its options' values
/// are green at their default); <paramref name="Centred"/>: a short list of choices in the middle (a pause menu);
/// <paramref name="DrumTest"/>: the drum is what the page tests, so it doesn't move or pick there (Escape leaves);
/// <paramref name="Back"/>: what Escape (or the back button) does here, else the previous page comes back;
/// <paramref name="Tag"/>: the owner's name for the page; <paramref name="Legend"/>: the controls line at the bottom (off
/// where the screen has its own there, Song Select's name boards under the online lobby); <paramref name="Fixed"/>: the
/// rows stay where they say (a menu's main list, at its left edge), else the page's block of labels and values is
/// centred, the values a few cells after the longest label (as the cabinet lays out its option pages);
/// <paramref name="Body"/>: a centred page's message as coloured lines (the boot's error screen), in place of
/// <paramref name="Message"/>; <paramref name="Footer"/>: coloured lines at the bottom, in place of the legend.
/// </summary>
internal sealed record OverlayPage(Func<string> Title, Func<OverlayRow[]> Rows, bool Defaults = false, bool Centred = false,
    bool DrumTest = false, Func<string?>? Message = null, Action? Back = null, object? Tag = null, bool Legend = true,
    bool Fixed = false, Func<(string Text, RenderColor Colour)[]>? Body = null,
    Func<(string Text, RenderColor Colour)[]>? Footer = null);

/// <summary>
/// The menus' one look and feel, after the arcade cabinet's service menu: plain text (Kosugi Maru) over black on a
/// cabinet, over the frosted game at home. A stack of pages of options, commands and information; arrows and Enter
/// everywhere, and at home the drum too (rims move, the centre picks; editing, the left rim lowers and the right
/// raises), Escape or the back button going up a level. The selected row is red for 0.3 s, then white for 0.3 s
/// (the cabinet's 0.6 s cycle), restarting on each move so a row just reached is red.
/// </summary>
internal sealed class OverlayMenu(SdlApplication application, string fontPath) : IDisposable
{
    public static readonly RenderColor White = new(1, 1, 1, 1), Red = new(0.92f, 0.05f, 0.05f, 1),
        Green = new(0.1f, 0.92f, 0.1f, 1), Yellow = new(0.95f, 0.92f, 0.1f, 1), Cyan = new(0.05f, 0.85f, 0.9f, 1);
    // Stage units (1280x720): the cabinet's 1080p layout at two thirds.
    private const float FontSize = 20, Half = 10.6f, RowSpacing = 30, FirstRow = 150;
    // The cursor's cycle in ticks (measured on the cabinet: 36 frames at 60 Hz), red for the first half.
    private const int BlinkTicks = 36;

    private readonly Dictionary<int, (RenderTextureId? Texture, GlyphMasks Glyph)> _glyphs = [];
    // Window pixels per stage unit (the 1280x720 stage fitted into the window), across and down; glyphs are rasterized
    // at that size and placed on whole pixels, so text stays sharp at any window size.
    private float _scaleX = 1, _scale = 1;
    // A page stays where it was first centred: values that grow (a percentage, an NG's reason) don't move it.
    private OverlayPage? _placed;
    private float _placedShift, _placedValueX;
    private readonly Stack<(OverlayPage Page, int Selection)> _pages = new();
    private RenderTextureId? _white;
    private int _tick, _movedAt;
    // Held arrows repeat (a volume goes 0-100 by ones): after 24 ticks, every 4.
    private SdlKeyboardKey? _repeatKey;
    private int _repeatTicks;

    public bool IsOpen => _pages.Count != 0;

    /// <summary>Home: over the frosted game, with the drum and Escape; else a cabinet's black screen, arrows and Enter only.</summary>
    public bool Home { get; private set; }

    /// <summary>A move (false: a ka) or a pick (true: a don), as the home menus sound them.</summary>
    public Action<bool>? Sound { get; set; }

    /// <summary>A value is being changed.</summary>
    public bool Editing { get; private set; }

    /// <summary>The page on top, null when closed.</summary>
    public OverlayPage? Page => IsOpen ? _pages.Peek().Page : null;

    /// <summary>The selected row among the page's selectable ones.</summary>
    public int Selection
    {
        get => IsOpen ? _pages.Peek().Selection : 0;
        set
        {
            if (!IsOpen) return;
            var (page, old) = _pages.Pop();
            _pages.Push((page, value));
            if (value != old)
                _movedAt = _tick;
        }
    }

    /// <summary>Opens on <paramref name="page"/> (any pages under it go).</summary>
    public void Open(OverlayPage page, bool home)
    {
        _pages.Clear();
        Home = home;
        Push(page);
    }

    public void Push(OverlayPage page)
    {
        _pages.Push((page, 0));
        (Editing, _movedAt) = (false, _tick);
    }

    /// <summary>Back to the page under this one (none: the menu closes).</summary>
    public void Pop()
    {
        if (IsOpen)
            _pages.Pop();
        (Editing, _movedAt) = (false, _tick);
    }

    public void Close() => _pages.Clear();

    /// <summary>
    /// One tick: <paramref name="pulses"/> the keys that went down, <paramref name="held"/> those held; <paramref name="escape"/>
    /// goes back (out of a value, then what the page says, else to the page under it).
    /// </summary>
    public void Tick(SdlKeyboardSnapshot pulses, SdlKeyboardSnapshot held, bool escape)
    {
        _tick++;
        if (Page is not { } page)
            return;
        SdlKeyboardKey[] arrows = [SdlKeyboardKey.Up, SdlKeyboardKey.Down, SdlKeyboardKey.Left, SdlKeyboardKey.Right];
        if (_repeatKey is { } repeating && held.IsDown(repeating))
            _repeatTicks++;
        else
            (_repeatKey, _repeatTicks) = (arrows.FirstOrDefault(key => pulses.IsDown(key)) is var first && pulses.IsDown(first) ? first : null, 0);
        bool key(SdlKeyboardKey key) => pulses.IsDown(key) || key == _repeatKey && _repeatTicks >= 24 && (_repeatTicks - 24) % 4 == 0;
        // Home: the drum's rims (left: up, right: down) and centre (pick), except on a page that tests the drum.
        var drums = Home && !page.DrumTest;
        bool hit(params int[] pads) => drums && pads.Any(index => pulses.IsDown(SdlInputBindings.Pads[index]));
        bool leftRim = hit(0, 4), rightRim = hit(3, 7),
            centre = hit(1, 2, 5, 6) || pulses.IsDown(SdlKeyboardKey.Enter) || Home && pulses.IsDown(SdlKeyboardKey.Space);
        if (escape)
        {
            if (Editing)
                Editing = false;
            else if (page.Back is { } back)
                back();
            else
                Pop();
            return;
        }
        var rows = page.Rows();
        var selectable = Enumerable.Range(0, rows.Length).Where(index => rows[index].Selectable).ToArray();
        if (selectable.Length == 0)
            return;
        var selection = Math.Clamp(Selection, 0, selectable.Length - 1);
        var row = rows[selectable[selection]];
        if (Editing)
        {
            // The arrows first: outside a song the shell also presses a rim for each.
            var step = key(SdlKeyboardKey.Right) || pulses.IsDown(SdlKeyboardKey.Up) || pulses.IsDown(SdlKeyboardKey.WheelUp) ? 1
                : key(SdlKeyboardKey.Left) || pulses.IsDown(SdlKeyboardKey.Down) || pulses.IsDown(SdlKeyboardKey.WheelDown) ? -1
                : rightRim ? 1 : leftRim ? -1 : 0;
            if (step != 0)
            {
                Sound?.Invoke(false);
                row.Step!(step);
                _movedAt = _tick;
            }
            if (centre)
            {
                Sound?.Invoke(true);
                Editing = false;
            }
            return;
        }
        var move = key(SdlKeyboardKey.Up) || leftRim || pulses.IsDown(SdlKeyboardKey.WheelUp) ? -1
            : key(SdlKeyboardKey.Down) || rightRim || pulses.IsDown(SdlKeyboardKey.WheelDown) ? 1 : 0;
        if (move != 0)
        {
            Sound?.Invoke(false);
            Selection = (selection + move + selectable.Length) % selectable.Length;
            _movedAt = _tick;
            return;
        }
        if (!centre)
            return;
        Sound?.Invoke(true);
        _movedAt = _tick;
        if (row.Step is not null)
            Editing = true;
        else
            row.Enter!();
    }

    public RenderFrame Frame() => new(RenderColor.Black, Quads(), 1280d / 720d);

    /// <summary>The page drawn (home: over the game, which the renderer frosts, or a dark veil where it can't).</summary>
    public List<RenderQuad> Quads()
    {
        var quads = new List<RenderQuad>();
        if (Page is not { } page)
            return quads;
        var (pixelWidth, pixelHeight) = application.GetPixelSize();
        var viewport = RenderViewport.AspectFit((uint)Math.Max(pixelWidth, 1), (uint)Math.Max(pixelHeight, 1), 1280d / 720d);
        if (viewport.Height / 720f != _scale)
            releaseGlyphs(); // the window was resized: rasterize again at the new size
        (_scaleX, _scale) = (viewport.Width / 1280f, viewport.Height / 720f);
        if (Home && !application.BlursBackdrop)
        {
            _white ??= application.UploadRgba8(1, 1, [255, 255, 255, 255]);
            quads.Add(RenderQuad.FromRectangles(_white.Value, new(0, 0, 1, 1), RenderRectangle.Full,
                new RenderColor(0, 0, 0, 0.92f), RenderColor.Transparent));
        }
        var red = (_tick - _movedAt) % BlinkTicks < BlinkTicks / 2;
        var rows = page.Rows();
        var selectable = Enumerable.Range(0, rows.Length).Where(index => rows[index].Selectable).ToList();
        var selected = selectable.Count == 0 ? -1 : selectable[Math.Clamp(Selection, 0, selectable.Count - 1)];
        if (page.Centred)
        {
            // The message's lines (each wrapped), the title above them, then a window of the choices around the
            // selection (a long list, the public rooms, scrolls with arrows at its ends).
            var lines = page.Body?.Invoke() is { } body
                ? body.SelectMany(line => wrapped(upper(line.Text), 100).Select(part => (Text: part, line.Colour))).Take(8).ToList()
                : page.Message?.Invoke() is { } message
                    ? message.Split('\n').SelectMany(line => wrapped(upper(line), 100)).Take(5).Select(static part => (Text: part, Colour: White)).ToList()
                    : [];
            var titleY = lines.Count > 2 ? 120 : 200f;
            centred(quads, upper(page.Title()), titleY, White);
            for (var line = 0; line < lines.Count; line++)
                centred(quads, lines[line].Text, titleY + 50 + line * 28, lines[line].Colour);
            var top = lines.Count == 0 ? 290f : titleY + 50 + lines.Count * 28 + 40;
            const float Gap = 40;
            var visible = Math.Max(1, (int)((600 - top) / Gap) + 1);
            var first = Math.Clamp(selected - visible / 2, 0, Math.Max(0, rows.Length - visible));
            for (var index = first; index < Math.Min(rows.Length, first + visible); index++)
                centred(quads, upper(rows[index].Label), top + (index - first) * Gap, index == selected && red ? Red : White);
            if (first > 0)
                centred(quads, "▲", top - Gap * 0.6f, White);
            if (first + visible < rows.Length)
                centred(quads, "▼", top + visible * Gap - Gap * 0.4f, White);
        }
        else
        {
            centred(quads, page.Title(), 60, White);
            if (page.Defaults)
            {
                var defaults = upper(Strings.T("service.default_in_green"));
                var x = 640 - (width(defaults) + Half * 2) / 2;
                text(quads, "]", text(quads, defaults, text(quads, "[", x, 90, White), 90, Green), 90, White);
            }
            // A long page (the main menu) closes its rows up to stay above the hint lines and the footer.
            var slots = rows.Length + rows.Skip(1).Count(static row => row.Gap);
            var bottom = rows.Any(static row => row.Hint.Length > 0) ? 560 : 610;
            var spacing = Math.Min(RowSpacing, (bottom - FirstRow) / (float)Math.Max(1, slots - 1));
            var values = rows.Select(static row => row.Value?.Invoke()).ToArray();
            var (shift, valueX, blockWidth) = page.Fixed ? (0f, (float?)null, float.MaxValue) : block(rows, values);
            if (!page.Fixed)
            {
                if (!ReferenceEquals(page, _placed))
                    (_placed, _placedShift, _placedValueX) = (page, shift, float.MinValue);
                // The value column only moves right, when a longer label arrives (a row added later), never the labels.
                if (valueX is { } x)
                    _placedValueX = Math.Max(_placedValueX, x - shift);
                (shift, valueX) = (_placedShift, valueX is null ? null : _placedValueX + _placedShift);
            }
            var y = FirstRow;
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (row.Gap && index > 0)
                    y += spacing;
                // EXIT rows are red (blinking white when selected); cyan rows are information or, as OTHERS, the language.
                // ponytail: EXIT known by its label; a row flag if a page names its way out otherwise.
                var exit = row.Label == ExitLabel;
                var colour = index == selected && (Editing || red) ? (exit ? White : Red) : exit ? Red : row.Cyan ? Cyan : White;
                // A line wider than the block (the sound test's note) is centred on its own.
                if (values[index] is null && !row.Selectable && width(row.Label) > blockWidth)
                    centred(quads, row.Label, y, colour);
                else
                    text(quads, row.Label, row.X + shift, y, colour);
                if (values[index] is { } value)
                {
                    var accent = row.Colour?.Invoke() ?? (row.Cyan ? Cyan : row.IsDefault is null ? Green : row.IsDefault() ? Green : Yellow);
                    if (index == selected && Editing && !red)
                        accent = Red;
                    var x = valueX ?? row.ValueX;
                    // A plain information value (a count) is white; a setting's, or a {braced} part, takes the colour.
                    foreach (var (part, coloured) in segments(value, whole: row.IsDefault is not null))
                        x = text(quads, part, x, y, coloured ? accent : row.Cyan ? Cyan : White);
                }
                y += spacing;
            }
            // The selected row's hint, in up to two lines.
            if (selected >= 0 && rows[selected].Hint is { Length: > 0 } hint)
            {
                var lines = wrapped(hint, 100);
                for (var line = 0; line < Math.Min(2, lines.Count); line++)
                    centred(quads, lines[line], 616 + line * 26, White); // a free line under the rows (the EXIT)
            }
        }
        if (page.Footer?.Invoke() is { } footerLines)
            for (var line = 0; line < footerLines.Length; line++)
                centred(quads, footerLines[line].Text, 690 - (footerLines.Length - 1 - line) * 30, footerLines[line].Colour);
        else if (page.Legend)
            centred(quads, footer(page), 690, White); // under the hint's two lines
        return quads;
    }

    // The block of a page's rows: the value column three cells after the longest label of a row with a value, and the
    // shift that centres labels and values together (the rows keep their indents from one another).
    private static (float Shift, float? ValueX, float Width) block(OverlayRow[] rows, string?[] values)
    {
        var left = rows.Min(static row => row.X);
        var valued = Enumerable.Range(0, rows.Length).Where(index => values[index] is not null).ToArray();
        float? valueX = valued.Length == 0 ? null : valued.Max(index => rows[index].X + width(rows[index].Label)) + Half * 3;
        var right = rows.Where(static row => row.Selectable).Select(static row => row.X + width(row.Label)).DefaultIfEmpty(left)
            .Concat(valued.Select(index => valueX!.Value + width(values[index]!.Replace("{", "").Replace("}", ""))))
            .Concat(valued.Select(index => rows[index].X + width(rows[index].Label))).Max();
        var shift = 640 - (left + right) / 2;
        return (shift, valueX + shift, right - left);
    }

    private static string ExitLabel => upper(Strings.T("service.exit"));

    private string footer(OverlayPage page) => upper(Strings.T(!Home
        ? Editing ? "service.legend.cabinet_editing" : "service.legend.cabinet"
        : page.DrumTest ? "service.legend.drum_test"
        : Editing ? "service.legend.home_editing"
        : "service.legend.home"));

    public static string upper(string text) => text.ToUpperInvariant();

    // Lines of at most `cells` half-width cells, broken at the last space (anywhere in Japanese, which has none).
    private static List<string> wrapped(string text, int cells)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var rune in text.EnumerateRunes())
        {
            if (width(line + rune) > cells * Half)
            {
                var space = line.LastIndexOf(' ');
                (var done, line) = space > 0 ? (line[..space], line[(space + 1)..]) : (line, "");
                lines.Add(done);
            }
            line += rune.ToString();
        }
        lines.Add(line);
        return lines;
    }

    // "1 COIN AS {1} CREDIT(S)": the braced part takes the value colour; a value without braces is coloured whole
    // (when it is a setting's).
    private static IEnumerable<(string Text, bool Coloured)> segments(string value, bool whole)
    {
        if (!value.Contains('{'))
        {
            yield return (value, whole);
            yield break;
        }
        var parts = value.Split('{', '}');
        for (var index = 0; index < parts.Length; index++)
            if (parts[index].Length != 0)
                yield return (parts[index], index % 2 == 1);
    }

    private void centred(List<RenderQuad> quads, string value, float baseline, RenderColor colour) =>
        text(quads, value, 640 - width(value) / 2, baseline, colour);

    private static float width(string value) => value.EnumerateRunes().Sum(static rune => wide(rune) ? Half * 2 : Half);

    // Full-width characters (kana, kanji, full-width forms) take two cells.
    private static bool wide(Rune rune) => rune.Value >= 0x1100 && rune.Value is not (>= 0xFF61 and <= 0xFFDC);

    // A fixed-pitch line from its baseline, each glyph centred in its cell (the cabinet's font is monospaced); returns
    // where the line ends.
    private float text(List<RenderQuad> quads, string value, float x, float baseline, RenderColor colour)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var cell = wide(rune) ? Half * 2 : Half;
            if (!Rune.IsWhiteSpace(rune) && glyph(rune.Value) is ({ } texture, var masks))
            {
                // Whole window pixels: the glyph's texels land 1:1 on the screen's.
                var left = MathF.Round((x + (cell - masks.Advance / _scale) / 2) * _scaleX) + masks.Left;
                var top = MathF.Round(baseline * _scale) - masks.Top;
                quads.Add(RenderQuad.FromRectangles(texture,
                    new(left / _scaleX / 1280, top / _scale / 720, masks.Width / _scaleX / 1280, masks.Height / _scale / 720),
                    RenderRectangle.Full, colour, RenderColor.Transparent));
            }
            x += cell;
        }
        return x;
    }

    // Each character rasterized once per window size: white with its coverage as alpha (straight, as the quad shader takes it),
    // tinted when drawn.
    private (RenderTextureId? Texture, GlyphMasks Glyph) glyph(int scalar)
    {
        if (_glyphs.TryGetValue(scalar, out var cached))
            return cached;
        var masks = NativeVerticalTextRasterizer.RenderGlyph(fontPath, scalar, FontSize * _scale, 0);
        RenderTextureId? texture = null;
        if (masks.Width > 0 && masks.Height > 0)
        {
            var pixels = new byte[masks.Fill.Length * 4];
            for (var index = 0; index < masks.Fill.Length; index++)
                (pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2], pixels[index * 4 + 3]) = (255, 255, 255, masks.Fill[index]);
            texture = application.UploadRgba8((uint)masks.Width, (uint)masks.Height, pixels);
        }
        return _glyphs[scalar] = (texture, masks);
    }

    private void releaseGlyphs()
    {
        foreach (var (texture, _) in _glyphs.Values)
            if (texture is { } id)
                application.ReleaseTexture(id);
        _glyphs.Clear();
    }

    public void Dispose()
    {
        releaseGlyphs();
        if (_white is { } white)
            application.ReleaseTexture(white);
    }
}
