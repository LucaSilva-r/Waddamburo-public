using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public sealed partial class SongSelectHostBinding
{
    private static readonly bool FolderTrace = Environment.GetEnvironmentVariable("WADDAMBURO_FOLDER_TRACE") == "1";
    public bool CurvedScrolling { get; }
    private readonly VerticalSongBrowser _browser;
    private CategoryPreviewReveal[] _categoryReveals = [];
    private readonly Dictionary<string, int> _categoryRevealFrames = [];
    public (float Text, float Artwork) BrowserCategoryReveal(string frame, float interpolation) =>
        _categoryRevealFrames.TryGetValue(frame, out var genre)
            ? (_categoryReveals[genre].TextOpacity(interpolation), _categoryReveals[genre].ArtworkScale(interpolation)) : (1, 1);
    private readonly SongFolderTransition _folderTransition = new();
    private readonly Dictionary<(int Genre, int Copy), CategoryTransitionPose> _folderCategoryPoses = [];
    private readonly record struct CategoryTransitionPose(float Top, float Height, float Body, float Preview, float Reveal);
    private (LumenNativeSurfaceKey Surface, float Extent)? _browserReturn, _browserReturnTall;
    private readonly SongPreviewCycle _previewCycle = new();
    private readonly SongPreviewReveal _previewReveal = new();
    private readonly CategoryPreviewReveal _songScrollReveal = new();
    private readonly Dictionary<(int Genre, int Board), (float Top, float Expansion)> _previousBrowserRows = [];
    public (int Genre, int Board) BrowserSongIdentity => (_browser.SelectedGenre, _browser.SelectedUnwrappedBoard);
    public IEnumerable<(int Genre, int Board, float Top, float Expansion)> BrowserOutgoingSongRows(float interpolation)
    {
        if (!_browser.SongScroll || _folderTransition.Busy || _browser.SelectedBoard < 0) yield break;
        foreach (var (offset, expansion) in _browser.ExpandedSongRows)
            if (offset != 0 && expansion > 0)
            {
                var identity = (_browser.SelectedGenre, _browser.SelectedUnwrappedBoard + offset);
                var top = _browser.VisibleRowTop(offset) - _browser.Camera;
                var previous = _previousBrowserRows.GetValueOrDefault(identity, (Top: top, Expansion: expansion));
                var t = Math.Clamp(interpolation, 0, 1);
                yield return (identity.Item1, identity.Item2, previous.Top + (top - previous.Top) * t,
                    previous.Expansion + (expansion - previous.Expansion) * t);
            }
    }
    public float BrowserSongTitleOpacity(float interpolation) => _browserExpandedTitle
        ? _browser.SongScroll ? _songScrollReveal.TextOpacity(interpolation) : _previewReveal.TitleOpacity(interpolation)
        : _folderTransition.Opening ? 0
        : 1 - VerticalSongBrowser.Ease((BrowserCardHeight(interpolation) - 65) / 35);
    public float BrowserSongDifficultyOpacity(float interpolation) => _browser.SongScroll
        ? _songScrollReveal.ArtworkScale(interpolation) : _previewReveal.DifficultyOpacity(interpolation);
    private int _browserTextTick;
    private readonly Dictionary<string, int> _browserTextBirth = [];
    public int BrowserTextTick => _browserTextTick;
    public int BrowserTextAge(LumenNativeSurfaceKey surface, bool subtitle = false)
    {
        var identity = browserTextIdentity(surface, subtitle);
        if (!_browserTextBirth.TryGetValue(identity, out var birth)) _browserTextBirth[identity] = birth = _browserTextTick;
        return _browserTextTick - birth;
    }

    /// <summary>Keep the selected song at its first character throughout the return from difficulty select.</summary>
    public void RestartBrowserTitleScroll()
    {
        if (_scrollingTitle is not { } title) return;
        _browserTextBirth.Remove(browserTextIdentity(title.Surface, false));
        _browserTextBirth.Remove(browserTextIdentity(title.Surface, true));
        _titleAge = 0;
    }

    private static string browserTextIdentity(LumenNativeSurfaceKey surface, bool subtitle)
    {
        var last = surface.Value.LastIndexOf(':');
        var kind = last > 0 ? surface.Value.LastIndexOf(':', last - 1) : -1;
        return (kind > 0 ? surface.Value[..kind] : surface.Value) + (subtitle ? ":subtitle" : ":title");
    }
    public LumenTextLayout? BrowserTextLayout(LumenNativeSurfaceKey surface) => _session.GetHorizontalLayout(surface);
    public (float Width, float Height, FolderArtStyle Style)? BrowserPatternSize(LumenNativeSurfaceKey surface) => _session.GetPatternSize(surface);
    private readonly Dictionary<int, (LumenNativeSurfaceKey Surface, float Extent, float TextWidth)> _categoryTitles = [];
    private bool[] _browserReady = [];
    private const int SongRowCopies = 23;
    private readonly Dictionary<int, (int Genre, int Board)> _songRowIdentities = [];
    private readonly HashSet<string> _browserCopies = [];
    private readonly List<string> _browserRowFrames = [];
    private readonly Dictionary<string, float> _browserRowScales = [];
    public IReadOnlyList<string> BrowserRowFrames => _browserRowFrames;
    public bool BrowserUsesOsuSongPattern => (uint)_browser.SelectedGenre < (uint)_session.Catalog.Categories.Length
        && _session.Catalog.Categories[_browser.SelectedGenre].Key.Source == Waddamburo.Catalog.SongSourceKind.OsuLazer;
    public float BrowserRowScale(string path) => _browserRowScales.GetValueOrDefault(path, 1.15f);
    private readonly List<string> _browserCategoryFrames = [];
    public IReadOnlyList<string> BrowserCategoryFrames => _browserCategoryFrames;
    public bool BrowserCategorySelected(string frame) => _categoryRevealFrames.TryGetValue(frame, out var genre)
        && genre == _browser.SelectedGenre;
    private readonly Dictionary<string, LumenNativeSurfaceKey> _browserDescriptions = [];
    private readonly Dictionary<int, LumenNativeSurfaceKey?> _categoryDescriptions = [];
    public bool BrowserCategoryIsFavourites(string frame) => _categoryRevealFrames.TryGetValue(frame, out var genre)
        && _session.Catalog.Categories[genre].AuthoredLabel == SongSelectCatalogView.FavouritesLabel;
    public LumenNativeSurfaceKey? BrowserDescription(string frame) => _browserDescriptions.TryGetValue(frame, out var surface) ? surface : null;
    private (int Genre, int Board) _browserTitleAt = (-1, -1);
    private bool _browserExpandedTitle;
    private (int Genre, int Board) _returnRowIdentity = (-1, -1);
    public LumenNativeSurfacePlacement? BrowserSongPanel { get; private set; }
    private float _previousBrowserSongHeight;
    public float BrowserCardHeight(float interpolation) => _previousBrowserSongHeight
        + (_browser.SelectionHeight - _previousBrowserSongHeight) * Math.Clamp(interpolation, 0, 1);
    public float BrowserSelectedRowTop(float interpolation)
    {
        var top = _browser.SelectionTop - _browser.Camera;
        return _previousBrowserRows.TryGetValue(BrowserSongIdentity, out var previous)
            ? previous.Top + (top - previous.Top) * Math.Clamp(interpolation, 0, 1) : top;
    }
    public LumenNativeSurfacePlacement? InterpolatedBrowserSongPanel(float interpolation) => BrowserSongPanel is { } panel
        ? panel with { Height = BrowserCardHeight(interpolation), Y = BrowserSelectedRowTop(interpolation) } : null;
    private const string BrowserRoot = "main/genreSelect_/";

    public bool BrowserPreviewShowsUra => _previewCycle.ShowsUra;
    public float BrowserPreviewOpacity => _previewCycle.Opacity;
    public IReadOnlyList<int?> BrowserPreviewLevels { get; private set; } = [];

    private bool _closeBrowserRequested, _restoringBrowserCategory;
    public bool BrowserFolderBusy => _folderTransition.Busy || _closeBrowserRequested || _restoringBrowserCategory;
    public bool BrowserOnReturn { get; private set; }
    private int _browserDirection = 1;
    public void SetBrowserScrollDirection(int direction) => _browserDirection = direction >= 0 ? 1 : -1;
    public float BrowserSongContentOpacity(float interpolation) => _folderTransition.Folder >= 0
        ? _folderTransition.SongContentOpacity(interpolation) : 1;
    public float BrowserSongMorphWidth(float interpolation) => _folderTransition.Folder >= 0
        ? _folderTransition.SongWidth(interpolation) : 1;
    private (int Genre, int Board)? _closingBrowserSong;
    public (string Path, float Width)? BrowserFolderMorph(float interpolation) => _folderTransition.Folder >= 0
        ? (BrowserRoot + $"genreFolder_/wd_open_{_folderTransition.Folder}_1", _folderTransition.CategoryWidth(interpolation)) : null;

    public bool CloseBrowserFolder()
    {
        if (!VerticalLayout || CourseSelectSong is not null || _restoringBrowserCategory
            || _openFolder < 0 && _folderTransition.Folder < 0) return false;
        var player = requirePlayer();
        var wait = player.ReadScriptValue("main.musicScene.musicSelect.waitTween");
        var settled = wait is not null && Equals(wait, player.ReadScriptValue("_global.MusicSelect.WAIT_NONE"));
        // Return first opens to the same full-size card as a song. A quick
        // confirm waits for that pose before running the reverse folder tween.
        if (!settled || _openFolder < 0 || _browser.SelectionHeight < 245.9f)
        {
            _closeBrowserRequested = true;
            return true;
        }
        if (!player.TryCallScriptMethod("main.musicScene.musicSelect", "Tween_GenreCancel", [])) return false;
        _closingBrowserSong = (_browser.SelectedGenre, _browser.SelectedBoard);
        _folderTransition.Close();
        _restoringBrowserCategory = true;
        return true;
    }

    private static readonly string[] NativeSlideLabels = ["\u30ab\u30fc\u30bd\u30eb\u53f3", "\u30ab\u30fc\u30bd\u30eb\u5de6"];

    private NativeSongMotion readNativeSongMotion()
    {
        var player = requirePlayer();
        var slide = 1f;
        if (player.InstanceFrame(BrowserRoot.TrimEnd('/')) is { Playing: true } list)
            foreach (var label in NativeSlideLabels)
                if (player.TryGetLabelFrame(BrowserRoot.TrimEnd('/'), label, out var start)
                    && list.Frame >= start && list.Frame < start + 24)
                {
                    _browserDirection = label == NativeSlideLabels[0] ? 1 : -1;
                    // Native dmyC_ positions over the eight authored slide frames.
                    // Sampling the real dummy also follows wheel fast-forwarding.
                    if (list.Frame <= start + 8 && player.TryGetInstanceLocalTransform(BrowserRoot + "dmyC_", out var centre))
                    {
                        // Undo the export's 20-unit side spacing, retaining
                        // the original authored eight-frame curve exactly.
                        var x = centre.X + (centre.X < 150 ? 20 : centre.X > 150 ? -20 : 0);
                        var origin = label == NativeSlideLabels[0] ? 400f : -100f;
                        slide = Math.Clamp((x - origin) / (150 - origin), 0, 1);
                    }
                    break;
                }
        var expansion = 0f;
        if (player.InstanceAlpha(BrowserRoot + "expand_") is > 0
            && player.TryGetInstanceLocalTransform(BrowserRoot + "expand_/left_", out var left)
            && player.TryGetInstanceLocalTransform(BrowserRoot + "expand_/right_", out var right))
            expansion = Math.Clamp((right.X - left.X - 61) / (360 - 61), 0, 1);
        // The reverse folder morph starts from the complete Return card even
        // after the native controller has hidden its old selection overlay.
        return _closingBrowserSong is not null ? new(1, 1) : new(slide, expansion);
    }

    private float categoryAlpha(int genre) => genre == _folderTransition.Folder ? 1 : _folderTransition.OtherAlpha;
    private float categoryOffset(int genre) => _folderTransition.Folder < 0 || genre == _folderTransition.Folder ? 0
        : (_browser.Top(genre) < _browser.Top(_folderTransition.Folder) ? -1 : 1) * _folderTransition.OtherOffset;

    private void updateVerticalBrowser()
    {
        _browserTextTick++;
        var workTrace = new BrowserStartupTrace(_browserTextTick);
        var player = requirePlayer();
        var count = _session.Catalog.Categories.Length;
        if (count == 0) return;
        var genre = (int)(player.CallScriptNumber("_global.container", "GetCurrentMusicInfo", "genreIndex") ?? 0);
        var board = (int)(player.CallScriptNumber("_global.container", "GetCurrentMusicInfo", "musicIndex") ?? -1);
        genre = Math.Clamp(genre, 0, count - 1);
        var opened = new bool[count];
        if (_browserReady.Length != count) _browserReady = new bool[count];
        var counts = new int[count];
        for (var i = 0; i < count; i++)
        {
            opened[i] = player.ReadScriptValue($"_global.container.isOpenFolder.{i}") is true;
            if (!opened[i]) _browserReady[i] = false;
            counts[i] = Math.Max(1, (int)(player.CallScriptNumber("_global.CppConnection.resource", "GetMusicBoardNum", null, i) ?? 1));
        }
        var openedFolder = opened[genre] ? genre : -1;
        for (var slot = 0; slot < Boards.Length; slot++)
        {
            var info = $"_global.CppConnection.resource.musicboardList.{slot}.musicInfo";
            var g = (int)(player.ReadScriptValue(info + ".genreIndex") as double? ?? -1);
            if ((uint)g < (uint)count && opened[g] && player.ReadScriptValue(info + ".musicIndex") is double b && b >= 0)
                _browserReady[g] = true;
        }
        for (var i = 0; i < count; i++) opened[i] &= _browserReady[i];
        if (CourseSelectSong is null)
        {
            var previousFolder = _folderTransition.Folder;
            _folderTransition.Advance(openedFolder, opened[genre]);
            if (_folderTransition.Folder >= 0 && _folderTransition.Folder != previousFolder)
                _browserTextBirth.Clear(); // Every folder visit starts its title pause at the beginning.
        }
        if (_folderTransition.Folder < 0) _closingBrowserSong = null;
        if (_closingBrowserSong is { } closing && _folderTransition.RowsAlpha > 0)
        {
            genre = closing.Genre; board = closing.Board; opened[genre] = true;
        }
        // Keep the category tile's height until it has flattened and opened again.
        // The list grows only while its rows are entering (and shrinks as they leave).
        if (_folderTransition.Folder == genre && _folderTransition.RowsAlpha == 0)
            opened[genre] = false;
        if (!opened[genre]) board = -1;
        var isReturn = board >= 0 && player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, genre, board) is < 0;
        // The movie adds a trailing Return as well as the leading one. They are
        // the same seam in a circular list; retain just one visual row.
        if (board >= 0 && counts[genre] > 1
            && player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, genre, counts[genre] - 1) is < 0)
        {
            counts[genre]--;
            if (board == counts[genre]) board = 0;
        }
        BrowserOnReturn = isReturn;
        _openFolder = opened[genre] ? genre : -1;
        var selection = BrowserRoot + "expand_";
        player.TryGetInstanceLocalTransform(selection + "/left_", out var left);
        var previousSelection = (_browser.SelectedGenre, _browser.SelectedBoard);
        var previousHeight = _browser.SelectionHeight;
        _previousBrowserRows.Clear();
        if (_browser.SelectedGenre >= 0 && _browser.SelectedBoard >= 0)
            foreach (var offset in _browser.VisibleSongOffsets().Concat(_browser.ExpandedSongRows.Select(row => row.Offset)).Distinct())
                _previousBrowserRows[(_browser.SelectedGenre, _browser.SelectedUnwrappedBoard + offset)] =
                    (_browser.VisibleRowTop(offset) - _browser.Camera, _browser.VisibleRowExpansion(offset));
        if (CourseSelectSong is null)
        {
            var motion = _folderTransition.PreviewMotion(readNativeSongMotion());
            _browser.Advance(opened, counts, genre, board, 246, direction: _browserDirection, motion: motion);
        }
        if (_closeBrowserRequested && _browser.SelectionHeight >= 245.9f)
        {
            _closeBrowserRequested = false;
            CloseBrowserFolder();
        }
        _songScrollReveal.Advance(_browser.SongScroll ? (_browser.SelectionHeight - 65) / 181 : 0);
        BrowserSongPanel = board >= 0 && !isReturn
            ? new(360, _browser.SelectionTop - _browser.Camera, 506, _browser.SelectionHeight) : null;
        _previousBrowserSongHeight = _previousBrowserRows.TryGetValue(BrowserSongIdentity, out var previousRow)
            ? 65 + previousRow.Expansion : previousSelection == (genre, board) ? previousHeight : _browser.SelectionHeight;
        if (player.TryGetInstanceLocalTransform(BrowserRoot.TrimEnd('/'), out var column))
            player.TrySetInstanceLayoutPosition(BrowserRoot.TrimEnd('/'), column.X + 33.4f + .6f * _folderTransition.RowsAlpha, column.Y - _browser.Camera);
        player.TrySetInstanceLayoutVisibility("main/cursor_", false);
        foreach (var child in new[] { "wd_context_folder", "wd_category_previous", "wd_category_next", "wd_flat_selection",
                     "genreFolderTab", "genreName_", "tab_", "dmyR1_", "dmyC_", "dmyL1_" })
            player.TrySetInstanceLayoutVisibility(BrowserRoot + child, false);

        workTrace.Lap("model-and-selection");
        var nearest = new Dictionary<(int Genre, int Board), (int Slot, float Distance)>();
        for (var index = 0; index < Boards.Length; index++)
        {
            var info = $"_global.CppConnection.resource.musicboardList.{index}.musicInfo";
            var key = ((int)(player.ReadScriptValue(info + ".genreIndex") as double? ?? -1),
                (int)(player.ReadScriptValue(info + ".musicIndex") as double? ?? -1));
            if (!player.TryGetInstanceLocalTransform(BrowserRoot + Boards[index], out var pose)) continue;
            var distance = Math.Abs(pose.X - 150);
            if (!nearest.TryGetValue(key, out var previous) || distance < previous.Distance)
                nearest[key] = (index, distance);
        }
        for (var index = 0; index < Boards.Length; index++)
        {
            var path = BrowserRoot + Boards[index];
            var info = $"_global.CppConnection.resource.musicboardList.{index}.musicInfo";
            var g = (int)(player.ReadScriptValue(info + ".genreIndex") as double? ?? -1);
            var b = (int)(player.ReadScriptValue(info + ".musicIndex") as double? ?? -1);
            var valid = board < 0 && (uint)g < (uint)count && b >= 0 && (opened[g] || _browser.Reveal(g) > 0)
                && nearest.TryGetValue((g, b), out var closest) && closest.Slot == index;
            player.TrySetInstanceLayoutVisibility(path, valid);
            if (!valid) continue;
            if (b >= 0)
            {
                var available = _browser.BodyHeight(g) - (_browser.BoardTop(g, b) - _browser.Top(g));
                var entering = Math.Clamp((_browser.Reveal(g) - .25f) / .75f, 0, 1);
                player.TrySetInstanceLayoutAlpha(path, Math.Clamp((available - 35) / 24, 0, 1)
                    * entering * categoryAlpha(g) * _folderTransition.RowsAlpha);
            }
            else player.TrySetInstanceLayoutAlpha(path, categoryAlpha(g));
            var top = _browser.BoardTop(g, b) + categoryOffset(g);
            var nativeTop = -35f;
            if (b < 0 && player.TryGetInstanceLocalTransform(path + "/base_/expand_/wd_native_top", out var edge))
                nativeTop = edge.X - 9;
            player.TrySetInstanceLayoutPosition(path, (top - 185) / .65f - 42 - nativeTop, 0);
            var selected = g == genre && b == board;
            var returnRow = player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, g, b) is < 0;
            var returnTitle = path + "/wd_return_title";
            if (returnRow)
            {
                if (player.InstanceFrame(returnTitle) is null)
                    player.TryCloneInstance(BrowserRoot + "wd_flat_selection/wd_selected_title", path, "wd_return_title", 100);
                player.TrySetInstanceLayoutTransform(returnTitle, new(1, 0, 0, 1, 46, 253.0909f));
                var title = browserReturn();
                player.SetInstanceOverlay(returnTitle, title.Surface,
                    new(-28, 200 - title.Extent, 56, title.Extent), replace: true);
            }
            player.TrySetInstanceLayoutVisibility(returnTitle, returnRow && !selected);
            foreach (var child in new[] { "folder_", "backboard_" })
                player.TrySetInstanceLayoutVisibility(path + "/base_/" + child, b >= 0 ? false : null);
            player.TrySetInstanceLayoutVisibility(path + "/base_/expand_", b >= 0 ? !selected : null);
            player.TrySetInstanceLayoutVisibility(path + "/backword_", returnRow || selected && b >= 0 ? false : null);
            // The focus overlay owns the selected title. Its recycled compact copy
            // can still be travelling into place while the previous preview closes.
            player.TrySetInstanceLayoutVisibility(path + $"/song_name{index}", selected && b >= 0 ? false : null);
            player.TrySetInstanceLayoutVisibility(path + "/accessory_/wd_legacy_spine_tab", false);
            var previewing = _browser.Preview(g) > 0 || _browser.Reveal(g) > 0;
            showCategoryFrame(path + "/base_", g, b < 0 && !previewing);
            if (b < 0 && previewing)
                foreach (var child in new[] { "folder_", "expand_", "backboard_" })
                    player.TrySetInstanceLayoutVisibility(path + "/base_/" + child, false);
            if (b < 0)
                foreach (var child in new[] { "accessory_", "folderaccessory_" })
                    player.TrySetInstanceLayoutVisibility(path + "/" + child, !previewing);
        }

        var selectedTop = _browser.SelectionTop;
        if (board >= 0)
        {
            player.TrySetInstanceLayoutPosition(selection, (selectedTop - 185) / .65f - left.X + 9, -4,
                interpolate: previousSelection == (genre, board));
            player.TrySetInstanceLayoutPosition(BrowserRoot + "musicDetail_", (selectedTop + 70 - 185) / .65f + 44.615f, 100);
        }
        if (board >= 0 && !isReturn && CourseSelectSong is null)
        {
            var song = (int)(player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, genre, board) ?? -1);
            if (song >= 0 && _session.Catalog.TryGetSong(genre, song, out var previewSong))
            {
                _previewCycle.Advance(previewSong.Descriptor.Key.ToString(), previewSong.Level(Waddamburo.Catalog.TaikoCourse.Oni),
                    previewSong.Level(Waddamburo.Catalog.TaikoCourse.Ura), _browser.SelectionHeight >= 245.9f);
                BrowserPreviewLevels = [previewSong.Level(Waddamburo.Catalog.TaikoCourse.Easy),
                    previewSong.Level(Waddamburo.Catalog.TaikoCourse.Normal), previewSong.Level(Waddamburo.Catalog.TaikoCourse.Hard), _previewCycle.Level];
                preparePreviewArt();
            }
            if (song >= 0)
            {
                var title = _session.GetPreviewTitle(genre, song, _browser.SelectionHeight > (_browser.SongScroll ? 65 + 181 * .8f : 100));
                if (_browserTitleAt != (genre, board) || _scrollingTitle?.Surface != title.Surface)
                {
                    _scrollingTitle = (title.Surface, _session.GetHorizontalExtent(title.Surface));
                    _browserTitleAt = (genre, board); _titleAge = 0;
                }
                // A new canvas and its origin must become visible in the same frame.
                // Interpolating the old origin with the new canvas makes the title jump.
                player.TrySetInstanceLayoutPosition(selection + "/song_name_detail",
                    left.X + (title.Expanded ? 67.0385f : 33.1923f), 244,
                    interpolate: _browserExpandedTitle == title.Expanded);
                _browserExpandedTitle = title.Expanded;
            }
            disableBrowserTitleMask(selection + "/song_name_detail");
        }
        if (_closingBrowserSong is not null) _previewReveal.Hold();
        else if (CourseSelectSong is null)
            _previewReveal.Advance(genre, board, board >= 0 && !isReturn && _browserExpandedTitle,
                BrowserSongMorphWidth(1) >= .85f);
        player.TrySetInstanceLayoutVisibility(selection, board >= 0 && !isReturn);
        player.TrySetInstanceLayoutAlpha(selection, _folderTransition.RowsAlpha);
        player.TrySetInstanceLayoutVisibility(selection + "/song_name_center", false);
        player.TrySetInstanceLayoutVisibility(selection + "/song_name_detail", true);
        player.TrySetInstanceLayoutAlpha(selection + "/song_name_detail", 1);
        player.TrySetInstanceLayoutVisibility(BrowserRoot + "musicDetail_", board >= 0 && !isReturn && _browser.SelectionHeight > 70 ? null : false);
        player.TrySetInstanceLayoutAlpha(BrowserRoot + "musicDetail_", _folderTransition.RowsAlpha);
        // The native compact highlight is also the width reference for song previews.
        player.TrySetInstanceLayoutVisibility(BrowserRoot + "wd_flat_selection", board >= 0);
        player.TrySetInstanceLayoutVisibility(BrowserRoot + "wd_flat_selection/wd_selected_title", isReturn);
        if (board >= 0)
            player.TrySetInstanceLayoutPosition(BrowserRoot + "wd_flat_selection", (selectedTop - 185) / .65f + 35, -4,
                interpolate: previousSelection == (genre, board));
        if (isReturn)
        {
            var (surface, extent) = browserReturn();
            player.SetNativeFill("wd_flat_selection/wd_selected_title", surface,
                new(-32, 200 - extent + 14.4f / 1.1f, 64, extent));
            player.TrySetInstanceLayoutAlpha(BrowserRoot + "wd_flat_selection", Math.Clamp((_browser.Reveal(genre) - .25f) / .75f, 0, 1)
                * _folderTransition.RowsAlpha);
            player.TrySetInstanceLayoutPosition(BrowserRoot + "wd_flat_selection", (selectedTop - 185) / .65f + 35, -4,
                interpolate: previousSelection == (genre, board));
        }
        player.TrySetInstanceLayoutVisibility(BrowserRoot + "genreDetail_", false);
        workTrace.Lap("recycled-boards-and-title");
        updateSongRows(genre, board, counts[genre]);
        workTrace.Lap("song-rows");
        if (_folderTransition.Folder < 0 && _browser.SelectedBoard < 0 && _browser.Reveal(genre) == 0)
            _restoringBrowserCategory = false;
        if (_categoryReveals.Length != count) _categoryReveals = Enumerable.Range(0, count).Select(_ => new CategoryPreviewReveal()).ToArray();
        for (var index = 0; index < count; index++)
            _categoryReveals[index].Advance(index == _folderTransition.Folder
                ? _folderTransition.CategoryWidth(1) : _browser.Preview(index));
        updateOpenFrames(opened);
        workTrace.Lap("category-frames");
        if (FolderTrace)
            Console.WriteLine(FormattableString.Invariant($"[folder] t={_browserTextTick} p={_folderTransition.Progress:F4} g={genre} b={board} n={counts[genre]} open={opened[genre]} closing={_closingBrowserSong is not null} cam={_browser.Camera:F3} top={_browser.Top(genre):F3} sel={_browser.SelectionTop:F3} h={_browser.SelectionHeight:F3} preview={_browser.Preview(genre):F4} reveal={_browser.Reveal(genre):F4}"));
    }

    private void updateSongRows(int genre, int board, int count)
    {
        _browserRowFrames.Clear();
        _browserRowScales.Clear();
        var player = requirePlayer();
        var shown = new HashSet<int>();
        const string returnBody = BrowserRoot + "wd_return_selection";
        var sameReturn = BrowserOnReturn && _returnRowIdentity == BrowserSongIdentity;
        _returnRowIdentity = BrowserOnReturn ? BrowserSongIdentity : (-1, -1);
        if (BrowserOnReturn && board >= 0)
        {
            if (player.InstanceFrame(returnBody) is null)
                player.TryCloneInstance(BrowserRoot + "musicBoard_center_/base_", BrowserRoot.TrimEnd('/'), "wd_return_selection", 599);
            setBrowserLabel(returnBody, _session.Catalog.Categories[genre].AuthoredLabel);
            var top = _browser.SelectionTop;
            var rowScale = 1.15f + Math.Max(0, _browser.SelectionHeight - 65) / 50.7f;
            player.TrySetInstanceLayoutTransform(returnBody, new(rowScale, 0, 0, -1, (top - 185) / .65f + 35 * rowScale, 494), interpolate: sameReturn);
            player.TrySetInstanceLayoutPosition(returnBody, (top - 185) / .65f + 35 * rowScale, 494, interpolate: sameReturn);
            player.TrySetInstanceLayoutVisibility(returnBody + "/folder_", false);
            player.TrySetInstanceLayoutVisibility(returnBody + "/expand_", false);
            player.TrySetInstanceLayoutVisibility(returnBody + "/backboard_", true);
            player.TrySetInstanceLayoutAlpha(returnBody + "/backboard_", 1);
            player.TrySetInstanceLayoutAlpha(returnBody, _folderTransition.RowsAlpha);
            _browserRowFrames.Add(returnBody + "/backboard_");
            _browserRowScales[returnBody + "/backboard_"] = rowScale;
        }
        player.TrySetInstanceLayoutVisibility(returnBody, BrowserOnReturn && board >= 0);
        const string returnTitle = BrowserRoot + "wd_return_selection_title";
        if (BrowserOnReturn && board >= 0)
        {
            if (player.InstanceFrame(returnTitle) is null)
                player.TryCloneInstance(BrowserRoot + "wd_flat_selection/wd_selected_title", BrowserRoot.TrimEnd('/'), "wd_return_selection_title", 681);
            var top = _browser.SelectionTop;
            player.TrySetInstanceLayoutTransform(returnTitle, new(1, 0, 0, 1,
                (top - 185 + 25.35f + Math.Max(0, _browser.SelectionHeight - 65) / 2) / .65f, 249.0909f), interpolate: sameReturn);
            player.TrySetInstanceLayoutPosition(returnTitle,
                (top - 185 + 25.35f + Math.Max(0, _browser.SelectionHeight - 65) / 2) / .65f, 249.0909f, interpolate: sameReturn);
            var text = browserReturn(tall: true);
            player.SetInstanceOverlay(returnTitle, text.Surface, new(-32, 200 - text.Extent, 64, text.Extent), replace: true);
            player.TrySetInstanceLayoutAlpha(returnTitle, _folderTransition.RowsAlpha);
        }
        player.TrySetInstanceLayoutVisibility(returnTitle, BrowserOnReturn && board >= 0);
        if (CurvedScrolling && BrowserOnReturn && board >= 0)
        {
            player.TrySetHostInstanceDepth(returnBody, 30000);
            player.TrySetHostInstanceDepth(returnTitle, 30001);
        }
        player.TrySetInstanceLayoutVisibility(BrowserRoot + "wd_flat_selection/wd_selected_title", false);

        if (board >= 0 && _folderTransition.RowsAlpha > 0)
        {
            var selected = _browser.SelectedUnwrappedBoard;
            foreach (var offset in _browser.VisibleSongOffsets())
            {
                if (offset == 0 || count <= 1) continue;
                var index = selected + offset;
                var wrapped = (index % count + count) % count;
                // Only the focused occurrence expands. Repeating its extra height
                // in every cycle leaves holes when a category has very few songs.
                var top = _browser.VisibleRowTop(offset)
                    + Math.Sign(offset) * _folderTransition.RowsOffset;
                var y = top - _browser.Camera;
                if (y < -240 - _browser.VisibleRowExpansion(offset) || y > 960) continue;
                var slot = (index % SongRowCopies + SongRowCopies) % SongRowCopies;
                var sameRow = _songRowIdentities.TryGetValue(slot, out var identity) && identity == (genre, index);
                _songRowIdentities[slot] = (genre, index);
                var bodyName = $"wd_song_row_{slot}";
                var body = BrowserRoot + bodyName;
                if (player.InstanceFrame(body) is null)
                    player.TryCloneInstance(BrowserRoot + "musicBoard_center_/base_", BrowserRoot.TrimEnd('/'), bodyName, 600 + slot);
                setBrowserLabel(body, _session.Catalog.Categories[genre].AuthoredLabel);
                var rowScale = 1.15f + _browser.VisibleRowExpansion(offset) / 50.7f;
                player.TrySetInstanceLayoutTransform(body, new(rowScale, 0, 0, -1, (top - 185) / .65f + 35 * rowScale, 494), interpolate: sameRow);
                player.TrySetInstanceLayoutPosition(body, (top - 185) / .65f + 35 * rowScale, 494, interpolate: sameRow);
                player.TrySetInstanceLayoutAlpha(body, _folderTransition.RowsAlpha);
                player.TrySetInstanceLayoutVisibility(body, true);
                player.TrySetInstanceLayoutVisibility(body + "/folder_", false);
                var song = (int)(player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, genre, wrapped) ?? -1);
                player.TrySetInstanceLayoutVisibility(body + "/backboard_", song < 0);
                player.TrySetInstanceLayoutAlpha(body + "/backboard_", 1);
                player.TrySetInstanceLayoutVisibility(body + "/expand_", song >= 0);
                player.TrySetInstanceLayoutAlpha(body + "/expand_", 1);
                setBrowserLabel(body + "/expand_", "wait");
                player.TrySetInstanceNearestSampling(body + "/expand_", true);
                _browserRowFrames.Add(body + (song < 0 ? "/backboard_" : "/expand_"));
                _browserRowScales[_browserRowFrames[^1]] = rowScale;
                var titleName = $"wd_song_row_title_{slot}";
                var title = BrowserRoot + titleName;
                if (player.InstanceFrame(title) is null)
                    player.TryCloneInstance(BrowserRoot + "wd_flat_selection/wd_selected_title", BrowserRoot.TrimEnd('/'), titleName, 650 + slot);
                player.TrySetInstanceLayoutTransform(title, new(1, 0, 0, 1,
                    (top - 185 + 25.675f * 1.15f + (song < 0 ? _browser.VisibleRowExpansion(offset) / 2 : 0)) / .65f, 249.0909f), interpolate: sameRow);
                player.TrySetInstanceLayoutPosition(title,
                    (top - 185 + 25.675f * 1.15f + (song < 0 ? _browser.VisibleRowExpansion(offset) / 2 : 0)) / .65f,
                    249.0909f, interpolate: sameRow);
                disableBrowserTitleMask(title);
                var surface = song < 0 ? browserReturn(tall: true).Surface : _session.GetBoardTexture(genre, song, SongBoardTextureKind.HorizontalCompact);
                if (surface is { } text)
                {
                    var extent = _session.GetHorizontalExtent(text);
                    player.SetInstanceOverlay(title, text, new(-32, 200 - extent, 64, extent), replace: true);
                }
                player.TrySetInstanceLayoutAlpha(title, _folderTransition.RowsAlpha
                    * (1 - Math.Clamp(_browser.VisibleRowExpansion(offset) / 181, 0, 1)));
                player.TrySetInstanceLayoutVisibility(title, true);
                if (CurvedScrolling)
                {
                    // One depth level per distance from focus; paired rows keep
                    // a stable tie-break without colliding with other host copies.
                    var depth = 20000 + (SongRowCopies - Math.Min(SongRowCopies, Math.Abs(offset))) * SongRowCopies * 2 + slot * 2;
                    player.TrySetHostInstanceDepth(body, depth);
                    player.TrySetHostInstanceDepth(title, depth + 1);
                }
                shown.Add(slot);
            }
        }
        for (var slot = 0; slot < SongRowCopies; slot++)
        {
            if (shown.Contains(slot)) continue;
            player.TrySetInstanceLayoutVisibility(BrowserRoot + $"wd_song_row_{slot}", false);
            player.TrySetInstanceLayoutVisibility(BrowserRoot + $"wd_song_row_title_{slot}", false);
        }
    }

    private void preparePreviewArt()
    {
        var player = requirePlayer();
        var courses = new[] { "easy", "normal", "hard", "mania", "extreme" };
        if (courses.Any(course => player.InstanceFrame("main/wd_song_icon_" + course) is null))
        {
            const string source = "main/wd_song_effect_source";
            player.TryCreateExportInstance("CourseEffect", "main", "wd_song_effect_source", 2021);
            player.TrySetInstanceLayoutVisibility(source, false);
            foreach (var course in courses)
            {
                var name = "wd_song_icon_" + course;
                var path = "main/" + name;
                if (player.InstanceFrame(path) is not null) continue;
                if (!player.TryCloneInstance(source + "/bg_" + course, "main", name, 2000 + Array.IndexOf(courses, course))) continue;
                player.TryGotoLabel(path, "on_cursor_end", false);
                player.TrySetInstanceLayoutTransform(path, LumenMatrix.Identity);
                player.TrySetInstanceLayoutAlpha(path, 1);
                player.TrySetInstanceLayoutVisibility(path, false);
            }
            player.TryRemoveHostInstance(source);
        }
        const string button = "main/wd_song_button";
        if (player.InstanceFrame(button) is null)
        {
            const string buttons = "main/wd_song_button_source";
            player.TryCreateExportInstance("SelectStar", "main", "wd_song_button_source", 2022);
            player.TrySetInstanceLayoutVisibility(buttons, false);
            for (var i = 0; i < 4; i++)
            {
                var name = $"wd_song_rating_{i}";
                var path = "main/" + name;
                player.TryCloneInstance(buttons + "/starpanel", "main", name, 2030 + i);
                player.TrySetInstanceLayoutTransform(path, LumenMatrix.Identity);
                player.TrySetInstanceLayoutAlpha(path, 1);
                player.TrySetInstanceLayoutVisibility(path, false);
            }
            player.TryGotoFrame(buttons, 20, false);
            player.TryCloneInstance(buttons + "/board_course", "main", "wd_song_button", 2023);
            player.TrySetInstanceLayoutTransform(button, LumenMatrix.Identity);
            player.TrySetInstanceLayoutAlpha(button, 1);
            player.TrySetInstanceLayoutVisibility(button, false);
            player.TryRemoveHostInstance(buttons);
        }
        for (var i = 0; i < 4; i++)
        {
            var rating = BrowserPreviewLevels[i] ?? 0;
            var path = $"main/wd_song_rating_{i}";
            if (rating is >= 1 and <= 10) setBrowserLabel(path, $"easy_{rating}");
        }
        for (var i = 0; i < 4; i++)
        {
            for (var digit = 0; digit < 2; digit++)
            {
                var name = $"wd_song_level_{i}_{digit}";
                var path = "main/" + name;
                if (player.InstanceFrame(path) is null)
                    player.TryCloneInstance("main/coursestarView_/panel_1p_/starnumx1", "main", name, 2010 + i * 2 + digit);
                var level = BrowserPreviewLevels[i] ?? 0;
                var label = "_" + (digit == 0 ? level % 10 : level / 10);
                if (player.InstanceFrame(path)?.Frame != (digit == 0 ? level % 10 : level / 10))
                    player.TryGotoLabel(path, label, false);
                player.TrySetInstanceLayoutTransform(path, LumenMatrix.Identity);
                player.TrySetInstanceLayoutAlpha(path, 1);
                player.TrySetInstanceLayoutVisibility(path, false);
            }
        }
        const string star = "main/wd_song_star";
        if (player.InstanceFrame(star) is null)
        {
            player.TryCloneInstance("main/coursestarView_/panel_1p_/staricon", "main", "wd_song_star", 2020);
            player.TrySetInstanceLayoutTransform(star, LumenMatrix.Identity);
            player.TrySetInstanceLayoutAlpha(star, 1);
            player.TrySetInstanceLayoutVisibility(star, false);
        }
    }

    private void disableBrowserTitleMask(string title)
    {
        // The presentation clips each independently scrolling line to its card.
        // The original 400-pixel stencil would cut that wider window a second time.
        var mask = title + "/wd_title_mask";
        requirePlayer().TrySetInstanceClipDepth(mask, null);
        requirePlayer().TrySetInstanceLayoutVisibility(mask, false);
    }

    private (LumenNativeSurfaceKey Surface, float Extent) browserReturn(bool tall = false)
    {
        if ((tall ? _browserReturnTall : _browserReturn) is { } cached) return cached;
        var title = _session.GetFolderName("Return", tall ? SongBoardTextureKind.HorizontalCompact : SongBoardTextureKind.HorizontalSelected);
        var value = (title, _session.GetHorizontalExtent(title));
        if (tall) _browserReturnTall = value;
        else _browserReturn = value;
        return value;
    }

    private void updateOpenFrames(bool[] opened)
    {
        var workTrace = new BrowserStartupTrace(_browserTextTick);
        _browserCategoryFrames.Clear();
        _browserDescriptions.Clear();
        _categoryRevealFrames.Clear();
        var player = requirePlayer();
        var parent = BrowserRoot + "genreFolder_";
        const float headerY = -67.65f - 12 / 1.1f; // Twelve screen pixels to the right.
        const float headerLift = 4; // Keep the tab overlapping the frame's top rim.
        player.TrySetInstanceLayoutVisibility(parent, true);
        player.TrySetInstanceLayoutAlpha(parent, 1);
        // Closing one native folder pulses/scales this shared container. Other open
        // folders must keep their own geometry, while ActionScript retains that pulse.
        player.TrySetInstanceLayoutTransform(parent, LumenMatrix.Identity);
        player.TrySetInstanceColor(parent, LumenRenderColor.White, LumenRenderColor.Transparent);
        foreach (var child in new[] { "left_", "right_", "center_", "wd_rows_frame" })
            player.TrySetInstanceLayoutVisibility(parent + "/" + child, false);
        var folderActive = _folderTransition.Folder >= 0 || _restoringBrowserCategory;
        if (!folderActive) _folderCategoryPoses.Clear();
        // These are copies of initialized native artwork, behind the recycled music boards.
        // Recreate after a native parent seek; no controller or input state is duplicated.
        for (var i = 0; i < opened.Length; i++)
        for (var copy = -1; copy <= 1; copy++)
        {
            var slot = (copy + 1) * opened.Length + i;
            var suffix = $"{i}_{copy + 1}";
            var frame = parent + $"/wd_open_{suffix}";
            var header = parent + $"/wd_header_{suffix}";
            var detail = parent + $"/wd_preview_{suffix}";
            var caption = parent + $"/wd_caption_{suffix}";
            var pose = new CategoryTransitionPose(_browser.Top(i) + copy * _browser.Length - _browser.Camera,
                _browser.Height(i), _browser.BodyHeight(i), _browser.Preview(i), _browser.Reveal(i));
            if (folderActive && _folderCategoryPoses.TryGetValue((i, copy), out var captured)) pose = captured;
            else if (!folderActive) _folderCategoryPoses[(i, copy)] = pose;
            // Category geometry belongs to the closed category list. Opening a
            // song changes its camera/period; neither may pull the departing or
            // returning categories through thousands of song-row pixels.
            var departure = folderActive && i != _folderTransition.Folder
                ? (pose.Top < VerticalSongBrowser.CategoryFocusTop ? -1 : 1) * _folderTransition.OtherOffset : 0;
            var top = _browser.Camera + pose.Top + departure;
            var bodyHeight = pose.Body;
            var visible = categoryAlpha(i) > 0
                && pose.Top + departure + pose.Height > -100 && pose.Top + departure < 850;
            var previewAmount = Math.Clamp(pose.Preview + pose.Reveal, 0, 1);
            var preview = pose.Preview > 0;
            if (!visible)
            {
                if (_browserCopies.Remove(suffix))
                    foreach (var path in new[] { frame, header, detail, caption })
                        if (player.InstanceFrame("/" + path) is not null) player.TryRemoveHostInstance(path);
                continue;
            }
            _browserCopies.Add(suffix);
            if (player.InstanceFrame("/" + frame) is null)
                player.TryCloneInstance(parent + "/wd_rows_frame", parent, $"wd_open_{suffix}", 100 + slot);
            if (player.InstanceFrame("/" + header) is null)
                player.TryCloneInstance(BrowserRoot + "genreFolderTab", parent, $"wd_header_{suffix}", 100 + opened.Length * 6 + slot);
            player.TrySetInstanceColor(frame, LumenRenderColor.White, LumenRenderColor.Transparent);
            player.TrySetInstanceColor(header, LumenRenderColor.White, LumenRenderColor.Transparent);
            var panelAlpha = i == _folderTransition.Folder && !_folderTransition.CategoryVisible ? 0 : 1;
            player.TrySetInstanceLayoutVisibility(frame, panelAlpha > 0);
            player.TrySetInstanceLayoutVisibility(header, panelAlpha > 0 && (preview || _browser.Reveal(i) > 0));
            var label = i < _genreLabels.Count ? _genreLabels[i] : _session.Catalog.Categories[i].AuthoredLabel;
            setBrowserLabel(frame, label);
            player.TrySetInstanceLayoutAlpha(frame, categoryAlpha(i) * panelAlpha);
            player.TrySetInstanceLayoutTransform(frame, new(1, 0, 0, 1,
                (top - 185) / .65f - VerticalCategoryFrame.Top, -70));
            if (panelAlpha > 0) resizeBrowserFrame(frame, bodyHeight, i, previewAmount, pose.Reveal, folderActive);
            if (panelAlpha > 0)
            {
                _browserCategoryFrames.Add(frame);
                _categoryRevealFrames[frame] = i;
            }
            workTrace.Lap("clone-resize", i);
            if (preview && categoryDescription(i) is { } description && _session.PrepareSurface(description))
                _browserDescriptions[frame] = description;
            // Preserve the tab's offset relative to the widened preview body.
            var tabY = headerY + 10 * previewAmount;
            // Move the complete tab and its caption together, retaining the overlap.
            player.TrySetInstanceLayoutPosition(header, 29.65f + (top - 165 + 6.5f - headerLift) / .65f, tabY);
            player.TrySetInstanceLayoutAlpha(header, previewAmount
                * categoryAlpha(i) * panelAlpha);
            foreach (var child in new[] { "tab", "name" })
            {
                setBrowserLabel(header + "/" + child, label);
                player.TrySetInstanceLayoutVisibility(header + "/" + child, child != "name");
                player.TrySetInstanceLayoutAlpha(header + "/" + child, 1);
            }
            player.TrySetInstanceNearestSampling(header + "/tab", false);
            if (_session.Catalog.Categories[i].Images.Header is null)
                player.SetInstanceOverlay(header + "/name", _session.GetFolderName(_session.Catalog.Categories[i].Name,
                    SongBoardTextureKind.Expanded), new(0, 0, 256, 56), replace: true);
            // Caption copies stay attached to their category, independently of native board recycling.
            if (player.InstanceFrame("/" + caption) is null)
                player.TryCloneInstance(BrowserRoot + "musicBoard_center_/song_name6", parent,
                    $"wd_caption_{suffix}", 100 + opened.Length * 9 + slot);
            player.TrySetInstanceLayoutVisibility(caption, panelAlpha > 0 && (folderActive || !opened[i] && _browser.Reveal(i) == 0));
            player.TrySetInstanceLayoutAlpha(caption, categoryAlpha(i) * panelAlpha);
            if (!_categoryTitles.TryGetValue(i, out var categoryTitle))
            {
                var surface = _session.GetFolderName(_session.Catalog.Categories[i].Name, SongBoardTextureKind.HorizontalCompact);
                _categoryTitles[i] = categoryTitle = (surface, _session.GetHorizontalExtent(surface), _session.GetHorizontalTextWidth(surface));
            }
            var progress = pose.Preview;
            var fittedScale = Math.Min(1.15f, 204.8f / Math.Max(1, categoryTitle.TextWidth * 1.1f));
            var captionScale = 1 + (fittedScale - 1) * progress;
            player.TryGetInstanceLocalTransform(header + "/name", out var namePose);
            var tabCenter = tabY + namePose.Y + namePose.M12 * 128 + namePose.M22 * 28;
            var captionY = 440 - 200 * captionScale
                + progress * (tabCenter - 440 + captionScale * (6 + categoryTitle.TextWidth / 2));
            player.TrySetInstanceLayoutTransform(caption, new(captionScale, 0, 0, captionScale,
                (top - 185 + 28.85f - (45.85f + headerLift) * progress) / .65f, captionY));
            player.SetInstanceOverlay(caption, categoryTitle.Surface,
                new(-28, 200 - categoryTitle.Extent, 56, categoryTitle.Extent), replace: true);
            workTrace.Lap("caption-and-description", i);
            if (!preview && player.InstanceFrame("/" + detail) is not null) player.TryRemoveHostInstance(detail);
            if (preview && player.InstanceFrame("/" + detail) is null)
            {
                player.TryCloneInstance(BrowserRoot + "genreDetail_", parent, $"wd_preview_{suffix}", 100 + opened.Length * 3 + slot);
                // A visual copy must not run the source controller's fade handler,
                // whose script references still point at the original shared artwork.
                player.TryWriteScriptValue(detail.Replace('/', '.') + ".onEnterFrame", false);
            }
            if (preview)
            {
                // The frame and its content leave the presentation pass together.
                // Hiding only the frame leaves one tick of interpolated native
                // detail alpha, which flashes the full preview after the morph.
                player.TrySetInstanceLayoutVisibility(detail, panelAlpha > 0);
                setBrowserLabel(detail + "/type_", label);
                var scale = Math.Clamp((bodyHeight - 20) / 226, .1f, 1);
                player.TrySetInstanceTransform(detail, new(scale, 0, 0, scale,
                    (top + bodyHeight / 2 - 185) / .65f - 103 * scale, 240 - 190 * scale));
                player.TrySetInstanceLayoutAlpha(detail, pose.Preview
                    * Math.Clamp(1 - pose.Reveal * 4, 0, 1) * categoryAlpha(i) * panelAlpha);
                player.TrySetInstanceLayoutVisibility(detail + "/jukuLevel_", false);
                var songs = _session.Catalog.Categories[i].Songs.Length;
                var counter = detail + "/musicNumber_";
                player.TryWriteScriptValue(counter.Replace('/', '.') + ".onEnterFrame", false);
                // The number's native layout moves the surrounding label and
                // bubble for one, two or three digits; freezing a copied num1
                // layout lets the label cover the tens digit.
                setBrowserLabel(counter, songs >= 100 ? "num3" : songs >= 10 ? "num2" : "num1");
                player.TrySetInstanceLayoutVisibility(detail + "/musicNumber_", songs > 0);
                player.TrySetInstanceLayoutAlpha(detail + "/musicNumber_", 1);
                player.TrySetInstanceAlpha(counter, 1);
                player.TrySetInstanceVisibility(counter, songs > 0);
                foreach (var digit in Enumerable.Range(0, 3))
                {
                    var path = detail + $"/musicNumber_/digit{digit}";
                    player.TrySetInstanceLayoutVisibility(path, digit == 0 || songs >= Math.Pow(10, digit));
                    player.TrySetInstanceLayoutAlpha(path, 1);
                    player.TrySetInstanceAlpha(path, 1);
                    player.TrySetInstanceVisibility(path, digit == 0 || songs >= Math.Pow(10, digit));
                    setBrowserLabel(path, $"_{songs / (int)Math.Pow(10, digit) % 10}");
                }
            }
            workTrace.Lap("detail-and-counter", i);
            // Each category is one visual stack. Distance is a circular row
            // count: selected z=0, immediate neighbours z=-1, then -2, etc.
            var depth = 10000 + ((opened.Length * 2 + _browser.CategoryZ(i, copy)) * opened.Length * 3 + slot) * 4;
            player.TrySetHostInstanceDepth(frame, depth);
            player.TrySetHostInstanceDepth(header, depth + 1);
            if (preview) player.TrySetHostInstanceDepth(detail, depth + 2);
            player.TrySetHostInstanceDepth(caption, depth + 3);
            workTrace.Lap("depth", i);
        }
    }

    private LumenNativeSurfaceKey? categoryDescription(int genre)
    {
        if (_categoryDescriptions.TryGetValue(genre, out var surface)) return surface;
        var category = _session.Catalog.Categories[genre];
        var text = category.Description ?? category.Art?.Lines ?? category.Name switch
        {
            "How to Play" => "Learn how to play the game!!",
            "Favourites" => "Play your favorite songs!!",
            "Pop" or "J-POP" => "Choose between the latest hits and the most popular songs!!",
            "Anime" => "Play your favorite Anime songs!!",
            "Children and Folk" or "Kids" => "Choose between exciting, upbeat and fun songs!!",
            "Variety" => "Choose between various songs that everyone knows!!",
            "Classical" => "Play famous Classical songs from the past!!",
            "Game Music" => "Play the songs from your favorite games!!",
            "Namco Original" => "Play the original songs from the Taiko series!!",
            "Vocaloid" => "Play your favorite Vocaloid songs!!",
            _ => null,
        };
        return _categoryDescriptions[genre] = string.IsNullOrWhiteSpace(text) ? null : _session.GetFolderDescription(text, 0, horizontal: true);
    }

    private void setBrowserLabel(string path, string label)
    {
        var player = requirePlayer();
        if (player.TryGetLabelFrame(path, Patching.SongSelectHeaderColours.Label(label), out var frame)
            || player.TryGetLabelFrame(path, label, out frame))
        {
            var current = player.InstanceFrame(path);
            if (current is not { Playing: false } || current.Value.Frame != frame)
                player.TryGotoFrame(path, frame, play: false);
        }
    }

    private void resizeBrowserFrame(string frame, float height, int category, float preview, float opening, bool folderActive)
    {
        var player = requirePlayer();
        var root = frame + "/";
        var peg = Math.Clamp((75 - height) / (75 - VerticalSongBrowser.CompactRowHeight), 0, 1);
        foreach (var child in new[] { "wd_peg_outline", "wd_peg_fill" })
        {
            player.TrySetInstanceLayoutVisibility(root + child, peg > 0);
            player.TrySetInstanceLayoutAlpha(root + child, peg);
        }
        // The same 526px full-size aperture as the gold song/Return template.
        // Keep corners at native size; extend only the middle spans by22px.
        var sideStart = 88.18f + (16 - 88.18f) * opening - 10 * preview;
        var sideEnd = 548.18f + (560 - 548.18f) * opening + 10 * preview;
        for (var part = 0; part < 9; part++)
        {
            var slice = VerticalCategoryFrame.Slice(part / 3, part % 3, height, VerticalCategoryFrame.Top, sideStart, sideEnd);
            player.TrySetInstanceTransform(root + $"part_{part}", new(slice.Width, 0, 0, slice.Height, slice.X, slice.Y));
            player.TrySetInstanceNearestSampling(root + $"part_{part}", true);
        }
        player.TrySetInstanceTransform(root + "wd_peg_outline", new(0, -35.2f / 1.1f, 16 / .65f, 0,
            VerticalCategoryFrame.Top - 2 / .65f, sideEnd - 18.6f / 1.1f));
        player.TrySetInstanceTransform(root + "wd_peg_fill", new(4 / .65f, 0, 0, 27.2f / 1.1f,
            VerticalCategoryFrame.Top + 2 / .65f, sideEnd - 18.6f / 1.1f));
        var length = height / .65f;
        // The compositor supplies a single four-pixel rim; do not draw and
        // separately snapshot four legacy outline strips for every visible card.
        foreach (var edge in new[] { "top", "bottom", "start", "end" })
            player.TrySetInstanceLayoutVisibility(root + "wd_border_" + edge, false);
        player.TrySetInstanceTransform(root + "wd_pattern_mask",
            new((length - 8 / .65f) / 640, 0, 0, (sideEnd - sideStart - 8 / 1.1f) / 720,
                VerticalCategoryFrame.Top + length / 2, (sideStart + sideEnd) / 2));
        var art = _session.Catalog.Categories[category].Art;
        var first = folderActive ? 0 : Math.Max(0, (int)MathF.Floor((_browser.Camera - _browser.Top(category)) / 600) - 1);
        for (var tile = 0; tile < 3; tile++)
        {
            var path = root + $"wd_rows_pattern_{tile}";
            player.TrySetInstanceLayoutVisibility(path, art is not null);
            if (art is null) continue;
            player.SetNativeFill($"{frame[(frame.LastIndexOf('/') + 1)..]}/wd_rows_pattern_{tile}", _session.GetFolderArt(art, FolderArtPart.Pattern));
            player.TrySetInstanceTransform(path, new(0, -598f / 1.1f, 600f / .65f, 0,
                VerticalCategoryFrame.Top + ((first + tile) * 600 + 300) / .65f, 288));
        }
    }
    private struct BrowserStartupTrace
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("WADDAMBURO_STARTUP_TRACE") == "1";
        private readonly int _tick;
        private long _at;
        public BrowserStartupTrace(int tick)
        {
            _tick = tick;
            _at = Enabled && tick <= 2 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        }
        public void Lap(string phase, int genre = -1)
        {
            if (_at == 0) return;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_at).TotalMilliseconds;
            Console.Error.WriteLine(FormattableString.Invariant($"[browser-startup] tick={_tick} genre={genre} {phase}: {elapsed:F2} ms"));
            _at = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

}
