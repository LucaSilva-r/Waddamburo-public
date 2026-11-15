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

    /// <summary>A folder name: Compact = vertical spine text, Expanded = horizontal banner.</summary>
    LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0);

    /// <summary>A picture file (a folder's spine/header/box image).</summary>
    LumenNativeSurfaceKey GetImage(string path);

    /// <summary>A built-in folder's drawn art part.</summary>
    LumenNativeSurfaceKey GetFolderArt(FolderArt art, FolderArtPart part);

    /// <summary>An open folder's description: the lines ('\n') as vertical columns, right to left.</summary>
    LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb);
}

public sealed record SongPreviewRequest(
    SongKey Song,
    CatalogAssetKey Audio,
    TimeSpan Start);

public interface ISongPreviewController
{
    /// <summary>A song preview, or null for Song Select's background music.</summary>
    void SetPreview(SongPreviewRequest? request);

    /// <summary>Silence: no preview and no background music (Song Select closed, or the chosen song's title is up).</summary>
    void Silence();

    /// <summary>
    /// A song was chosen: what plays goes on (under the rainbow, until its title appears and the host silences it)
    /// and nothing else starts.
    /// </summary>
    void Hold();
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

    // --- The screen's state, whatever movie shows it ---

    /// <summary>The countdown (the movie starts it; home or the countdown off: it holds its full time).</summary>
    public SongSelectTimer Timer { get; } = new();

    /// <summary>False: the countdown is off (home, the cabinet setting): it holds its full time.</summary>
    public bool Counting { get; init; } = true;

    /// <summary>The countdown was started (seconds).</summary>
    public event Action<double>? CountdownStarted;

    public event Action? CountdownStopped;

    public void StartCountdown(double seconds)
    {
        Timer.Start(seconds);
        if (!Counting)
            Timer.Stop();
        CountdownStarted?.Invoke(seconds);
    }

    public void StopCountdown()
    {
        Timer.Stop();
        CountdownStopped?.Invoke();
    }

    /// <summary>Seconds left on the countdown; null while none runs (home, or the countdown off).</summary>
    public int? TimeLeft => Counting && Timer.IsRunning ? (int)Math.Ceiling(Timer.Remaining.TotalSeconds) : null;

    /// <summary>The list's cursor settled on a folder (its voice plays).</summary>
    public event Action<int>? FolderCursorMoved;

    public void MoveFolderCursor(int category) => FolderCursorMoved?.Invoke(category);

    /// <summary>
    /// The by-difficulty panel's 前回 board: each playing drum's course (Ura counts as Oni) and its ★. Courses are
    /// catalog courses (<see cref="TaikoCourse"/>), -1 for a drum that does not play.
    /// </summary>
    public void RecordLastCourses(int category, int song, int playerOneCourse, int playerTwoCourse)
    {
        Catalog.TryGetSong(category, song, out var played);
        for (var side = 0; side < 2; side++)
        {
            var course = side == 0 ? playerOneCourse : playerTwoCourse;
            if (course < 0)
                continue;
            TaikoGuest.LastCourse[side] = Math.Min(course, (int)TaikoCourse.Oni);
            if (played is not null && course <= (int)TaikoCourse.Oni && SongSelectCatalogView.CourseStar(played, course) is { } stars)
                TaikoGuest.LastCourseStar[side] = (course, stars);
        }
    }

    /// <summary>The folder open in the list (-1: none).</summary>
    public int OpenFolder { get; private set; } = -1;

    /// <summary>The song whose courses are being chosen (folder and song index); null in the list.</summary>
    public (int Category, int Song)? CourseSelect { get; private set; }

    /// <summary>The song whose courses are being chosen; null in the list.</summary>
    public SongSelectSong? CourseSelectSong { get; private set; }

    /// <summary>Backing out of the courses: they are closing, the list does not take steps yet.</summary>
    public bool LeavingCourseSelect { get; set; }

    /// <summary>The players decided the mode-switch folder (to the other song select).</summary>
    public bool ModeSwitchRequested { get; private set; }

    /// <summary>The players decided the how-to-play folder (the tutorial).</summary>
    public bool TutorialRequested { get; private set; }

    /// <summary>The players decided the dan dojo (its folder, or a series in it).</summary>
    public bool DanRequested { get; private set; }

    /// <summary>The series decided in the dan dojo folder; null: the folder itself (a cabinet's).</summary>
    public string? DanSeriesPicked { get; private set; }

    /// <summary>The players decided a library folder: Song Select reloads with that library.</summary>
    public SongSourceKind? LibraryRequested { get; private set; }

    /// <summary>The players decided a Group or Sort spine: Song Select reloads with the next grouping or order.</summary>
    public SongBrowseControl? BrowseRequested { get; private set; }

    /// <summary>The folder and song of the last course decided (to come back to it).</summary>
    public (CategoryKey Category, SongKey Song)? Picked { get; private set; }

    /// <summary>Forgets the last course decided (a new credit starts on the default song).</summary>
    public void ForgetPicked() => Picked = null;

    /// <summary>Remembers a song as picked, so Song Select comes back to it.</summary>
    public void Remember(int category, int song)
    {
        if (Catalog.TryGetSong(category, song, out var picked))
            Picked = (Catalog.HomeOf(category, picked.Descriptor.Key), picked.Descriptor.Key);
    }

    public void Open(int folder) => OpenFolder = folder;

    /// <summary>Closes the open folder; the folder that was open.</summary>
    public int Close()
    {
        var closed = OpenFolder;
        OpenFolder = -1;
        return closed;
    }

    /// <summary>The courses of a song open (a dan series spine: the dojo is requested, back comes here).</summary>
    public void BeginCourses(int category, int song)
    {
        CourseSelect = (category, song);
        if (Catalog.TryGetSong(category, song, out var picked) && SongSelectCatalogView.DanSeriesOf(picked.Descriptor.Key) is { } series)
        {
            DanSeriesPicked = series;
            DanRequested = true;
            Picked = (Catalog.Categories[category].Key, picked.Descriptor.Key);
        }
        CourseSelectSong = Catalog.TryGetSong(category, song, out var open) ? open : null;
        LeavingCourseSelect = false;
    }

    public void LeaveCourses()
    {
        CourseSelectSong = null;
        LeavingCourseSelect = false;
    }

    /// <summary>A folder that is a choice by itself was decided: the mode switch, the tutorial, the dojo, a library, a browse spine.</summary>
    public void Choose(int category)
    {
        if (category == Catalog.ModeSwitchCategory)
            ModeSwitchRequested = true;
        else if (category == Catalog.TutorialCategory)
            TutorialRequested = true;
        else if (category == Catalog.DanCategory)
            DanRequested = true;
        else if ((uint)category < (uint)Catalog.Categories.Length)
        {
            LibraryRequested = Catalog.Categories[category].Link;
            BrowseRequested = Catalog.Categories[category].Control;
        }
    }

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

    public LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb) => _textures.GetFolderDescription(lines, outlineRgb);

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

        // The preview plays on, unchanged, under the rainbow; the host silences it as the song's title appears (#17).
        _previews.Hold();
        Picked = (Catalog.HomeOf(category, selection.Song), selection.Song);
        return Selection = selection;
    }

    private static SongChartDescriptor? chart(SongSelectSong song, int courseIndex, bool required)
    {
        if (!required && courseIndex < 0)
            return null;
        if ((uint)courseIndex >= 5)
            throw new ArgumentOutOfRangeException(nameof(courseIndex));
        var course = (TaikoCourse)courseIndex;
        // A song without the movie's always-shown courses (an Edit-only TJA still lists easy..hard): its nearest
        // course, the harder one on a tie, rather than a handoff that fails and leaves Song Select waiting.
        return song.Descriptor.Charts.FirstOrDefault(chart => chart.Course == course)
            ?? song.Descriptor.Charts.Where(static chart => chart.Course is not null)
                .OrderBy(chart => Math.Abs((int)chart.Course!.Value - courseIndex)).ThenByDescending(static chart => chart.Course)
                .FirstOrDefault()
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
