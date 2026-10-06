using Waddamburo.Formats.Ddp;
using Waddamburo.Game;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Green's play option icons (40x40), as the lane shows them: lane_obi's textures 27-34, in the order of
/// its icon_option frames (abekobe, doron, speedx2, speedx3, speedx4, random1, random2), then 真.
/// Loaded from the player's archive the first time one is drawn.
/// </summary>
internal sealed class OptionIcons(SdlApplication application, string assetRoot) : IDisposable
{
    private const int First = 27, Count = 8;
    private const int Abekobe = 0, Doron = 1, Speed = 2, Random = 5, Shinuchi = 7;
    private RenderTextureId[]? _icons;
    private bool _loaded;

    /// <summary>A play's icons in the lane's order: speed (its range's lion), ドロン, あべこべ, random, 真.</summary>
    public IEnumerable<RenderTextureId> Of(TaikoPlayOptions options)
    {
        if (load() is not { } icons)
            yield break;
        if (options.SpeedIcon > 1)
            yield return icons[Speed + options.SpeedIcon - 2];
        if (options.Doron)
            yield return icons[Doron];
        if (options.Abekobe)
            yield return icons[Abekobe];
        if (options.Random != TaikoRandom.None)
            yield return icons[Random + (int)options.Random - 1];
        if (options.Shinuchi)
            yield return icons[Shinuchi];
    }

    private RenderTextureId[]? load()
    {
        if (_loaded)
            return _icons;
        _loaded = true;
        try
        {
            var archive = DdpArchive.Open(File.ReadAllBytes(Path.Combine(assetRoot, "enso_system", "common", "packeddata.ddp")));
            var movie = LumenMovieContent.Load(archive.OpenMovie("lane_obi/lane_obi.lm"));
            if (movie.Textures.Length < First + Count)
                throw new InvalidDataException("lane_obi has fewer textures than expected.");
            _icons = [.. movie.Textures.Skip(First).Take(Count).Select(texture =>
                application.UploadRgba8((uint)texture.Width, (uint)texture.Height, [.. texture.Rgba8]))];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"Play option icons unavailable: {exception.Message}");
        }
        return _icons;
    }

    public void Dispose()
    {
        foreach (var icon in _icons ?? [])
            application.ReleaseTexture(icon);
    }
}
