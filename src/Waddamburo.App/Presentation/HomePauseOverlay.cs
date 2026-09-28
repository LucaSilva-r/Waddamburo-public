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
    private readonly Dictionary<(string Text, int Width, int Height, uint Scale), RenderTextureId> _text = [];
    private readonly RenderTextureId _white;

    public HomePauseOverlay(SdlApplication application, string fontPath, string assetRoot)
    {
        _application = application;
        _fontPath = fontPath;
        _white = application.UploadRgba8(1, 1, [255, 255, 255, 255]);
        load(Path.Combine(assetRoot, "entry_info", "packeddata.ddp"), [0, 11], 0);
        load(Path.Combine(assetRoot, "song_select", "packeddata.ddp"), [103, 352, 354], 1000);
    }

    public IEnumerable<RenderQuad> Quads(bool paused, int selection, float black)
    {
        var quads = new List<RenderQuad>();
        if (paused)
        {
            quads.Add(rect(_white, 0, 0, 1280, 720, new(0, 0, 0, 0.6f)));
            quads.Add(_art.TryGetValue(Panel, out var panel)
                ? rect(panel.Texture, 140, 98, 1000, 520)
                : rect(_white, 140, 98, 1000, 520, new(1, 0.98f, 0.91f, 1)));
            if (_art.TryGetValue(Badge, out var badge))
                quads.Add(rect(badge.Texture, 460, 143, 58, 58));
            quads.Add(label("Paused", 640, 174, 260, 60));
            string[] choices = ["Resume", "Restart Song", "Song Select"];
            for (var index = 0; index < choices.Length; index++)
            {
                var y = 247 + index * 88;
                button(quads, 405, y, 470, 68, index == selection);
                if (index == selection && _art.TryGetValue(Arrow, out var arrow))
                    quads.Add(rect(arrow.Texture, 348, y + 10, 48, 48));
                quads.Add(label(choices[index], 640, y + 35, 470, 52));
            }
        }
        if (black > 0)
            quads.Add(rect(_white, 0, 0, 1280, 720, new(0, 0, 0, Math.Clamp(black, 0, 1))));
        return quads;
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

    private RenderQuad label(string value, float centreX, float centreY, int width, int height)
    {
        var scale = (uint)Math.Clamp((_application.GetPixelSize().Height + 719) / 720, 1, 3);
        var key = (value, width, height, scale);
        if (!_text.TryGetValue(key, out var texture))
        {
            var canvas = new VectorCanvas(width, height, (int)scale, _fontPath);
            canvas.Text(value, width / 2f, height / 2f, width, height);
            _text[key] = texture = _application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        }
        return rect(texture, centreX - width / 2f, centreY - height / 2f, width, height);
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
