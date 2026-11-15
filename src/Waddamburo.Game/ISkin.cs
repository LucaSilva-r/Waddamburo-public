using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Game;

/// <summary>
/// A skin: the movies, art and character the engine shows (see free-movies.md, "Zero Bandai dependency"). The screens'
/// hosts (<see cref="Flow.IScreenHosts"/>) are still made by the composition root; they move here with step 6.
/// </summary>
public interface ISkin
{
    /// <summary>The skin's side of the gameplay lanes.</summary>
    IGameplaySkin Gameplay { get; }

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
