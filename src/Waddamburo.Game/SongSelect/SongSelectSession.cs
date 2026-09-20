using Waddamburo.Catalog;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.SongSelect;

public enum SongBoardTextureKind
{
    Compact,
    Expanded,
}

public readonly record struct SongBoardTextureStyle
{
    public SongBoardTextureStyle(uint compactOutlineRgb)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compactOutlineRgb, 0x00ff_ffffU);
        CompactOutlineRgb = compactOutlineRgb;
    }

    public uint CompactOutlineRgb { get; }
}

public interface ISongBoardTextureService
{
    LumenNativeSurfaceKey GetSongTitle(
        SongSelectSong song,
        SongBoardTextureStyle style,
        SongBoardTextureKind kind);
}

public sealed record SongPreviewRequest(
    SongKey Song,
    CatalogAssetKey Audio,
    TimeSpan Start);

public interface ISongPreviewController
{
    void SetPreview(SongPreviewRequest? request);
}

public sealed record SongSelection(
    long CatalogRevision,
    SongKey Song,
    ChartKey PlayerOneChart,
    ChartKey? PlayerTwoChart);

/// <summary>Owns browser requests while remaining independent of Lumen and content sources.</summary>
public sealed class SongSelectSession
{
    private readonly ISongBoardTextureService _textures;
    private readonly ISongPreviewController _previews;

    public SongSelectSession(
        SongSelectCatalogView catalog,
        ISongBoardTextureService textures,
        ISongPreviewController previews)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
    }

    public SongSelectCatalogView Catalog { get; }

    public SongSelection? Selection { get; private set; }

    public SongPreviewRequest? Preview(int category, int song)
    {
        SongPreviewRequest? request = null;
        if (Catalog.TryGetSong(category, song, out var selected)
            && selected.Descriptor.AudioAsset is { } audio)
        {
            request = new SongPreviewRequest(
                selected.Descriptor.Key,
                audio,
                selected.Descriptor.PreviewStart ?? TimeSpan.Zero);
        }
        _previews.SetPreview(request);
        return request;
    }

    public void StopPreview() => _previews.SetPreview(null);

    public LumenNativeSurfaceKey GetBoardTexture(
        int category,
        int song,
        SongBoardTextureKind kind)
    {
        if (!Catalog.TryGetSong(category, song, out var selected))
            throw new ArgumentOutOfRangeException(
                nameof(song),
                $"Song Select referenced category {category}, song {song} outside the pinned catalog.");
        return _textures.GetSongTitle(selected, Catalog.Categories[category].BoardStyle, kind);
    }

    public SongSelection Select(int category, int song, int playerOneCourse, int playerTwoCourse)
    {
        if (!Catalog.TryGetSong(category, song, out var selected))
            throw new ArgumentOutOfRangeException(nameof(song));
        var playerOne = chart(selected, playerOneCourse, required: true)!;
        var playerTwo = chart(selected, playerTwoCourse, required: false);
        StopPreview();
        return Selection = new SongSelection(
            Catalog.Revision,
            selected.Descriptor.Key,
            playerOne.Key,
            playerTwo?.Key);
    }

    private static SongChartDescriptor? chart(SongSelectSong song, int courseIndex, bool required)
    {
        if (!required && courseIndex < 0)
            return null;
        if ((uint)courseIndex >= 5)
            throw new ArgumentOutOfRangeException(nameof(courseIndex));
        var course = (TaikoCourse)courseIndex;
        return song.Descriptor.Charts.FirstOrDefault(chart => chart.Course == course)
            ?? throw new InvalidOperationException($"Song '{song.Descriptor.Key}' does not provide {course}.");
    }
}
