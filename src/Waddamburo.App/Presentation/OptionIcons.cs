using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Green's play option icons (40x40), as the lane shows them: lane_obi's textures 27-34, in the order of
/// its icon_option frames (abekobe, doron, speedx2, speedx3, speedx4, random1, random2), then 真.
/// Decoded from the player's archive on a worker from the start; until then, rows show no icons.
/// </summary>
internal sealed class OptionIcons : IDisposable
{
    private const int First = 27, Count = 8;
    private const int Abekobe = 0, Doron = 1, Speed = 2, Random = 5, Shinuchi = 7;
    private readonly SdlApplication _application;
    // Read and decoded on a worker from the start (only the 8 icons, not the whole movie): done on the
    // main thread at the first score list, it stalled that frame about 0.7 s.
    private readonly Task<(int Width, int Height, byte[] Rgba)[]?> _decoding;
    private RenderTextureId[]? _icons;

    public OptionIcons(SdlApplication application, string assetRoot)
    {
        _application = application;
        _decoding = Task.Run(() => decode(assetRoot));
    }

    /// <summary>A play's icons in the lane's order: speed (its range's lion), ドロン, あべこべ, random, 真 (none until loaded).</summary>
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

    // The decoded icons uploaded, once ready (main thread).
    private RenderTextureId[]? load()
    {
        if (_icons is null && _decoding is { IsCompletedSuccessfully: true, Result: { } decoded })
            _icons = [.. decoded.Select(icon => _application.UploadRgba8((uint)icon.Width, (uint)icon.Height, icon.Rgba))];
        return _icons;
    }

    private static (int Width, int Height, byte[] Rgba)[]? decode(string assetRoot)
    {
        try
        {
            var archive = DdpArchive.Open(File.ReadAllBytes(Path.Combine(assetRoot, "enso_system", "common", "packeddata.ddp")));
            var textures = archive.OpenMovie("lane_obi/lane_obi.lm").Textures;
            if (textures.Length < First + Count)
                throw new InvalidDataException("lane_obi has fewer textures than expected.");
            return [.. textures.Skip(First).Take(Count).Select(static view =>
                NutFile.Parse(view.Data).Textures[0] is var texture
                    ? (texture.Width, texture.Height, NutTextureDecoder.DecodeRgba8(texture)) : default)];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException
            or ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Play option icons unavailable: {exception.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons ?? [])
            _application.ReleaseTexture(icon);
    }
}
