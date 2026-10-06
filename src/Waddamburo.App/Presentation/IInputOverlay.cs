using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// An overlay that takes the keyboard (song search, the score list). The shell asks the open one
/// first; with none open, each may open on its own key. While one is open the scene gets no input.
/// </summary>
internal interface IInputOverlay
{
    bool IsOpen { get; }

    /// <summary>One tick of input: true when the overlay took it (it is open, or just opened).</summary>
    bool Tick(SdlKeyboardSnapshot keys, bool escape);

    IEnumerable<RenderQuad> Quads();
}
