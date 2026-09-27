using Waddamburo.App.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The "who's playing?" screen, full screen over the paused attract loop: a warm background with the
/// title band, and one column per drum (1P red, 2P blue) showing its choice on a big card with arrows,
/// a READY ribbon once locked in, the pairing/login code while one is shown, and a status line. The
/// background and each column are separate textures, rebuilt only when they change.
/// </summary>
internal sealed class PlayerSetupView(SdlApplication application, string fontPath) : IDisposable
{
    private const float ColumnWidth = 500, ColumnHeight = 520, ColumnTop = 130;
    private static readonly float[] ColumnLeft = [100, 680];
    private static readonly (byte R, byte G, byte B) Cream = (248, 241, 223), White = (255, 255, 255),
        Yellow = (254, 205, 1), Red = (249, 71, 40), Blue = (58, 150, 226), Green = (80, 200, 90),
        Grey = (190, 185, 175), Dark = (60, 30, 20);

    private (RenderTextureId Texture, uint Scale)? _background;
    private readonly (RenderTextureId Texture, SetupColumn Column, uint Scale)?[] _columns = new (RenderTextureId, SetupColumn, uint)?[2];

    public IEnumerable<RenderQuad> Quads(IReadOnlyList<SetupColumn> columns)
    {
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_background is not { } background || background.Scale != scale)
        {
            release(_background?.Texture);
            _background = (upload(drawBackground((int)scale), 1280, 720, scale), scale);
        }
        var quads = new List<RenderQuad> { quad(_background.Value.Texture, 0, 0, 1280, 720) };
        for (var side = 0; side < 2; side++)
        {
            if (_columns[side] is not { } cached || cached.Column != columns[side] || cached.Scale != scale)
            {
                release(_columns[side]?.Texture);
                _columns[side] = (upload(drawColumn(side, columns[side], (int)scale), ColumnWidth, ColumnHeight, scale),
                    columns[side], scale);
            }
            quads.Add(quad(_columns[side]!.Value.Texture, ColumnLeft[side], ColumnTop, ColumnWidth, ColumnHeight));
        }
        return quads;
    }

    public void Dispose()
    {
        release(_background?.Texture);
        foreach (var column in _columns)
            release(column?.Texture);
    }

    private byte[] drawBackground(int scale)
    {
        var canvas = new VectorCanvas(1280, 720, scale, fontPath);
        // A warm vertical gradient (the entry's evening colours), then the title band and the key hint.
        for (var y = 0; y < canvas.PixelHeight; y++)
        {
            var t = y / (float)canvas.PixelHeight;
            (byte, byte, byte) colour = ((byte)(232 - 90 * t), (byte)(92 - 62 * t), (byte)(52 - 30 * t));
            for (var x = 0; x < canvas.PixelWidth; x++)
                canvas.Blend(x, y, colour, 1);
        }
        canvas.RoundedRect(340, 22, 600, 84, 42, Yellow, outline: 5);
        canvas.Text("Who's playing?", 640, 64, 540, 56);
        canvas.Text("Rims: choose    Centre: decide    Rim again: change    Esc: back to the title",
            640, 684, 1180, 30, 0x000000);
        return canvas.Pixels;
    }

    private byte[] drawColumn(int side, SetupColumn column, int scale)
    {
        var canvas = new VectorCanvas((int)ColumnWidth, (int)ColumnHeight, scale, fontPath);
        var accent = side == 0 ? Red : Blue;
        var choice = column.Choice;
        var joined = choice.Kind != SetupChoiceKind.NotPlaying;
        // The card, then the player tag over its top edge.
        canvas.RoundedRect(20, 30, ColumnWidth - 40, 400, 30, joined ? (choice.Kind == SetupChoiceKind.Account ? White : Cream) : Grey,
            outline: 6, column.Ready ? accent : Dark);
        canvas.RoundedRect(ColumnWidth / 2 - 60, 4, 120, 56, 28, accent, outline: 4);
        canvas.Text(side == 0 ? "1P" : "2P", ColumnWidth / 2, 32, 90, 44);
        // Arrows while choosing.
        if (!column.Ready && column.Code is null)
        {
            canvas.Triangle((52, 230), (82, 205), (82, 255), accent);
            canvas.Triangle((ColumnWidth - 52, 230), (ColumnWidth - 82, 205), (ColumnWidth - 82, 255), accent);
        }
        // The choice's picture: an avatar, the pairing/login code, or a symbol.
        const float artY = 200, artSize = 250;
        if (column.Code is { } code)
        {
            canvas.RoundedRect(90, artY - 45, ColumnWidth - 180, 90, 45, Yellow, outline: 5);
            canvas.Text($"{code[..3]}-{code[3..]}", ColumnWidth / 2, artY, ColumnWidth - 220, 70);
            if (column.Seconds is { } seconds)
                canvas.Text($"{seconds}", ColumnWidth / 2, artY + 90, 120, 40, 0x000000);
        }
        else if (choice.Avatar is { } avatar)
            canvas.Image(avatar, null, ColumnWidth / 2, artY, artSize, artSize);
        else
            canvas.Text(choice.Kind switch
            {
                SetupChoiceKind.NotPlaying => "...",
                SetupChoiceKind.Guest => "?",
                SetupChoiceKind.Friend => "123-456",
                SetupChoiceKind.AddAccount => "+",
                _ => choice.Label[..1],
            }, ColumnWidth / 2, artY, 300, 140);
        canvas.Text(choice.Label, ColumnWidth / 2, 380, ColumnWidth - 100, 50);
        if (choice.IsDefault && !column.Ready)
        {
            canvas.RoundedRect(40, 50, 90, 32, 16, Yellow, outline: 3);
            canvas.Text("AUTO", 85, 66, 70, 22);
        }
        // Locked in: a READY ribbon (STARTING once everyone is); otherwise the status line under the card.
        if (column.Ready)
        {
            canvas.RoundedRect(90, 440, ColumnWidth - 180, 64, 32, Green, outline: 5);
            canvas.Text(column.Message == "Starting..." ? "STARTING!" : "READY!", ColumnWidth / 2, 472, ColumnWidth - 220, 48);
        }
        else if (column.Message is { } message)
            canvas.Text(message, ColumnWidth / 2, 470, ColumnWidth - 20, 34);
        return canvas.Pixels;
    }

    private RenderTextureId upload(byte[] pixels, float width, float height, uint scale) =>
        application.UploadRgba8((uint)(width * scale), (uint)(height * scale), pixels);

    private void release(RenderTextureId? texture)
    {
        if (texture is { } value)
            application.ReleaseTexture(value);
    }

    private static RenderQuad quad(RenderTextureId texture, float x, float y, float width, float height) =>
        RenderQuad.FromRectangles(texture, new RenderRectangle(x / 1280f, y / 720f, width / 1280f, height / 720f),
            RenderRectangle.Full, RenderColor.White, RenderColor.Transparent);
}
