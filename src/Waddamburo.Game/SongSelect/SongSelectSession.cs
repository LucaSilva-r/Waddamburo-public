using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
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
    ChartKey? PlayerOneChart,
    ChartKey? PlayerTwoChart);

/// <summary>Owns browser requests while remaining independent of Lumen and content sources.</summary>
public sealed class SongSelectSession
{
    private readonly ISongBoardTextureService _textures;
    private readonly ISongPreviewController _previews;
    private readonly IPlayRequestSink _playRequests;

    public SongSelectSession(
        SongSelectCatalogView catalog,
        ISongBoardTextureService textures,
        ISongPreviewController previews,
        IPlayRequestSink playRequests)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _playRequests = playRequests ?? throw new ArgumentNullException(nameof(playRequests));
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
        // Either side may play alone; -1 = that drum has no player.
        if (playerOneCourse < 0 && playerTwoCourse < 0)
            throw new ArgumentOutOfRangeException(nameof(playerOneCourse), "Song Select selected no player's course.");
        var playerOne = chart(selected, playerOneCourse, required: false);
        var playerTwo = chart(selected, playerTwoCourse, required: false);
        var selection = new SongSelection(
            Catalog.Revision,
            selected.Descriptor.Key,
            playerOne?.Key,
            playerTwo?.Key);
        var players = new[]
        {
            playerOne is null ? null : request(LocalPlayerSlot.PlayerOne, playerOne),
            playerTwo is null ? null : request(LocalPlayerSlot.PlayerTwo, playerTwo),
        }.OfType<PlayerChartRequest>();
        var playRequest = new PlayRequest(
            Catalog.Revision,
            selected.Descriptor.Key,
            selected.Descriptor.AudioAsset,
            players);
        if (!_playRequests.TryRequestPlay(playRequest))
            throw new InvalidOperationException("Another play request is already pending.");

        StopPreview();
        return Selection = selection;
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

    private static PlayerChartRequest request(LocalPlayerSlot player, SongChartDescriptor chart) => new(
        player,
        chart.Key,
        chart.ChartAsset,
        chart.Course ?? throw new InvalidOperationException($"Chart '{chart.Key}' has no Taiko course."));
}
