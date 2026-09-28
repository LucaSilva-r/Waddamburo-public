using Waddamburo.App.Flow;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>Home-mode controls, drawn with artwork decoded at runtime from the player's archives.</summary>
internal sealed class HomePauseOverlay : IDisposable
{
    // Texture ordinals are observed archive facts; the layout is Waddamburo's own.
    private const int Panel = 0, Badge = 11, Arrow = 1103, Button = 1352, SelectedButton = 1354;
    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly Dictionary<int, (RenderTextureId Texture, int Width, int Height)> _art = [];
    private readonly Dictionary<(string Text, int Width, int Height, uint Scale, float Anchor), RenderTextureId> _text = [];
    private readonly RenderTextureId _white;

    public HomePauseOverlay(SdlApplication application, string fontPath, string assetRoot)
    {
        _application = application;
        _fontPath = fontPath;
        _white = application.UploadRgba8(1, 1, [255, 255, 255, 255]);
        load(Path.Combine(assetRoot, "entry_info", "packeddata.ddp"), [0, 11], 0);
        load(Path.Combine(assetRoot, "song_select", "packeddata.ddp"), [103, 352, 354], 1000);
    }

    public IEnumerable<RenderQuad> Quads(HomeMenu? menu, float black)
    {
        var quads = new List<RenderQuad>();
        if (menu is not null)
        {
            quads.Add(rect(_white, 0, 0, 1280, 720, new(0, 0, 0, 0.6f)));
            quads.Add(_art.TryGetValue(Panel, out var panel)
                ? rect(panel.Texture, 140, 98, 1000, 520)
                : rect(_white, 140, 98, 1000, 520, new(1, 0.98f, 0.91f, 1)));
            if (_art.TryGetValue(Badge, out var badge))
                quads.Add(rect(badge.Texture, 460, 143, 58, 58));
            quads.Add(label(menu.Title, 640, 174, 260, 60));
            if (menu.SettingsPage)
                settings(quads, menu);
            else
                choices(quads, menu);
        }
        if (black > 0)
            quads.Add(rect(_white, 0, 0, 1280, 720, new(0, 0, 0, Math.Clamp(black, 0, 1))));
        return quads;
    }

    // The pause choices: a few big buttons.
    private void choices(List<RenderQuad> quads, HomeMenu menu)
    {
        var rows = menu.Rows;
        const float spacing = 88, height = 69;
        // Centred between the title (ends at y 200) and the panel's inner bottom (592).
        var top = 396 - ((rows.Length - 1) * spacing + height) / 2;
        for (var index = 0; index < rows.Length; index++)
        {
            var y = top + index * spacing;
            button(quads, 405, y, 470, height, index == menu.Selection);
            if (index == menu.Selection && _art.TryGetValue(Arrow, out var arrow))
                quads.Add(rect(arrow.Texture, 396 - height * 0.7f, y + height * 0.15f, height * 0.7f, height * 0.7f));
            quads.Add(label(rows[index], 640, y + height / 2, 470, (int)(height * 0.76f)));
        }
    }

    // Settings: a scrolling list (label left, value right) and the selected row's hint under it.
    private const int VisibleRows = 6;
    private const float ListX = 250, ListWidth = 780, ListTop = 222, RowHeight = 40, RowSpacing = 46;

    private void settings(List<RenderQuad> quads, HomeMenu menu)
    {
        var items = menu.Items;
        var first = Math.Clamp(menu.Selection - VisibleRows / 2, 0, Math.Max(0, items.Length - VisibleRows));
        for (var row = 0; row < Math.Min(VisibleRows, items.Length); row++)
        {
            var index = first + row;
            var item = items[index];
            var selected = index == menu.Selection;
            var y = ListTop + row * RowSpacing;
            if (item.Kind == HomeMenu.ItemKind.Header)
            {
                // A section title on the panel, with a rule under it.
                quads.Add(label(item.Label, ListX + 4, y + RowHeight / 2 + 2, 300, 34, anchor: 0));
                quads.Add(rect(_white, ListX + 4, y + RowHeight - 1, ListWidth - 8, 3, new(0.45f, 0.3f, 0.1f, 0.5f)));
                continue;
            }
            if (item.Kind == HomeMenu.ItemKind.Library)
                status(quads, ListX, y, ListWidth, RowHeight, item.State, selected);
            else
                button(quads, ListX, y, ListWidth, RowHeight, selected);
            if (selected && _art.TryGetValue(Arrow, out var arrow))
                quads.Add(rect(arrow.Texture, ListX - 34, y + 6, 28, 28));
            if (item.Value is null)
            {
                quads.Add(label(item.Label, ListX + ListWidth / 2, y + RowHeight / 2, 400, 30));
                continue;
            }
            quads.Add(label(item.Label, ListX + 22, y + RowHeight / 2, 420, 30, anchor: 0));
            var value = selected && menu.Editing ? $"<  {item.Value}  >" : item.Value;
            quads.Add(label(value, ListX + ListWidth - 22, y + RowHeight / 2, 320, 30, anchor: 1));
        }
        // Scrollbar: the visible part of the list.
        if (items.Length > VisibleRows)
        {
            var track = (VisibleRows - 1) * RowSpacing + RowHeight;
            quads.Add(rect(_white, ListX + ListWidth + 14, ListTop, 6, track, new(0, 0, 0, 0.15f)));
            quads.Add(rect(_white, ListX + ListWidth + 14, ListTop + track * first / items.Length, 6,
                track * VisibleRows / items.Length, new(0.45f, 0.3f, 0.1f, 0.9f)));
        }
        // Wrapped to short lines (the title rasterizer squeezes long ones).
        var hint = wrap(items[menu.Selection].Hint).ToArray();
        for (var line = 0; line < hint.Length; line++)
            quads.Add(label(hint[line], 640, 550 + (line - (hint.Length - 1) / 2f) * 29, 860, 27));
    }

    // Splits at the space nearest the middle until every line is short.
    private static IEnumerable<string> wrap(string text)
    {
        const int Short = 56;
        var middle = text.Length / 2;
        var space = text.Length <= Short ? -1
            : new[] { text.LastIndexOf(' ', middle), text.IndexOf(' ', middle) }
                .Where(static index => index > 0).OrderBy(index => Math.Abs(index - middle)).FirstOrDefault(-1);
        return space < 0 ? [text] : wrap(text[..space]).Concat(wrap(text[(space + 1)..]));
    }

    // A song library's button in its state's colour (red, orange, green); the selected one lighter
    // and framed in the selection yellow.
    private void status(List<RenderQuad> quads, float x, float y, float width, float height, HomeMenu.LibraryState state,
        bool selected)
    {
        var (r, g, b) = state switch
        {
            HomeMenu.LibraryState.Ready => (0.18f, 0.6f, 0.24f),
            HomeMenu.LibraryState.Warning => (0.92f, 0.52f, 0.08f),
            _ => (0.8f, 0.16f, 0.14f),
        };
        var lift = selected ? 0.18f : 0;
        quads.Add(rect(_white, x, y, width, height, new(0.1f, 0.06f, 0.02f, 1)));
        quads.Add(rect(_white, x + 3, y + 3, width - 6, height - 6, new(r + lift, g + lift, b + lift, 1)));
        if (!selected)
            return;
        var yellow = new RenderColor(1, 0.87f, 0.2f, 1);
        quads.Add(rect(_white, x - 3, y - 3, width + 6, 3, yellow));
        quads.Add(rect(_white, x - 3, y + height, width + 6, 3, yellow));
        quads.Add(rect(_white, x - 3, y, 3, height, yellow));
        quads.Add(rect(_white, x + width, y, 3, height, yellow));
    }

    private void button(List<RenderQuad> quads, float x, float y, float width, float height, bool selected)
    {
        if (!_art.TryGetValue(selected ? SelectedButton : Button, out var art) || art.Width < 32)
        {
            quads.Add(rect(_white, x, y, width, height, selected
                ? new RenderColor(0.19f, 0.88f, 1, 1) : new RenderColor(0.16f, 0.45f, 0.65f, 1)));
            return;
        }
        var edge = 16f * height / art.Height;
        var u = 16f / art.Width;
        quads.Add(rect(art.Texture, x, y, edge, height, uv: new(0, 0, u, 1)));
        quads.Add(rect(art.Texture, x + edge, y, width - edge * 2, height,
            uv: new(0.5f, 0, 1f / art.Width, 1)));
        quads.Add(rect(art.Texture, x + width - edge, y, edge, height,
            uv: new(1 - u, 0, u, 1)));
    }

    // Text in a box: centred (anchor 0.5) on x, or starting (0) or ending (1) there.
    private RenderQuad label(string value, float x, float centreY, int width, int height, float anchor = 0.5f)
    {
        var scale = (uint)Math.Clamp((_application.GetPixelSize().Height + 719) / 720, 1, 3);
        // ponytail: every setting value gets its own cached texture (a few hundred at most), freed on exit.
        var key = (value, width, height, scale, anchor);
        if (!_text.TryGetValue(key, out var texture))
        {
            var canvas = new VectorCanvas(width, height, (int)scale, _fontPath);
            canvas.Text(value, width * anchor, height / 2f, width, height, anchorX: anchor);
            _text[key] = texture = _application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        }
        return rect(texture, x - width * anchor, centreY - height / 2f, width, height);
    }

    private void load(string path, int[] indices, int keyOffset)
    {
        if (!File.Exists(path)) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var archive = DdpArchiveIndex.Parse(bytes);
            foreach (var index in indices)
            {
                if ((uint)index >= (uint)archive.Textures.Length) continue;
                var entry = archive.Textures[index];
                var file = NutFile.Parse(bytes.AsMemory(checked((int)entry.Offset), checked((int)entry.Length)));
                if (file.Textures.Length == 0) continue;
                var texture = file.Textures[0];
                var rgba = NutTextureDecoder.DecodeRgba8(texture);
                _art[keyOffset + index] = (_application.UploadRgba8(
                    checked((uint)texture.Width), checked((uint)texture.Height), rgba), texture.Width, texture.Height);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException
            or ArgumentException)
        {
            Console.Error.WriteLine($"Pause artwork unavailable: {exception.Message}");
        }
    }

    private static RenderQuad rect(RenderTextureId texture, float x, float y, float width, float height,
        RenderColor? tint = null, RenderRectangle? uv = null) =>
        RenderQuad.FromRectangles(texture,
            new(x / 1280, y / 720, width / 1280, height / 720), uv ?? RenderRectangle.Full,
            tint ?? RenderColor.White, RenderColor.Transparent);

    public void Dispose()
    {
        _application.ReleaseTexture(_white);
        foreach (var entry in _art.Values) _application.ReleaseTexture(entry.Texture);
        foreach (var texture in _text.Values) _application.ReleaseTexture(texture);
    }
}
