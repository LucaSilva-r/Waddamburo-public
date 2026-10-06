using System.Globalization;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select's scores (home, online), as osu! shows them: R on a song (in the list or on its
/// difficulties) opens its leaderboard from TaikOnline, each player's best play, or the player's own
/// plays; picking one watches its replay (a review: never saved). Left/right pick the difficulty, up/down
/// the play, Space switches leaderboard/own plays, Enter watches, R or Escape closes.
/// </summary>
internal sealed class ScoreListOverlay(GameShell shell, OverlayPainter painter) : IInputOverlay
{
    private const float Left = 170, Top = 70, Width = 940, Height = 580, RowHeight = 40;
    private const int VisibleRows = 10;
    private static readonly string[] CourseNames = ["Easy", "Normal", "Hard", "Oni", "Ura"];

    private SongSelectSong? _song;
    private SongChartDescriptor[] _charts = [];
    private int _chart;
    private bool _mine;
    private int _selection;
    private ScoreProfile? _profile;
    private ScoreClient? _client;
    // Per chart and list (leaderboard / own): the request under way or done.
    private readonly Dictionary<(int Chart, bool Mine), Task<List<ScoreRow>>> _lists = [];
    private Task<ReplayDownload>? _watching;
    private string? _error;

    public bool IsOpen { get; private set; }

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
            return keys.IsDown(SdlKeyboardKey.R) && open();
        if (_watching is { } watching)
        {
            if (watching.IsCompleted)
                watch(watching);
            return true;
        }
        if (escape || keys.IsDown(SdlKeyboardKey.R))
        {
            IsOpen = false;
            return true;
        }
        if (keys.IsDown(SdlKeyboardKey.Left) || keys.IsDown(SdlKeyboardKey.Right))
        {
            _chart = Math.Clamp(_chart + (keys.IsDown(SdlKeyboardKey.Right) ? 1 : -1), 0, _charts.Length - 1);
            _selection = 0;
        }
        if (keys.IsDown(SdlKeyboardKey.Space))
        {
            _mine = !_mine;
            _selection = 0;
        }
        var rows = Rows();
        if (keys.IsDown(SdlKeyboardKey.Up))
            _selection = Math.Max(0, _selection - 1);
        if (keys.IsDown(SdlKeyboardKey.Down))
            _selection = Math.Min(Math.Max(0, rows.Count - 1), _selection + 1);
        if (keys.IsDown(SdlKeyboardKey.Enter) && _selection < rows.Count && _client is { } client)
        {
            var playId = rows[_selection].PlayId;
            _watching = Task.Run(() => client.ReplayAsync(playId));
        }
        return true;
    }

    // R on a song: its playable difficulties, starting on the highest the player has a crown on (else Oni).
    private bool open()
    {
        if (!shell.Arcade.Home || shell.Active.Id != FlowScenes.SongSelect || shell.PlayRequests.Pending is not null
            || shell.Hosts.SongSelect?.SongUnderCursor is not { } song)
            return false;
        _song = song;
        _charts = [.. song.Descriptor.Charts.Where(static chart => chart.Course is not null).OrderBy(static chart => chart.Course)];
        if (_charts.Length == 0)
            return false;
        _profile = TaikoGuest.Profiles[shell.Hosts.PlayerSide];
        _client = _profile?.Token is { } token ? shell.Sync.ClientFor(token) : null;
        var crowns = _profile is { } profile && shell.Sync.Scores is { } scores ? scores.Crowns(profile.Baid) : [];
        var played = Array.FindLastIndex(_charts, chart => crowns.ContainsKey(chart.Key.ToString()));
        _chart = played >= 0 ? played : Math.Max(0, Array.FindIndex(_charts, static chart => chart.Course == TaikoCourse.Oni));
        _lists.Clear();
        (_mine, _selection, _watching, _error) = (false, 0, null, null);
        IsOpen = true;
        return true;
    }

    /// <summary>The rows of the list on show (empty while loading or failed).</summary>
    private List<ScoreRow> Rows() => list() is { IsCompletedSuccessfully: true } task ? task.Result : [];

    private Task<List<ScoreRow>>? list()
    {
        if (_client is not { } client)
            return null;
        var key = (_chart, _mine);
        if (!_lists.TryGetValue(key, out var task))
        {
            var chart = _charts[_chart];
            var mine = _mine;
            _lists[key] = task = Task.Run(async () =>
            {
                // The server knows charts by their notes' hash.
                var sha = await shell.ChartHashes.HashAsync(chart).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The chart has no course.");
                return mine ? await client.MyScoresAsync(sha).ConfigureAwait(false)
                    : (await client.LeaderboardAsync(sha).ConfigureAwait(false)).Scores;
            });
        }
        return task;
    }

    // The replay is here: the song starts as its review (Song Select's rainbow and handoff, as for a pick).
    private void watch(Task<ReplayDownload> download)
    {
        _watching = null;
        if (!download.IsCompletedSuccessfully)
        {
            _error = T("score_list.replay_failed", download.Exception?.GetBaseException().Message ?? "");
            return;
        }
        var replay = download.Result;
        var chart = _charts[_chart];
        var request = new PlayRequest(shell.Hosts.SongSelect!.CatalogRevision, _song!.Descriptor.Key, _song.Descriptor.AudioAsset,
            [new PlayerChartRequest(shell.Hosts.PlayerSide == 1 ? LocalPlayerSlot.PlayerTwo : LocalPlayerSlot.PlayerOne,
                chart.Key, chart.ChartAsset, chart.Course!.Value)])
        {
            Review = new ReviewRequest(replay.Replay.Inputs, TimeSpan.FromMilliseconds(replay.Play.InputOffsetMs ?? 0), replay.Player),
        };
        if (!shell.PlayRequests.TryRequestPlay(request))
        {
            _error = T("score_list.replay_failed", "busy");
            return;
        }
        // Back from the replay, Song Select opens on this song again (as after a play).
        shell.Hosts.SongSelect!.RememberCurrentSong();
        Console.WriteLine($"Watching {replay.Play.Name}'s {replay.Play.Score} on {chart.Key}.");
        IsOpen = false;
    }

    public IEnumerable<RenderQuad> Quads()
    {
        if (!IsOpen || _song is null)
            return [];
        var quads = new List<RenderQuad>
        {
            painter.Rect(0, 0, OverlayPainter.StageWidth, OverlayPainter.StageHeight, new RenderColor(0, 0, 0, 0.55f)),
            painter.Panel(Left, Top, Width, Height, (30, 31, 38), (90, 92, 104), radius: 16),
            painter.Text(_song.Descriptor.Title.English ?? _song.Descriptor.Title.Primary, Left + 30, Top + 34, Width - 60, 36, anchor: 0),
        };
        // Difficulties, then leaderboard / own plays.
        var tabWidth = (Width - 60) / _charts.Length;
        for (var index = 0; index < _charts.Length; index++)
        {
            var chart = _charts[index];
            var x = Left + 30 + index * tabWidth;
            var name = $"{CourseNames[(int)chart.Course!.Value]}{(chart.Level is { } level ? $" ★{level}" : "")}";
            if (index == _chart)
                quads.Add(painter.Rect(x + 4, Top + 64, tabWidth - 8, 34, new RenderColor(1, 0.75f, 0.2f, 0.9f)));
            quads.Add(painter.Text(name, x + tabWidth / 2, Top + 81, tabWidth - 16, 26));
        }
        quads.Add(painter.Text(T(_mine ? "score_list.mine" : "score_list.leaderboard"), Left + 30, Top + 122, 400, 26, anchor: 0,
            tint: (255, 205, 80)));
        quads.Add(painter.Text(T("score_list.switch"), Left + Width - 30, Top + 122, 420, 22, anchor: 1, tint: (170, 175, 190)));

        var listTop = Top + 150;
        string? message = _client is null ? T("score_list.offline")
            : _watching is not null ? T("score_list.loading_replay")
            : _error ?? (list() switch
            {
                { IsCompletedSuccessfully: true, Result.Count: 0 } => T("score_list.empty"),
                { IsCompletedSuccessfully: true } => null,
                { IsCompleted: true } failed => T("score_list.failed", failed.Exception?.GetBaseException().Message ?? ""),
                _ => T("score_list.loading"),
            });
        if (message is not null)
            quads.Add(painter.Text(message, Left + Width / 2, listTop + 120, Width - 80, 30));
        else
        {
            var rows = Rows();
            var first = Math.Clamp(_selection - VisibleRows / 2, 0, Math.Max(0, rows.Count - VisibleRows));
            for (var index = first; index < Math.Min(rows.Count, first + VisibleRows); index++)
            {
                var y = listTop + (index - first) * RowHeight;
                if (index == _selection)
                    quads.Add(painter.Rect(Left + 20, y, Width - 40, RowHeight - 4, new RenderColor(1, 1, 1, 0.14f)));
                quads.AddRange(row(rows[index], y + (RowHeight - 4) / 2));
            }
        }
        quads.Add(painter.Text(T("score_list.keys"), Left + Width / 2, Top + Height - 26, Width - 60, 22, tint: (170, 175, 190)));
        return quads;
    }

    private IEnumerable<RenderQuad> row(ScoreRow play, float centreY)
    {
        var crown = play.Crown switch { 3 => T("score_list.crown.all_great"), 2 => T("score_list.crown.full_combo"), 1 => T("score_list.crown.clear"), _ => "" };
        var when = play.PlayedAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";
        yield return painter.Text(play.Rank is { } rank ? $"#{rank}" : "", Left + 40, centreY, 70, 26, anchor: 0, tint: (170, 175, 190));
        yield return painter.Text(_mine ? when : play.Name, Left + 115, centreY, 300, 26, anchor: 0);
        yield return painter.Text(play.Score.ToString("N0", CultureInfo.InvariantCulture), Left + 560, centreY, 140, 26, anchor: 1);
        yield return painter.Text(crown, Left + 580, centreY, 130, 22, anchor: 0, tint: play.Crown >= 2 ? ((byte)255, (byte)205, (byte)80) : ((byte)150, (byte)200, (byte)255));
        yield return painter.Text($"{play.Great} / {play.Good} / {play.Miss}", Left + Width - 40, centreY, 200, 24, anchor: 1, tint: (200, 205, 215));
    }
}
