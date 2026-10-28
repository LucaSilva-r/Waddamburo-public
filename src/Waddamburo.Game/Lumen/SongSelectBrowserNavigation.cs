using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public sealed partial class SongSelectHostBinding
{
    private int _firstBrowserSong = -1;

    public void BeginBrowserCategorySelection()
    {
        var genre = _browser.SelectedGenre;
        if (_verticalRows && _browser.SelectedBoard < 0 && genre >= 0
            && _session.Catalog.Categories[genre].Songs.Length > 0
            && _session.Catalog.Categories[genre].Link is null)
            _firstBrowserSong = genre;
    }

    private void completeNativeBrowserDecision()
    {
        if (!_verticalRows || CourseSelectSong is not null || _firstBrowserSong < 0) return;
        var player = requirePlayer();
        const string path = "main/genreSelect_";
        if (player.InstanceFrame(path) is not { Playing: true } frame
            || !player.TryGetLabelFrame(path, "\u30b8\u30e3\u30f3\u30eb\u6c7a\u5b9a\uff08\u901a\u5e38\uff09", out var first)
            || !player.TryGetLabelFrame(path, "genre_decide_end", out var last)
            || frame.Frame < first || frame.Frame >= last) return;
        // The host draws the visible folder transition. Finish this hidden native
        // lead-in with all callbacks, rather than waiting before that transition.
        player.TryFastForward(path, last);
    }

    private void bindBrowserNavigation()
    {
        if (!_verticalRows) return;
        var player = requirePlayer();
        const string prototype = "_global.BoardContainer.prototype";
        if (!player.TryWrapScriptMethod(prototype, "SetIndex_OpenFolder", (_, original) =>
        {
            var genre = (int)(player.CallScriptNumber("_global.container", "GetCurrentMusicInfo", "genreIndex") ?? -1);
            if (genre == _firstBrowserSong)
            {
                player.TryWriteScriptValue($"_global.container.openIndex.{genre}", 1d);
                _firstBrowserSong = -1;
            }
            var result = original();
            if (genre >= 0)
            {
                var at = (int)(player.ReadScriptValue($"_global.container.openIndex.{genre}") as double? ?? 1);
                var centre = (int)(player.ReadScriptValue("_global.BoardContainer.INDEX_CENTER") as double? ?? 6);
                for (var index = 0; index < Boards.Length; index++) resetBrowserBoard(index, genre, at + index - centre);
            }
            return result;
        })) throw new InvalidOperationException("The vertical browser could not bind its folder opening.");
        if (!player.TryWrapScriptMethod(prototype, "SetIndex_CloseFolder", (_, original) =>
        {
            var result = original();
            // Opening fills every recycled board with this folder's circular song
            // list. The movie's close only restores the range tracked by boardNum,
            // leaving songs outside that range navigable in the category browser.
            // Rebuild both sides from the restored centre using the native walkers.
            var centre = (int)(player.ReadScriptValue("_global.BoardContainer.INDEX_CENTER") as double? ?? 6);
            for (var index = centre; index > 0; index--)
                player.TryCallScriptMethod("_global.container", "ResetBoard_LeftIndex", [LumenHostValue.FromNumber(index)]);
            for (var index = centre; index < Boards.Length - 1; index++)
                player.TryCallScriptMethod("_global.container", "ResetBoard_RightIndex", [LumenHostValue.FromNumber(index)]);
            return result;
        })) throw new InvalidOperationException("The vertical browser could not bind its folder closing.");
        foreach (var direction in new[] { -1, 1 })
        {
            var step = direction;
            var method = step < 0 ? "ResetBoard_LeftIndex" : "ResetBoard_RightIndex";
            if (!player.TryWrapScriptMethod(prototype, method, (call, original) =>
            {
                var index = integer(call, 0);
                var info = $"_global.container.board.{index}.musicInfo";
                var genre = (int)(player.ReadScriptValue(info + ".genreIndex") as double? ?? -1);
                var board = (int)(player.ReadScriptValue(info + ".musicIndex") as double? ?? -1);
                if (genre < 0 || board < 0 || player.ReadScriptValue($"_global.container.isOpenFolder.{genre}") is not true)
                    return original();
                return resetBrowserBoard(index + step, genre, board + step)
                    ? LumenHostValue.FromNumber(index + step) : original();
            })) throw new InvalidOperationException("The vertical browser could not bind circular navigation.");
        }
    }

    private bool resetBrowserBoard(int index, int genre, int board)
    {
        var player = requirePlayer();
        var count = (int)(player.CallScriptNumber("_global.CppConnection.resource", "GetMusicBoardNum", null, genre) ?? 0);
        if (count > 1 && player.CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null, genre, count - 1) is < 0)
            count--; // The terminal Return is the same seam as the leading Return.
        if (count <= 0) return false;
        var wrapped = (board % count + count) % count;
        return player.TryCallScriptMethod($"_global.container.board.{index}", "ResetMusicInfo",
            [LumenHostValue.FromNumber(genre), LumenHostValue.FromNumber(wrapped)]);
    }
}
