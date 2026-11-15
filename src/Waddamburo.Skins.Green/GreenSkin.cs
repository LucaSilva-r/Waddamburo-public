using Waddamburo.Game;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Skins.Green.Screens;
using Waddamburo.Skins.Green.Art;
using Waddamburo.Skins.Green.Gameplay;
using Waddamburo.Skins.Green.Patching;

namespace Waddamburo.Skins.Green;

/// <summary>Taiko no Tatsujin Green's movies, art and Don-chan, from the user's install.</summary>
public sealed class GreenSkin : ISkin
{
    public IGameplaySkin Gameplay { get; } = new GreenGameplaySkin();

    public void Decoded(string archive, LumenMovieContent content, Action applyUpscales, Action<byte[]> made)
    {
        // Pixel art: whole pixels, never the upscaler's guesses (cached ones included).
        if (PixelArt.IsArchive(archive))
        {
            PixelArt.Enlarge(content, made);
            return;
        }
        if (KadonNotePatch.AppliesTo(content))
        {
            // After the upscales: the purple copies are made from what the GPU would show (upscaled when on).
            applyUpscales();
            foreach (var pixels in KadonNotePatch.Apply(content))
                made(pixels);
            return;
        }
        applyUpscales();
        if (!SongSelectGenrePatch.AppliesTo(content))
            return;
        // The genre patch decodes only the textures it recolours (song_select decoded whole was 343 MiB, 337 ms).
        var own = content.Textures.Length;
        SongSelectGenrePatch.Apply(content);
        for (var index = own; index < content.Textures.Length; index++)
            if (System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(content.Textures[index].Rgba8) is { } pixels)
                made(pixels);
    }

    public bool IsPixelArt(string archive) => PixelArt.IsArchive(archive);

    public IEnumerable<SceneDefinition> OwnScenes =>
    [
        SystemIndicators.Definition(new SceneId("system-indicators")),
        ResumeCountdown.Definition(new SceneId("resume-countdown")),
    ];

    public Func<LumenTextureContent, LumenTextureContent>? Upscaled { set => NutData.Cached = value; }
}
