using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Home;

namespace Waddamburo.App.Presentation;

/// <summary>
/// What the host draws over the entry for home players: the account name over each drum's name board
/// (the board's own name glyphs are kana only), and during the player setup the option text that stands
/// in for a Don (Sign in, Join with code), a code while pairing, and a status line. Text is the game
/// font, rasterized once per content and window scale. (The arrows are the entry's own, SetupArrows.)
/// </summary>
internal sealed class EntrySetupOverlay(SdlApplication application, string fontPath) : IDisposable
{
    // Stage positions (1280x720), per side: the Don on its stand, the board's name line, the status line.
    private static readonly float[] StandX = [150, 1130];
    private const float StandY = 420, QrY = 368, QrSize = 150;
    private static readonly float[] TagX = [222, 1112];
    private const float TagY = 683, TagWidth = 176, TagHeight = 24;
    private const float MessageY = 560;

    private readonly Dictionary<(string Text, int Width, int Height, bool Tag, uint Scale), RenderTextureId> _text = [];
    private readonly Dictionary<(string Url, uint Scale), (RenderTextureId Texture, float Size)> _qr = [];

    /// <param name="setup">Each side's setup column while the setup is open (null: not in setup).</param>
    /// <param name="tags">Each side's name for its board (null: no board).</param>
    /// <param name="standVisible">Per side, whether the stand's text may show (not while costume smoke covers it).</param>
    public IEnumerable<RenderQuad> Quads(IReadOnlyList<SetupColumn>? setup, IReadOnlyList<string?> tags, IReadOnlyList<bool> standVisible)
    {
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        var quads = new List<RenderQuad>();
        for (var side = 0; side < 2; side++)
        {
            if (tags[side] is { Length: > 0 } tag)
                quads.Add(text(tag, TagX[side], TagY, TagWidth, TagHeight, tagStyle: true, scale));
            if (setup?[side] is not { } column)
                continue;
            var choice = column.Choice;
            // The stand: a code while pairing, else the option's text where a Don would be.
            if (standVisible[side] && column.Code is { } code)
            {
                // The QR code (the page to enter it on, code filled in), the code itself under it for typing,
                // and the seconds left beside that.
                if (column.QrUrl is { } url)
                    quads.Add(qr(url, StandX[side], QrY, QrSize, scale));
                var codeY = column.QrUrl is null ? StandY - 20 : QrY + QrSize / 2 + 26;
                quads.Add(text($"{code[..3]}-{code[3..]}", StandX[side], codeY, 170, 42, tagStyle: false, scale));
                if (column.Seconds is { } seconds)
                    quads.Add(text($"{seconds}", StandX[side] + 112, codeY, 44, 30, tagStyle: false, scale));
            }
            else if (standVisible[side] && !choice.HasDon)
            {
                if (choice.Kind == SetupChoiceKind.AddAccount)
                {
                    quads.Add(text("+", StandX[side], StandY - 35, 120, 110, tagStyle: false, scale));
                    quads.Add(text(choice.Label, StandX[side], StandY + 45, 240, 44, tagStyle: false, scale));
                }
                else
                {
                    // Two lines, so it fits between the arrows.
                    quads.Add(text("Join with", StandX[side], StandY - 26, 200, 48, tagStyle: false, scale));
                    quads.Add(text("code", StandX[side], StandY + 28, 200, 48, tagStyle: false, scale));
                }
            }
            if (column.Message is { } message)
                quads.Add(text(message, StandX[side], MessageY, 300, 26, tagStyle: false, scale));
        }
        return quads;
    }

    public void Dispose()
    {
        foreach (var texture in _text.Values)
            application.ReleaseTexture(texture);
        _text.Clear();
        foreach (var (texture, _) in _qr.Values)
            application.ReleaseTexture(texture);
        _qr.Clear();
    }

    // Text centred in a box in the game's outlined title style (white in black); the name tag's letters
    // spaced out like the board's own lettering.
    private RenderQuad text(string value, float centreX, float centreY, float width, float height, bool tagStyle, uint scale)
    {
        var key = (value, (int)width, (int)height, tagStyle, scale);
        if (!_text.TryGetValue(key, out var texture))
        {
            var canvas = new VectorCanvas((int)width, (int)height, (int)scale, fontPath);
            canvas.Text(value, width / 2, height / 2, width, height, spacing: tagStyle ? 0.06f : 0);
            _text[key] = texture = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        }
        return quad(texture, centreX - width / 2, centreY - height / 2, width, height, RenderRectangle.Full);
    }

    // A QR code in whole pixels per module at this window scale (with its white quiet zone), drawn at
    // most `size` stage units wide.
    private RenderQuad qr(string url, float centreX, float centreY, float size, uint scale)
    {
        if (!_qr.TryGetValue((url, scale), out var entry))
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var modules = data.ModuleMatrix;
            var count = modules.Count;
            var pixel = Math.Max(1, (int)(size * scale / count));
            var side = count * pixel;
            var pixels = new byte[side * side * 4];
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                {
                    var dark = modules[y / pixel][x / pixel];
                    var at = (y * side + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = dark ? (byte)0 : (byte)255;
                    pixels[at + 3] = 255;
                }
            entry = (application.UploadRgba8((uint)side, (uint)side, pixels), side / (float)scale);
            _qr[(url, scale)] = entry;
        }
        return RenderQuad.FromRectangles(entry.Texture, new RenderRectangle((centreX - entry.Size / 2) / 1280f,
                (centreY - entry.Size / 2) / 720f, entry.Size / 1280f, entry.Size / 720f), RenderRectangle.Full,
            RenderColor.White, RenderColor.Transparent, RenderSampling.Nearest);
    }

    private static RenderQuad quad(RenderTextureId texture, float x, float y, float width, float height, RenderRectangle uv) =>
        RenderQuad.FromRectangles(texture, new RenderRectangle(x / 1280f, y / 720f, width / 1280f, height / 720f),
            uv, RenderColor.White, RenderColor.Transparent);
}
