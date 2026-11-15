using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game;

/// <summary>
/// A skin: the movies, art and character the engine shows (see free-movies.md, "Zero Bandai dependency"). The screens'
/// hosts (<see cref="Flow.IScreenHosts"/>) are still made by the composition root; they move here with step 6.
/// </summary>
public interface ISkin
{
    /// <summary>The skin's side of the gameplay lanes.</summary>
    IGameplaySkin Gameplay { get; }

    /// <summary>The intermission movies (rainbow, shutter, fades) beyond what they read from the provider.</summary>
    ITransitionSkin Transitions { get; }

    /// <summary>
    /// A movie of the skin decoded: the skin patches it in memory (the files stay untouched). It calls
    /// <paramref name="applyUpscales"/> where the cached upscales go in (or never, for art that must not be upscaled)
    /// and <paramref name="made"/> for each texture it made, so the upscaler leaves those alone.
    /// </summary>
    void Decoded(string archive, LumenMovieContent content, Action applyUpscales, Action<byte[]> made);

    /// <summary>The archive's art is drawn by whole pixels, never upscaled.</summary>
    bool IsPixelArt(string archive);

    /// <summary>Scenes of the skin's own beyond the engine's lists (the texture bake goes through them).</summary>
    IEnumerable<SceneDefinition> OwnScenes { get; }

    /// <summary>The upscaled version of a texture the skin draws itself, when upscales are on (null: off).</summary>
    Func<LumenTextureContent, LumenTextureContent>? Upscaled { set; }
}

/// <summary>What the skin does to an intermission movie as it covers and reveals (ours read transition.* themselves).</summary>
public interface ITransitionSkin
{
    /// <summary>The movie covers the screen; false while it cannot take it yet.</summary>
    /// <param name="side">Whose colour: 0 the left drum, 1 the right, 2 both.</param>
    bool Cover(LumenPlayer movie, bool title, int side);

    void Reveal(LumenPlayer movie, bool title);

    /// <summary>The song's title art on the rainbow.</summary>
    void ShowTitle(LumenPlayer movie, LumenNativeSurfaceKey title);

    /// <summary>The rainbow's title is on screen (the song's preview stops then).</summary>
    bool TitleShown(LumenPlayer movie);
}
