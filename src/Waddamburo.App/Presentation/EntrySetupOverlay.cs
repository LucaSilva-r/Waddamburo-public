using Waddamburo.App.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

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
    private const float StandY = 420;
    private static readonly float[] TagX = [222, 1112];
    private const float TagY = 683, TagWidth = 176, TagHeight = 24;
    private const float MessageY = 560;

    private readonly Dictionary<(string Text, int Width, int Height, bool Tag, uint Scale), RenderTextureId> _text = [];

    /// <param name="setup">Each side's setup column while the setup is open (null: not in setup).</param>
    /// <param name="tags">Each side's name for its board (null: no board).</param>
    public IEnumerable<RenderQuad> Quads(IReadOnlyList<SetupColumn>? setup, IReadOnlyList<string?> tags)
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
            if (column.Code is { } code)
            {
                quads.Add(text($"{code[..3]}-{code[3..]}", StandX[side], StandY - 20, 250, 70, tagStyle: false, scale));
                if (column.Seconds is { } seconds)
                    quads.Add(text($"{seconds}", StandX[side], StandY + 45, 100, 34, tagStyle: false, scale));
            }
            else if (!choice.HasDon)
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
            var message = column.Message ?? (choice.IsDefault && !column.Ready ? "Joins by itself (S)" : null);
            if (message is not null)
                quads.Add(text(message, StandX[side], MessageY, 300, 26, tagStyle: false, scale));
        }
        return quads;
    }

    public void Dispose()
    {
        foreach (var texture in _text.Values)
            application.ReleaseTexture(texture);
        _text.Clear();
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

    private static RenderQuad quad(RenderTextureId texture, float x, float y, float width, float height, RenderRectangle uv) =>
        RenderQuad.FromRectangles(texture, new RenderRectangle(x / 1280f, y / 720f, width / 1280f, height / 720f),
            uv, RenderColor.White, RenderColor.Transparent);
}
