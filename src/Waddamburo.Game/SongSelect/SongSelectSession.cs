using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.SongSelect;

public enum SongBoardTextureKind
{
    Compact,
    Expanded,
    HorizontalCompact,
    HorizontalExpanded,
    HorizontalSelected,
    HorizontalCourse,
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

    /// <summary>A folder name: Compact = vertical spine text, Expanded = horizontal banner.</summary>
    LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0);

    /// <summary>Logical horizontal extent of a scrolling portrait slot (400 when it fits).</summary>
    float GetHorizontalExtent(LumenNativeSurfaceKey surface) => 400;

    float GetHorizontalTextWidth(LumenNativeSurfaceKey surface) => GetHorizontalExtent(surface);

    /// <summary>Starts preparing a surface and reports whether it can already be drawn.</summary>
    bool PrepareSurface(LumenNativeSurfaceKey surface) => true;

    LumenTextLayout? GetHorizontalLayout(LumenNativeSurfaceKey surface) => null;

    (float Width, float Height, FolderArtStyle Style)? GetPatternSize(LumenNativeSurfaceKey surface) => null;

    /// <summary>A picture file (a folder's spine/header/box image).</summary>
    LumenNativeSurfaceKey GetImage(string path);

    /// <summary>A built-in folder's drawn art part.</summary>
    LumenNativeSurfaceKey GetFolderArt(FolderArt art, FolderArtPart part);

    /// <summary>An open folder's description: the lines ('\n') as vertical columns, right to left.</summary>
    LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb);

    /// <summary>A paragraph stored in the portrait slot for the edited horizontal genre description.</summary>
    LumenNativeSurfaceKey GetHorizontalFolderDescription(string lines, uint outlineRgb) => GetFolderDescription(lines, outlineRgb);
}

public readonly record struct SongPreviewTitle(LumenNativeSurfaceKey Surface, bool Expanded);

public sealed record SongPreviewRequest(
    SongKey Song,
    CatalogAssetKey Audio,
    TimeSpan Start);

public interface ISongPreviewController
{
    /// <summary>A song preview, or null for Song Select's background music.</summary>
    void SetPreview(SongPreviewRequest? request);

    /// <summary>Silence: no preview and no background music (a song was chosen, or Song Select closed).</summary>
    void Silence();
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

    public void StopMusic() => _previews.Silence();

    public float GetHorizontalExtent(LumenNativeSurfaceKey surface) => _textures.GetHorizontalExtent(surface);

    public float GetHorizontalTextWidth(LumenNativeSurfaceKey surface) => _textures.GetHorizontalTextWidth(surface);

    public LumenTextLayout? GetHorizontalLayout(LumenNativeSurfaceKey surface) => _textures.GetHorizontalLayout(surface);

    public (float Width, float Height, FolderArtStyle Style)? GetPatternSize(LumenNativeSurfaceKey surface) => _textures.GetPatternSize(surface);

    /// <summary>Warms the expanded title while compact, retaining the drawable layout until it is ready.</summary>
    public SongPreviewTitle GetPreviewTitle(int category, int song, bool expanded)
    {
        var detailed = GetBoardTexture(category, song, SongBoardTextureKind.HorizontalExpanded);
        var ready = _textures.PrepareSurface(detailed);
        var useExpanded = expanded && ready;
        var surface = useExpanded ? detailed : GetBoardTexture(category, song, SongBoardTextureKind.HorizontalSelected);
        return new(surface, useExpanded);
    }

    public LumenNativeSurfaceKey GetBoardTexture(
        int category,
        int song,
        SongBoardTextureKind kind)
    {
        if (!Catalog.TryGetSong(category, song, out var selected))
            throw new ArgumentOutOfRangeException(
                nameof(song),
                $"Song Select referenced category {category}, song {song} outside the pinned catalog.");
        return _textures.GetSongTitle(selected, Catalog.BoardStyle(category, selected), kind);
    }

    public LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0) =>
        _textures.GetFolderName(name, kind, outlineRgb);

    public LumenNativeSurfaceKey GetImage(string path) => _textures.GetImage(path);

    public LumenNativeSurfaceKey GetFolderArt(FolderArt art, FolderArtPart part) => _textures.GetFolderArt(art, part);

    public LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb, bool horizontal = false) => horizontal
        ? _textures.GetHorizontalFolderDescription(lines, outlineRgb) : _textures.GetFolderDescription(lines, outlineRgb);

    public bool PrepareSurface(LumenNativeSurfaceKey surface) => _textures.PrepareSurface(surface);

    /// <remarks><paramref name="options"/>: each drum's play options (left, right).</remarks>
    public SongSelection Select(int category, int song, int playerOneCourse, int playerTwoCourse,
        IReadOnlyList<Gameplay.TaikoPlayOptions>? options = null)
    {
        if (!Catalog.TryGetSong(category, song, out var selected))
            throw new ArgumentOutOfRangeException(nameof(song));
        // Either side may play alone; -1 = that drum has no player.
        if (playerOneCourse < 0 && playerTwoCourse < 0)
            throw new ArgumentOutOfRangeException(nameof(playerOneCourse), "Song Select selected no player's course.");
        var playerOne = chart(selected, playerOneCourse, required: false);
        var playerTwo = chart(selected, playerTwoCourse, required: false);
        return SelectCharts(category, song, playerOne?.Key, playerTwo?.Key, options);
    }

    /// <summary>Selects individual charts, including multiple charts with the same course.</summary>
    public SongSelection SelectCharts(int category, int song, ChartKey? playerOneChart, ChartKey? playerTwoChart,
        IReadOnlyList<TaikoPlayOptions>? options = null)
    {
        if (!Catalog.TryGetSong(category, song, out var selected))
            throw new ArgumentOutOfRangeException(nameof(song));
        if (playerOneChart is null && playerTwoChart is null)
            throw new ArgumentException("Select at least one player's chart.", nameof(playerOneChart));
        SongChartDescriptor? resolve(ChartKey? key) => key is null ? null
            : selected.Descriptor.Charts.FirstOrDefault(chart => chart.Key == key)
                ?? throw new ArgumentException("The chart does not belong to the selected song.", nameof(playerOneChart));
        var playerOne = resolve(playerOneChart);
        var playerTwo = resolve(playerTwoChart);
        var selection = new SongSelection(
            Catalog.Revision,
            selected.Descriptor.Key,
            playerOne?.Key,
            playerTwo?.Key);
        // Two players play the courses' duet charts when the song has them (left drum's, right drum's).
        var duet = playerOne is not null && playerTwo is not null;
        var players = new[]
        {
            playerOne is null ? null : request(LocalPlayerSlot.PlayerOne, playerOne, duet) with { Options = options?.ElementAtOrDefault(0) ?? default },
            playerTwo is null ? null : request(LocalPlayerSlot.PlayerTwo, playerTwo, duet) with { Options = options?.ElementAtOrDefault(1) ?? default },
        }.OfType<PlayerChartRequest>();
        var playRequest = new PlayRequest(
            Catalog.Revision,
            selected.Descriptor.Key,
            selected.Descriptor.AudioAsset,
            players);
        if (!_playRequests.TryRequestPlay(playRequest))
            throw new InvalidOperationException("Another play request is already pending.");

        // The preview fades out and the background stays off until gameplay.
        StopMusic();
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

    private static PlayerChartRequest request(LocalPlayerSlot player, SongChartDescriptor chart, bool duet) => new(
        player,
        chart.Key,
        duet && !chart.DuetChartAssets.IsEmpty
            ? chart.DuetChartAssets[player == LocalPlayerSlot.PlayerOne ? 0 : 1]
            : chart.ChartAsset,
        chart.Course ?? throw new InvalidOperationException($"Chart '{chart.Key}' has no Taiko course."));
}
