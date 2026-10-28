using Waddamburo.Game.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Patching;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

[Flags]
public enum AuthoredSongCourseBits
{
    None = 0,
    Easy = 1 << 0,
    Normal = 1 << 1,
    Hard = 1 << 2,
    Oni = 1 << 3,
    HiddenEasy = 1 << 4,
    HiddenNormal = 1 << 5,
    HiddenHard = 1 << 6,
    HiddenOni = 1 << 7,
    All = Easy | Normal | Hard | Oni | HiddenEasy | HiddenNormal | HiddenHard | HiddenOni,
}

public readonly record struct SongSelectCoursePresentation(
    AuthoredSongCourseBits SelectionRestrictions,
    int EasyStars,
    int NormalStars,
    int HardStars,
    int OniStars,
    int HiddenEasyStars,
    int HiddenNormalStars,
    int HiddenHardStars,
    int HiddenOniStars)
{
    public static SongSelectCoursePresentation FromSong(SongSelectSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return new SongSelectCoursePresentation(
            restrictions(song),
            song.Level(TaikoCourse.Easy) ?? 0,
            song.Level(TaikoCourse.Normal) ?? 0,
            song.Level(TaikoCourse.Hard) ?? 0,
            song.Level(TaikoCourse.Oni) ?? 0,
            0,
            0,
            0,
            song.Level(TaikoCourse.Ura) ?? 0);
    }

    // osu!lazer songs, and any other song missing one of Easy to Oni, get restriction bits (the rest
    // keep the game's board, zero bits): every slot the song lacks is marked invalid, hidden
    // Easy/Normal/Hard always. The nonzero bits put the board in the movie's restricted mode
    // (MusicInfo.HasInvalidCourse): Oni shows at once (CheckMania, no right-ka presses) and missing
    // courses are greyed and cannot be picked. A song with Ura in that mode would show Ura in Oni's
    // place, so a song with Ura keeps the game's board even when it misses a course.
    // ponytail: such a song's missing courses stay pickable and fail to load back to Song Select.
    private static AuthoredSongCourseBits restrictions(SongSelectSong song)
    {
        var complete = song.HasCourse(TaikoCourse.Easy) && song.HasCourse(TaikoCourse.Normal)
            && song.HasCourse(TaikoCourse.Hard) && song.HasCourse(TaikoCourse.Oni);
        if (song.Descriptor.Key.Source != SongSourceKind.OsuLazer && (complete || song.HasCourse(TaikoCourse.Ura)))
            return AuthoredSongCourseBits.None;
        var available = AuthoredSongCourseBits.None;
        foreach (var (course, bit) in new[]
        {
            (TaikoCourse.Easy, AuthoredSongCourseBits.Easy), (TaikoCourse.Normal, AuthoredSongCourseBits.Normal),
            (TaikoCourse.Hard, AuthoredSongCourseBits.Hard), (TaikoCourse.Oni, AuthoredSongCourseBits.Oni),
            (TaikoCourse.Ura, AuthoredSongCourseBits.HiddenOni),
        })
        {
            if (song.HasCourse(course))
                available |= bit;
        }
        return AuthoredSongCourseBits.All & ~available;
    }

}

public enum SongSelectSoundRequestKind
{
    Effect,
    SystemEffect,
    PlayerEffect,
    LoopVoice,
}

public readonly record struct SongSelectSoundRequest(
    SongSelectSoundRequestKind Kind,
    System.Collections.Immutable.ImmutableArray<LumenHostValue> Arguments);

/// <summary>The typed terminal selection emitted by the authored Song Select movie.</summary>
public readonly record struct AuthoredSongSelectionRequest(
    int Category,
    int Song,
    int? PlayerOneCourse,
    int? PlayerTwoCourse)
{
    /// <summary>
    /// NotifyEndCourseSelect(category, song, course1P, course2P). An absent side's course is ''
    /// (traced: a solo right-drum player sends (2, 1, '', 3)).
    /// </summary>
    public static AuthoredSongSelectionRequest FromHostCall(LumenHostCall call)
    {
        var request = new AuthoredSongSelectionRequest(
            requiredInteger(call, 0),
            requiredInteger(call, 1),
            optionalInteger(call, 2),
            optionalInteger(call, 3));
        if (request.PlayerOneCourse is null && request.PlayerTwoCourse is null)
            throw new ArgumentOutOfRangeException(nameof(call), "Song selection names no player's course.");
        return request;
    }

    /// <summary>Authored courses use eight normal/hidden slots, not the catalog enum.</summary>
    public static int ToCatalogCourse(int authoredCourse) => authoredCourse switch
    {
        0 => (int)TaikoCourse.Easy,
        1 => (int)TaikoCourse.Normal,
        2 => (int)TaikoCourse.Hard,
        3 => (int)TaikoCourse.Oni,
        7 => (int)TaikoCourse.Ura,
        _ => throw new ArgumentOutOfRangeException(nameof(authoredCourse), authoredCourse,
            "The authored course slot has no supported catalog course."),
    };

    private static int requiredInteger(LumenHostCall call, int index)
    {
        if ((uint)index >= (uint)call.Arguments.Length || call.Arguments[index].Kind != LumenHostValueKind.Number)
            throw new ArgumentException($"Song selection argument {index} must be a number.", nameof(call));
        var value = call.Arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(call), $"Song selection argument {index} must be a finite integer.");
        return (int)value;
    }

    private static int? optionalInteger(LumenHostCall call, int index)
    {
        if ((uint)index >= (uint)call.Arguments.Length
            || call.Arguments[index].Kind is LumenHostValueKind.Undefined or LumenHostValueKind.Null
            || call.Arguments[index].Kind == LumenHostValueKind.Text && call.Arguments[index].AsString().Length == 0)
        {
            return null;
        }
        if (call.Arguments[index].Kind == LumenHostValueKind.Number
            && !double.IsFinite(call.Arguments[index].AsNumber()))
        {
            return null;
        }
        return requiredInteger(call, index);
    }
}

public interface ISongSelectSoundController
{
    void RequestSound(SongSelectSoundRequest request);

    void SelectCategoryVoice(string category);

    void StopVoice();
}

/// <summary>Translates the authored Song Select protocol into one catalog-pinned session.</summary>
public sealed partial class SongSelectHostBinding : ILumenHostBinding, IDisposable
{
    private readonly SongSelectSession _session;
    private readonly ISongSelectSoundController? _sounds;
    private readonly IDonPresentationController? _don;
    private LumenPlayer? _player;

    /// <summary>The list's fast scrolling (see <see cref="SongSelectScroll"/>), once the movie is attached.</summary>
    public SongSelectScroll? Scroll { get; private set; }
    private bool _assigned;
    private readonly SongSelectTimer _timer;
    private readonly IndicatorParts? _parts;
    private readonly int[] _sides; // the joined players' drums: 0 left, 1 right
    private readonly IReadOnlyDictionary<string, TaikoCrown>?[] _crowns; // per drum; null: a guest
    private readonly CountdownCues _countdownCues = new();

    /// <summary>Plays a cue the game itself plays (the countdown's ticks and voices).</summary>
    public Action<string, int>? PlayCue { get; init; }

    /// <summary>
    /// The cursor moved onto a song: fetch its score windows now, the movie asks for them once the
    /// cursor settles (traced 0.4 s later; fast scrolling never asks).
    /// </summary>
    public Action<SongSelectSong>? RankingWanted { get; init; }

    /// <summary>The songs marked favourite: their boards show the red coin icon (P toggles, see <see cref="ToggleFavourite"/>).</summary>
    public SongFavourites? Favourites { get; init; }

    /// <summary>The movie was patched with custom genres (Patching.SongSelectGenrePatch).</summary>
    public bool PatchedGenres { get; init; }

    /// <summary>Both players' course select shows Oni at once (the movie's isMania; the original needs right-rim hits).</summary>
    public bool ShowOni { get; init; }

    public bool VerticalLayout { get; private set; }
    private (LumenNativeSurfaceKey Surface, float Extent)? _scrollingTitle;
    private int _titleAge;
    private bool _verticalRows;
    private bool _courseWasShown;
    private readonly List<string> _genreLabels = [];

    /// <summary>
    /// The song whose difficulty selector is open (traced NotifyBeginCourseSelect(genre, song)); null again
    /// once the list asks for a ranking (backing out).
    /// </summary>
    public SongSelectSong? CourseSelectSong { get; private set; }

    /// <summary>The custom song's individual chart choices while its selector is open.</summary>
    public SongChartCarousel? ChartCarousel { get; private set; }

    public bool ChartCarouselReady => ChartCarousel is not null
        && _player?.InstanceFrame("main") is { } frame
        && _player.TryGetLabelFrame("main", "course", out var at) && frame.Frame == at;

    public void RefreshChartUtilities()
    {
        if (ChartCarousel is not { } choices) return;
        var player = requirePlayer();
        // Green's CourseResource defines BOARDTYPE_COURSE as zero. Static class
        // constants may live outside the root's enumerable script properties.
        var course = player.ReadScriptValue("_global.CourseResource.BOARDTYPE_COURSE") as double? ?? 0;
        foreach (var side in choices.Sides.Where(choices.IsUtility))
        {
            var type = player.CallScriptNumber("main.courseScene.courseSelect", "GetCursorType", null, side);
            if (type is { } value && value == course)
                choices.LeaveUtilities(side);
        }
    }

    public bool NativeChartFocused(int side) => _sides.Contains(side) && CourseSelectSong is not null
        && ChartCarousel is null && !LeavingCourseSelect
        && requirePlayer().CallScriptNumber("main.courseScene.courseSelect", "GetCursorType", null, side)
            == (requirePlayer().ReadScriptValue("_global.CourseResource.BOARDTYPE_COURSE") as double? ?? 0);

    public bool ChartReturnSelected(int side)
    {
        var player = requirePlayer();
        var back = player.ReadScriptValue("_global.CourseResource.BOARDTYPE_BACK") as double? ?? 3;
        var type = player.CallScriptNumber("main.courseScene.courseSelect", "GetCursorType", null, side);
        return type == back;
    }

    public void ConfirmChart(int side)
    {
        var choices = ChartCarousel ?? throw new InvalidOperationException("No chart selector is open.");
        if (choices.IsConfirmed(side)) return;
        choices.Confirm(side);
        updateChartCursorStyles();
        if (!choices.Ready || _courseSelect is not { } at) return;
        // The custom carousel owns the chart keys, while Main owns the final
        // voice, Don motions and motion wait. Let it complete the same branch
        // as a stock song; NotifyEndCourseSelect still resolves our chart keys.
        if (completeNativeChartSelection()) return;
        _ = notifySelection(new LumenHostCall([
            LumenHostValue.FromNumber(at.Category), LumenHostValue.FromNumber(at.Song),
            _sides.Contains(0) ? LumenHostValue.FromNumber(3) : LumenHostValue.Null,
            _sides.Contains(1) ? LumenHostValue.FromNumber(3) : LumenHostValue.Null,
        ]));
    }

    private bool completeNativeChartSelection()
    {
        var player = requirePlayer();
        if (player.ReadScriptValue("_global.CourseSelectScene.STATE_END") is not double end
            || player.ReadScriptValue("_global.CourseSelectScene.NextScene_End") is not double next) return false;
        foreach (var side in _sides)
        {
            if (!player.TryWriteScriptValue($"main.courseScene.state.{side}", end)
                || !player.TryWriteScriptValue($"main.courseScene.isEnd.{side}", true)) return false;
        }
        return player.TryWriteScriptValue("main.courseScene.next", next)
            && player.TryCallScriptMethod("main", "Proc_SelectCourse", []);
    }

    /// <summary>Closes the custom choices through the movie's own return transition.</summary>
    public bool CancelChartSelection()
    {
        if (_courseSelect is { } at && CourseSelectSong is { } song)
            Picked = (_session.Catalog.Categories[at.Category].Key, song.Descriptor.Key);
        var player = requirePlayer();
        foreach (var side in _sides)
        {
            if (_optionOpen[side])
            {
                // Course_Back only changes the scene state. Close this drum's native
                // utility first, so its raised Don and hidden name board return too.
                var utility = _optionMode[side] == 1 ? "toneChange" : "sessionOption";
                player.TryCallScriptMethod($"main.courseScene.{utility}.{side}", "Skip_SlideOut",
                    [LumenHostValue.FromBoolean(false)]);
                // An opening panel cannot start its exit tween. Its scene controller
                // is retiring now, so it will not retry after the opening completes.
                if (player.ReadScriptValue($"main.courseScene.{utility}.{side}.isSlideOut") is false)
                    player.TrySetInstanceVisibility($"main/{(_optionMode[side] == 1 ? "toneChange" : "option")}_{side + 1}p_", false);
                _parts?.FadeSongSelectName(Array.IndexOf(_sides, side), visible: true);
                (_optionOpen[side], _speedShown[side]) = (false, -1);
            }
            // Skip_SlideOut waits if a utility is still opening. The Don's native
            // down tween also handles that interruption, and is a no-op once down.
            player.TryCallScriptMethod("main.resource", "TweenDon_Down", [LumenHostValue.FromNumber(side)]);
            // Course_Back ends this drum's controller, so Proc_Wait will no longer
            // clear an interrupted settings/cursor wait. Main waits for both slots.
            if (player.ReadScriptValue("_global.CourseSelectScene.WAIT_NONE") is double idle)
                player.TryWriteScriptValue($"main.courseScene.waitTween.{side}", idle);
        }
        var native = _sides.All(side => player.TryCallScriptMethod("main.courseScene", "Course_Back",
            [LumenHostValue.FromNumber(side)]));
        LeavingCourseSelect = native;
        if (!native)
        {
            ChartCarousel = null;
            CourseSelectSong = null;
            _courseSelect = null;
        }
        return native;
    }

    /// <summary>Backing out of the difficulty boards: they are closing, the list does not take steps yet.</summary>
    public bool LeavingCourseSelect { get; private set; }

    /// <summary>The song the player is on: the one whose difficulties are open, else the list's centre (null: a folder or nothing).</summary>
    public SongSelectSong? SongUnderCursor => CourseSelectSong
        ?? (_centre is { } centre && _session.Catalog.TryGetSong(centre.Category, centre.Song, out var song) ? song : null);

    /// <summary>The catalog revision play requests are pinned to.</summary>
    public long CatalogRevision => _session.Catalog.Revision;

    /// <summary>The score windows known for a song: per course (easy..ura), up to three players' bests.</summary>
    public Func<SongSelectSong, IReadOnlyList<RankingEntry>?[]?>? Rankings { get; init; }

    public SongSelectHostBinding(
        SongSelectSession session,
        ISongSelectSoundController? sounds = null,
        IDonPresentationController? don = null,
        TimeProvider? timeProvider = null,
        IndicatorParts? parts = null,
        int side = 0,
        bool twoPlayers = false,
        IReadOnlyDictionary<string, TaikoCrown>?[]? crowns = null,
        bool curvedScrolling = false)
    {
        CurvedScrolling = curvedScrolling;
        _browser = new VerticalSongBrowser(curvedScrolling ? .15f : 1,
            overlayCategoryHeader: curvedScrolling, categoryGapScale: curvedScrolling ? -.25f : null);
        _crowns = crowns ?? [null, null];
        _sides = twoPlayers ? [0, 1] : [side];
        _parts = parts;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sounds = sounds;
        _don = don;
        _timer = new SongSelectTimer(timeProvider);
    }

    /// <summary>The players decided the mode-switch folder (to the other song select).</summary>
    public bool ModeSwitchRequested { get; private set; }

    /// <summary>The players decided the how-to-play folder (the tutorial movie).</summary>
    public bool TutorialRequested { get; private set; }

    /// <summary>The players decided a library folder: Song Select reloads with that library.</summary>
    public SongSourceKind? LibraryRequested { get; private set; }

    /// <summary>The players decided a Group or Sort spine: Song Select reloads with the next grouping or order.</summary>
    public SongBrowseControl? BrowseRequested { get; private set; }

    /// <summary>Song Select reloads for the host (library, grouping, order, search): its music plays on.</summary>
    public bool Reloading { get; set; }

    /// <summary>The folder the cursor starts on.</summary>
    public int StartCategory { get; init; }

    /// <summary>The song the cursor starts on inside <see cref="StartCategory"/> (-1: on the folder).</summary>
    public int StartSong { get; init; } = -1;

    /// <summary>The folder and song of the last course decided (to come back to it).</summary>
    public (CategoryKey Category, SongKey Song)? Picked { get; private set; }

    private (int Category, int Song)? _courseSelect;

    /// <summary>
    /// Remembers the song the player is on (its difficulties' song, else the list's centre) as picked, so
    /// Song Select comes back to it: a play started from Waddamburo's own UI (a replay), not the movie.
    /// </summary>
    public void RememberCurrentSong()
    {
        if ((CourseSelectSong is not null ? _courseSelect : _centre) is { } at
            && _session.Catalog.TryGetSong(at.Category, at.Song, out var song))
            Picked = (_session.Catalog.Categories[at.Category].Key, song.Descriptor.Key);
    }

    private bool _restoredInitialSong;
    private int _restoreSongAttempts;

    /// <summary>Opens the remembered song's folder once its native browser is ready.</summary>
    public LumenInputSnapshot RestoreInitialSong(LumenInputSnapshot input)
    {
        if (_restoredInitialSong || StartSong < 0 || !_assigned || _player is null || CourseSelectSong is not null
            || !_session.Catalog.TryGetSong(StartCategory, StartSong, out _)
            || _player.InstanceFrame("main/genreSelect_") is not { Playing: false }) return input;
        if (!_player.TryGetLabelFrame("main", "music", out var musicFrame)
            || _player.InstanceFrame("main") is not { Playing: false } main || main.Frame != musicFrame)
            return input;
        // SetSelectedMusic retains the song's board in openIndex, but Reset initially
        // displays its closed category. Let the native controller reopen that board;
        // a regular category confirmation deliberately starts at the first song.
        if (_player.CallScriptNumber("_global.container", "GetCurrentMusicInfo", "genreIndex") != StartCategory)
            return input;
        if (_player.ReadScriptValue($"_global.container.isOpenFolder.{StartCategory}") is true)
        {
            _restoredInitialSong = true;
            return input;
        }
        if (_player.ReadScriptValue($"main.cpp.key.m_inputEnable.{_sides[0]}") is not true) return input;
        if (++_restoreSongAttempts > 60) { _restoredInitialSong = true; return input; }
        if ((_restoreSongAttempts & 1) == 0) return input;
        if (_restoreSongAttempts == 1 && Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1")
            Console.WriteLine($"[song-restore] category={StartCategory} song={StartSong} main={main.Frame}");
        return new LumenInputSnapshot([_sides[0] == 0 ? 'Z' : 'C']);
    }

    public void Attach(LumenPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        VerticalLayout = player.TryGetLabelFrame("main", "wd_vertical_v1", out _);
        _verticalRows = player.TryGetLabelFrame("main", "wd_vertical_rows_v2", out _);
        // A page jump sounds like a costume change (as the later games do), not like its ten rim hits.
        Scroll = new SongSelectScroll(player, () => _openFolder, side => PlayCue?.Invoke("SE_COM", side == 0 ? 19 : 20),
            circularFolders: _verticalRows, defaultPlayer: _sides[0]);
        if (_don is null)
            return;
        // Like entry, the movie's 2P slot already turns Katsu-chan inward (user-confirmed: the
        // mirrored camera faced him the wrong way).
        DonLumenBinding.Attach(player, _don, DonPresentationLayout.OpposedPlayers);
    }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(_ => LumenHostValue.Undefined);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("GetMusicData", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("GetPlayerData", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("IsStart", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("StartTimer", call =>
            {
                if (call.Arguments.Length != 1 || call.Arguments[0].Kind != LumenHostValueKind.Number)
                    throw new ArgumentException("StartTimer requires a duration in seconds.", nameof(call));
                _timer.Start(call.Arguments[0].AsNumber());
                // Countdown off: the timer holds its full time (the movie reads GetTimeSec, e.g. ExtendTimer).
                if (!(_parts?.Countdown ?? true))
                    _timer.Stop();
                _countdownCues.Reset();
                _parts?.StartCountdown(call.Arguments[0].AsNumber());
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("StopTimer", _ =>
            {
                _timer.Stop();
                _parts?.StopCountdown();
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("GetTimeSec", _ => LumenHostValue.FromNumber(Math.Ceiling(_timer.Remaining.TotalSeconds)));
            // With the cabinet countdown off the timer never runs out (the counter still shows).
            lumen.RegisterMethod("IsTimeup", _ =>
            {
                // Polled every frame: the countdown's cues follow the seconds left.
                if ((_parts?.Countdown ?? true) && _timer.IsRunning)
                    foreach (var (bank, cue) in _countdownCues.Advance((int)Math.Ceiling(_timer.Remaining.TotalSeconds)))
                        PlayCue?.Invoke(bank, cue);
                return LumenHostValue.FromBoolean((_parts?.Countdown ?? true) && _timer.IsTimeUp);
            });
            lumen.RegisterMethod("IsInitWait", _ =>
            {
                var trace = new BrowserStartupTrace(0);
                var ready = _parts?.PollReady() ?? true;
                trace.Lap("indicator-parts-init");
                return LumenHostValue.FromBoolean(ready && assignInitialData());
            });
            lumen.RegisterMethod("NotifyTimeSec", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("GetMusicInfo_Basic", publishMusicInfo);
            lumen.RegisterMethod("RequestSongBoardTexture_Short", call => publishBoard(call, SongBoardTextureKind.Compact));
            lumen.RegisterMethod("RequestSongBoardTexture_Long", call => publishBoard(call, SongBoardTextureKind.Expanded));
            lumen.RegisterMethod("NotifyStopBGM", notifyPreview);
            lumen.RegisterMethod("GetScore", static _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("GetRankingScore", publishRanking);
            lumen.RegisterMethod("NotifyOpenFolder", openFolder);
            lumen.RegisterMethod("NotifyCloseFolder", _ =>
            {
                _openFolder = -1;
                if (_favouritesChanged)
                    relistFavourites();
                return clearFolderSurfaces();
            });
            lumen.RegisterMethod("NotifyEndCourseSelect", notifySelection);
            // SetOptionMode(player, open, board): traced (0, true, 0) as a player's option board opens and
            // (0, false, 0) as it closes; the game fades that player's name board out under it meanwhile.
            lumen.RegisterMethod("SetOptionMode", call =>
            {
                var player = integer(call, 0);
                if (Array.IndexOf(_sides, player) is var board and >= 0)
                    _parts?.FadeSongSelectName(board, visible: !boolean(call, 1));
                if ((uint)player < 2)
                {
                    (_optionOpen[player], _speedShown[player]) = (boolean(call, 1), -1);
                    _optionMode[player] = integer(call, 2);
                }
                return LumenHostValue.Undefined;
            });
            // RequestFillrect(slot, tone): the tone board's name and icon (FillrectNum: 15-18 player 0's
            // name/icon leaving, then arriving; 19-22 player 1's). The clips are tone_name_ / tone_icon_ +
            // in_ / out_ + player; the game's art goes in them.
            lumen.RegisterMethod("RequestFillrect", call =>
            {
                var slot = integer(call, 0) - 15;
                if (slot is < 0 or >= 8)
                    return LumenHostValue.Undefined;
                var (player, part) = (slot / 4, slot % 4 >= 2 ? "in_" : "out_");
                var tone = integer(call, 1);
                var lumenPlayer = requirePlayer();
                lumenPlayer.SetNativeFill($"tone_name_{part}{player}", ToneSurfaces.Name(tone), ToneSurfaces.NamePlacement);
                if (ToneSurfaces.Icon(tone) is { } icon)
                    lumenPlayer.SetNativeFill($"tone_icon_{part}{player}", icon);
                else
                    lumenPlayer.RemoveNativeFill($"tone_icon_{part}{player}");
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("NotifyBeginCourseSelect", call =>
            {
                _titleAge = 0;
                _courseWasShown = false;
                _courseSelect = (integer(call, 0), integer(call, 1));
                CourseSelectSong = _session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song) ? song : null;
                ChartCarousel = song?.Descriptor.Key.Source is SongSourceKind.Tja or SongSourceKind.OsuLazer
                    ? new SongChartCarousel(song.Descriptor, _sides) : null;
                LeavingCourseSelect = false;
                // The option rows exist by now (not yet at setup): the speed row grows to Nijiiro's speeds.
                foreach (var player in _sides)
                    if (_speedRow[player] is null)
                        extendSpeedRow(player, TaikoGuest.Options[player].SpeedIndex);
                return LumenHostValue.Undefined;
            });
            // SetSelectedMusic(genre, song): the movie's final pick; genre = the mode-switch folder with
            // song -1 moves to the other song select (traced SetSelectedMusic(7|8, -1), then Terminate).
            lumen.RegisterMethod("SetSelectedMusic", call =>
            {
                if (call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Number)
                {
                    var genre = (int)call.Arguments[0].AsNumber();
                    if (genre == _session.Catalog.ModeSwitchCategory)
                        ModeSwitchRequested = true;
                    // Traced SetSelectedMusic(0, -1), Terminate, then the tutorial scene.
                    else if (genre == _session.Catalog.TutorialCategory)
                        TutorialRequested = true;
                    else if ((uint)genre < (uint)_session.Catalog.Categories.Length)
                    {
                        LibraryRequested = _session.Catalog.Categories[genre].Link;
                        BrowseRequested = _session.Catalog.Categories[genre].Control;
                    }
                }
                return LumenHostValue.FromBoolean(true);
            });
            lumen.RegisterMethod(
                "NotifyGenreFolder",
                notifyGenreFolder);
            if (_don is not null)
                DonLumenBinding.RegisterMotion(context, lumen, _don);
            lumen.RegisterMethod("RequestSE", call =>
            {
                // SE 5/32 is the cancel: on the difficulty boards, it is backing out to the list (traced). The
                // list is still closing them until the ranking request about half a second later, which ends
                // CourseSelectSong; meanwhile the scroll keeps the wheel's steps for then.
                // ponytail: if another cancel on the boards (an option panel) turns out to play it, track that.
                if (CourseSelectSong is not null && integer(call, 0) == 5 && integer(call, 1) == 32)
                    LeavingCourseSelect = true;
                return requestSound(SongSelectSoundRequestKind.Effect, call);
            });
            lumen.RegisterMethod("RequestSystemSE", call => requestSound(SongSelectSoundRequestKind.SystemEffect, call));
            lumen.RegisterMethod("RequestPlayerSE", call => requestSound(SongSelectSoundRequestKind.PlayerEffect, call));
            // RequestSE_Save(player, bank, cue): a player's sound on the course boards (traced (0, 5, 51));
            // the tone board previews its drum sounds with it (SE_SELECT_NEIRO_000_005, cue = tone).
            lumen.RegisterMethod("RequestSE_Save", call =>
            {
                if (call.Arguments.Length >= 3)
                    _sounds?.RequestSound(new SongSelectSoundRequest(SongSelectSoundRequestKind.Effect, call.Arguments[1..]));
                return LumenHostValue.Undefined;
            });
            // ponytail: RequestSE_Stop(player) cuts the player's last one; previews just overlap.
            lumen.RegisterMethod("RequestSE_Stop", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("NotifyPlayLoopVO", call => requestSound(SongSelectSoundRequestKind.LoopVoice, call));
            lumen.RegisterMethod("StopVoice", _ =>
            {
                _sounds?.StopVoice();
                return LumenHostValue.Undefined;
            });
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        // A host reload (library, grouping, order, search) keeps the music playing.
        if (!Reloading)
            _session.StopMusic();
        _sounds?.StopVoice();
    }

    private bool assignInitialData()
    {
        if (_assigned)
            return true;
        if (_player is null)
            return false;
        _assigned = true;
        var workTrace = new BrowserStartupTrace(0);
        // Let the movie own intermediate Return rows and both index conversions.
        if (_verticalRows && !requirePlayer().TryWriteScriptValue("_global.Resource.BACKBOARD_INTERVAL", 10d))
            throw new InvalidOperationException("The vertical browser needs Resource.BACKBOARD_INTERVAL.");
        installFavouriteIcon();
        _featureFolders.Clear();
        _genreLabels.Clear();
        workTrace.Lap("init-favourite");
        var genres = registerCustomGenres();
        workTrace.Lap("init-register-genres");
        var custom = 0;
        for (var index = 0; index < _session.Catalog.Categories.Length; index++)
        {
            var category = _session.Catalog.Categories[index];
            var label = category.AuthoredLabel;
            // AssignMusic's last argument picks a feature folder's slot (00-09) whose spine and banner
            // show host-drawn text; -1 = none (the baked イベント art). Named folders take a custom genre
            // (the patched ボーカロイド art: own colour and name slots) while there are slots; more than ten
            // left on feature art share its slots (see nameFeatureFolders).
            var featureSlot = -1;
            if (label == SongSelectCatalogView.FeatureLabel && custom < genres)
                label = customGenre(custom++, category);
            else if (label == SongSelectCatalogView.FeatureLabel)
            {
                featureSlot = _featureFolders.Count % FeatureSlots;
                _featureFolders.Add((index, featureSlot));
            }
            _genreLabels.Add(label);
            invoke(
                "AssignMusic",
                LumenHostValue.FromString(label),
                // A folder that decides at once still counts one item (traced 1 for the mode switch).
                LumenHostValue.FromNumber((category.Presentation & SongCategoryPresentation.FolderEnd) != 0
                    ? 1 : category.Songs.Length),
                LumenHostValue.FromNumber((int)category.Presentation),
                LumenHostValue.FromNumber(featureSlot));
        }
        workTrace.Lap("init-assign-music");
        bindBrowserNavigation();
        bindChartCursorStyles();
        bindIndependentCourseInput();
        nameFeatureFolders(StartCategory);
        workTrace.Lap("init-navigation");
        // A song board takes its colours from GetGenreLabel(its folder) in MusicBoard.ResetMusicInfo(genre,
        // music): the search folder's boards show their song's library instead (see SongSelectCatalogView.SourceLabel).
        if (!requirePlayer().TryWrapScriptMethod("_global.MusicBoard.prototype", "ResetMusicInfo", (call, original) =>
            {
                // ResetMusicInfo(genre, board): the board index counts the ✕ back boards; Board2MusicIndex maps it.
                _boardLabel = call.Arguments.Length >= 2 && call.Arguments[0].Kind == LumenHostValueKind.Number
                    && call.Arguments[1].Kind == LumenHostValueKind.Number
                    && (uint)call.Arguments[0].AsNumber() < (uint)_session.Catalog.Categories.Length
                    && _session.Catalog.Categories[(int)call.Arguments[0].AsNumber()].Key == SongSelectCatalogView.SearchKey
                    && requirePlayer().CallScriptNumber("_global.CppConnection.resource", "Board2MusicIndex", null,
                        call.Arguments[0].AsNumber(), call.Arguments[1].AsNumber()) is { } music
                    && _session.Catalog.TryGetSong((int)call.Arguments[0].AsNumber(), (int)music, out var song)
                        ? SongSelectCatalogView.SourceLabel(song.Descriptor.Key.Source) : null;
                try
                {
                    return original();
                }
                finally
                {
                    _boardLabel = null;
                }
            })
            || !requirePlayer().TryWrapScriptMethod("_global.CppConnection.resource", "GetGenreLabel",
                (_, original) => _boardLabel is { } label ? LumenHostValue.FromString(label) : original()))
            Console.Error.WriteLine("Song Select has no MusicBoard.ResetMusicInfo; search results keep the folder's colour.");
        workTrace.Lap("init-method-wrappers");
        // Callback_SetSelectedMusic(genre, music) resets the movie's selection onto that song's board.
        invoke("SetSelectedMusic", LumenHostValue.FromNumber(StartCategory), LumenHostValue.FromNumber(StartSong));
        // SetPlayer(player, joined, hasData, ...): traced (1, true, false, ...) for a guest on the right
        // drum alone, both joined for two players. hasData (a profile) lets the boards show crowns.
        foreach (var player in new[] { 0, 1 })
        {
            invoke("SetPlayer", LumenHostValue.FromNumber(player), LumenHostValue.FromBoolean(_sides.Contains(player)),
                LumenHostValue.FromBoolean(_crowns[player] is not null && _sides.Contains(player)),
                // ponytail: jukuLevel -1 (none); the game sent 12 for a carded player (session13-card), from
                // player data we do not keep yet.
                LumenHostValue.FromNumber(-1));
            // (player, myScore, otherwiseScore, localrankingScore): traced (p, 2, 0, 1) for a carded
            // player, all 0 for an empty drum.
            var carded = _crowns[player] is not null && _sides.Contains(player);
            invoke("SetScoreType", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(carded ? 2 : 0),
                LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(carded ? 1 : 0));
            // Play options (the arcade's were for card players only; here anyone's): every item offered, the
            // drum's last choice selected. As the movie's own developer setup: Assign(p, 0b11111111),
            // SetDefaultSession(p, item bits, 真打), SetValidOptionMenu(p, session, tone).
            // 音色 (drum sound): Green's tones 1-5 offered beside the plain drum (always listed), the drum's last.
            if (_sides.Contains(player))
            {
                tryInvoke("AssignTone", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber((1 << TaikoGuest.ToneCount) - 2));
                tryInvoke("SetDefaultTone", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(TaikoGuest.Tones[player]));
                var options = TaikoGuest.Options[player];
                tryInvoke("AssignSession", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(0b11111111));
                tryInvoke("SetDefaultSession", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(options.SessionBits),
                    LumenHostValue.FromBoolean(options.Shinuchi));
                tryInvoke("SetValidOptionMenu", LumenHostValue.FromNumber(player), LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(true));
            }
        }
        workTrace.Lap("init-players");
        // CourseResource.isMania[player] is what repeated right-rim hits set (Callback_SetCourse's
        // isMania_ also writes it, but with the remembered course too); CheckMania reads it per board.
        if (ShowOni)
            foreach (var player in new[] { 0, 1 })
                if (!requirePlayer().TryWriteScriptValue($"_global.CppConnection.resource.course.isMania.{player}", true))
                    Console.Error.WriteLine("Song Select has no CourseResource.isMania; Oni stays hidden.");
        _parts?.ShowGuestNames(_sides);
        _sounds?.SelectCategoryVoice(_session.Catalog.Categories[StartCategory].Name);
        workTrace.Lap("init-names-and-voice");
        return true;
    }

    // SetMusicData(rank, musicBits): each bit shows MusicInfo.LABEL_M_ICON[bit] on the board (spine and
    // detail). The authored labels are time_new, time_limited, character_0; the host appends the red coin
    // bubble ("usercup_1P", otherwise a per-player course icon) for favourites.
    private const int FavouriteIconBit = 3;

    // Diagnostic: WADDAMBURO_ICON_GALLERY=1 gives the n-th song of a folder the n-th frame label of the
    // board icon clip (mod their count), to see every icon the boards can show.
    private static readonly string[]? IconGallery = Environment.GetEnvironmentVariable("WADDAMBURO_ICON_GALLERY") == "1"
        ? ["time_new", "present_1P", "present_2P", "present", "usercup_1P", "usercup_2P", "usercup", "officialcup_1P",
            "officialcup_2P", "officialcup", "vs_1P", "vs_2P", "vs", "time_limited", "character_1", "feature", "entry2players"]
        : null;

    private void installFavouriteIcon()
    {
        var labels = IconGallery?.Select(static (label, bit) => (label, bit)) ?? [("usercup_1P", FavouriteIconBit)];
        foreach (var (label, bit) in labels)
            if (!requirePlayer().TryWriteScriptValue($"_global.MusicInfo.LABEL_M_ICON.{bit}", label))
                Console.Error.WriteLine("Song Select has no MusicInfo.LABEL_M_ICON; favourites show no icon.");
    }

    private int musicBits(int songIndex, SongSelectSong song)
    {
        if (IconGallery is not { } gallery)
            return Favourites?.Contains(song.Descriptor.Key) == true ? 1 << FavouriteIconBit : 0;
        Console.WriteLine($"Icon gallery: {song.Descriptor.Title.English ?? song.Descriptor.Title.Primary} -> {gallery[songIndex % gallery.Length]}.");
        return 1 << (songIndex % gallery.Length);
    }

    private LumenHostValue publishMusicInfo(LumenHostCall call)
    {
        var found = _session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song);
        invoke("SetMusicData", LumenHostValue.FromNumber(0),
            LumenHostValue.FromNumber(found ? musicBits(integer(call, 1), song) : 0));
        invoke("SetPlayerBits", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        if (!found)
            return LumenHostValue.Undefined;
        var courses = SongSelectCoursePresentation.FromSong(song);
        invoke("SetInvalidCourse", LumenHostValue.FromNumber((int)courses.SelectionRestrictions));
        invoke(
            "SetStar",
            LumenHostValue.FromNumber(courses.EasyStars),
            LumenHostValue.FromNumber(courses.NormalStars),
            LumenHostValue.FromNumber(courses.HardStars),
            LumenHostValue.FromNumber(courses.OniStars));
        invoke(
            "SetStarHidden",
            LumenHostValue.FromNumber(courses.HiddenEasyStars),
            LumenHostValue.FromNumber(courses.HiddenNormalStars),
            LumenHostValue.FromNumber(courses.HiddenHardStars),
            LumenHostValue.FromNumber(courses.HiddenOniStars));
        publishCrowns(song);
        return LumenHostValue.Undefined;
    }

    // SetClearBits(player, silver, gold): one bit per authored course slot (ura = hidden oni, 7);
    // the movie shows a gold crown for a full combo, silver for a clear.
    private void publishCrowns(SongSelectSong song)
    {
        foreach (var player in _sides)
        {
            if (_crowns[player] is not { } crowns)
                continue;
            int silver = 0, gold = 0;
            foreach (var chart in song.Descriptor.Charts)
            {
                if (chart.Course is not { } course || !crowns.TryGetValue(chart.Key.ToString(), out var crown))
                    continue;
                var bit = 1 << (course == TaikoCourse.Ura ? 7 : (int)course);
                silver |= bit;
                if (crown == TaikoCrown.FullCombo)
                    gold |= bit;
            }
            invoke("SetClearBits", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(silver),
                LumenHostValue.FromNumber(gold));
        }
    }

    private LumenHostValue publishBoard(LumenHostCall call, SongBoardTextureKind kind)
    {
        var slot = integer(call, 0);
        var category = integer(call, 1);
        var songIndex = integer(call, 2);
        if (!_session.Catalog.TryGetSong(category, songIndex, out _))
            return LumenHostValue.Undefined;
        if (VerticalLayout && slot != 14)
            kind = kind == SongBoardTextureKind.Compact ? SongBoardTextureKind.HorizontalCompact : SongBoardTextureKind.HorizontalExpanded;
        var surface = _session.GetBoardTexture(category, songIndex, kind);
        if ((uint)slot < (uint)_boards.Length)
            _boards[slot] = (category, songIndex);
        if (slot is 13 or 14)
            _centre = (category, songIndex);
        var slotName = slot switch
        {
            13 => "song_name_center",
            14 => "song_name_detail",
            _ when slot is >= 0 and <= 12 => $"song_name{slot}",
            _ => throw new ArgumentOutOfRangeException(nameof(call), "Song board slot must be between 0 and 14."),
        };
        if (VerticalLayout && slot != 14)
        {
            var extent = _session.GetHorizontalExtent(surface);
            requirePlayer().SetNativeFill(slotName, surface, new(-28, 200 - extent + 10 / 1.1f, 56, extent));
        }
        else requirePlayer().SetNativeFill(slotName, surface);
        if (VerticalLayout && slot == 14)
        {
            var horizontal = _session.GetBoardTexture(category, songIndex, SongBoardTextureKind.HorizontalExpanded);
            if (_scrollingTitle?.Surface != horizontal) _titleAge = 0;
            _scrollingTitle = (horizontal, _session.GetHorizontalExtent(horizontal));
            showHorizontalTitle();
        }
        invoke("UpdateMusicBoard", LumenHostValue.FromNumber(slot));
        return LumenHostValue.Undefined;
    }

    private string? _boardLabel;


    // Answered inside the call (the movie only stores RankingScore while it waits): per course 0-4
    // (ura 4), ranks 0-2, empty ones as (course, rank, 0, "") (traced).
    private LumenHostValue publishRanking(LumenHostCall call)
    {
        // The movie reports no cancel of course select: backing out plays SE 5/32 and asks for the centre
        // song's ranking again, which it never does while a difficulty board is open (traced).
        CourseSelectSong = null;
        ChartCarousel = null;
        LeavingCourseSelect = false;
        _courseWasShown = false;
        var rankings = _session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song)
            ? Rankings?.Invoke(song) : null;
        for (var course = 0; course < 5; course++)
            for (var rank = 0; rank < 3; rank++)
            {
                var entry = rankings?[course] is { } lines && rank < lines.Count ? lines[rank] : null;
                invoke("RankingScore", LumenHostValue.FromNumber(course), LumenHostValue.FromNumber(rank),
                    LumenHostValue.FromNumber(entry?.Score ?? 0), LumenHostValue.FromString(entry?.Name ?? ""));
            }
        return LumenHostValue.FromBoolean(true);
    }

    private LumenHostValue notifyPreview(LumenHostCall call)
    {
        var category = integer(call, 0);
        var song = integer(call, 1);
        if (RankingWanted is { } wanted && _session.Catalog.TryGetSong(category, song, out var selected))
            wanted(selected);
        if (_session.Preview(category, song) is null)
            clearSelectionSurfaces();
        return LumenHostValue.Undefined;
    }

    private LumenHostValue requestSound(SongSelectSoundRequestKind kind, LumenHostCall call)
    {
        if (Scroll?.Jumping != true || kind != SongSelectSoundRequestKind.PlayerEffect)
            _sounds?.RequestSound(new SongSelectSoundRequest(kind, call.Arguments));
        return LumenHostValue.Undefined;
    }

    // The patched custom genres (SongSelectGenrePatch) become genres the movie knows: their labels join
    // GenreResource.MUSICINFO_KEY (AssignMusic looks a folder's key up there). Their count, 0 unpatched.
    private int registerCustomGenres()
    {
        const string keys = "_global.GenreResource.MUSICINFO_KEY";
        var player = requirePlayer();
        if (!PatchedGenres)
            return 0;
        if (player.ReadScriptValue(keys + ".length") is not double length)
            return 0;
        for (var slot = 0; slot < SongSelectGenrePatch.Slots; slot++)
            if (!player.TryWriteScriptValue($"{keys}.{(int)length + slot}", SongSelectGenrePatch.Label(slot)))
                return 0;
        // Initialization runs inside an AVM callback. Indexed writes are staged;
        // AssignMusic must see the extended length before that callback commits.
        if (!player.TryWriteScriptValue(keys + ".length", length + SongSelectGenrePatch.Slots))
            return 0;
        return SongSelectGenrePatch.Slots;
    }

    // Colours for folders that bring none (no box.def colour): picked by name, so a folder keeps its colour.
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (60, 170, 240), (130, 210, 60), (255, 102, 171), (170, 110, 255), (255, 150, 30),
        (40, 200, 170), (230, 70, 70), (210, 170, 20), (90, 120, 230), (240, 110, 200),
    ];

    /// <summary>A folder's default colour, stable for its name.</summary>
    public static (byte R, byte G, byte B) PaletteColour(string name)
    {
        var hash = 2166136261u;
        foreach (var character in name)
            hash = (hash ^ character) * 16777619u;
        return Palette[hash % (uint)Palette.Length];
    }

    // A named folder on custom genre <paramref name="slot"/>: its colour and names. Its label.
    private string customGenre(int slot, SongSelectCategory category)
    {
        var player = requirePlayer();
        // The recoloured art's body layer is multiplied by the folder's colour (its highlights stay light).
        var colour = category.Tint ?? PaletteColour(category.Name);
        player.SetColorTransform(SongSelectGenrePatch.Colours[slot],
            new LumenRenderColor(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1));
        // Genres outline their spine name in a dark shade of their colour.
        var outline = category.Outline ?? ((uint)(colour.R * 2 / 5) << 16 | (uint)(colour.G * 2 / 5) << 8 | (uint)(colour.B * 2 / 5));
        // Pictures (event-folder style) replace the drawn names.
        var (spine, header, box) = category.Images;
        player.SetNativeFill(SongSelectGenrePatch.Slot("tate", slot), spine is not null ? _session.GetImage(spine)
            : _session.GetFolderName(category.Name, VerticalLayout ? SongBoardTextureKind.HorizontalCompact : SongBoardTextureKind.Compact, outline));
        // Built-in folders' pattern over the front (revealed as it opens); none: plain.
        if (category.Art is { } art)
        {
            player.SetNativeFill(SongSelectGenrePatch.Slot("pattern", slot), _session.GetFolderArt(art, FolderArtPart.Pattern));
        }
        // The J-POP header quad sits right of its tab's visual centre (short "J-POP" hides it): shifted back.
        player.SetNativeFill(SongSelectGenrePatch.Slot("yoko", slot), header is not null ? _session.GetImage(header)
            : _session.GetFolderName(category.Name, SongBoardTextureKind.Expanded),
            new LumenNativeSurfacePlacement(-128 - SongSelectGenrePatch.HeaderShift, -28, 256, 56));
        if ((category.Description ?? category.Art?.Lines) is { Length: > 0 } description)
            // Text inside an open folder is outlined in black (the genres' own descriptions).
            player.SetNativeFill(SongSelectGenrePatch.Slot("desc", slot), _session.GetFolderDescription(description, 0x000000, VerticalLayout));
        if (box is not null || category.Art is not null)
            player.SetNativeFill(SongSelectGenrePatch.Slot("image", slot),
                box is not null ? _session.GetImage(box) : _session.GetFolderArt(category.Art!, FolderArtPart.Box));
        return SongSelectGenrePatch.Label(slot);
    }

    // The movie has ten feature name slots (feature_board_{tate,yoko}_00-09); folder i uses slot
    // i % 10, and each slot shows the folder nearest the cursor that uses it. Fewer than ten spines are
    // on screen at once, so two folders sharing a slot are never both visible.
    private const int FeatureSlots = 10;
    private readonly List<(int Category, int Slot)> _featureFolders = [];
    private readonly string?[] _slotNames = new string?[FeatureSlots];

    private void nameFeatureFolders(int cursor)
    {
        var count = _session.Catalog.Categories.Length;
        foreach (var group in _featureFolders.GroupBy(static folder => folder.Slot))
        {
            var nearest = group.MinBy(folder =>
            {
                var distance = Math.Abs(folder.Category - cursor);
                return Math.Min(distance, count - distance);
            });
            var name = _session.Catalog.Categories[nearest.Category].Name;
            if (_slotNames[group.Key] == name)
                continue;
            _slotNames[group.Key] = name;
            requirePlayer().SetNativeFill($"feature_board_tate_{group.Key:00}",
                _session.GetFolderName(name, VerticalLayout ? SongBoardTextureKind.HorizontalCompact : SongBoardTextureKind.Compact));
            requirePlayer().SetNativeFill($"feature_board_yoko_{group.Key:00}",
                _session.GetFolderName(name, SongBoardTextureKind.Expanded));
        }
    }

    /// <summary>
    /// Once per tick: while more feature folders than name slots are listed, follows the folder cursor
    /// as it moves (NotifyGenreFolder only reports it once the list settles) so the spines coming into
    /// view already carry their names.
    /// </summary>
    public void Poll()
    {
        if (!VerticalLayout && ChartCarouselReady)
            updateChartCursorStyles();
        if (VerticalLayout && _player is { } vertical)
        {
            if (CourseSelectSong is not null && vertical.ReadScriptValue("main.courseScene.isBackEvent") is true)
                LeavingCourseSelect = true;
            vertical.TrySetInstanceLayoutVisibility("main/coursestarView_", false);
            vertical.TrySetInstanceLayoutVisibility("main/courseBG_1p_", false);
            vertical.TrySetInstanceLayoutVisibility("main/courseBG_2p_", false);
            vertical.TrySetInstanceLayoutVisibility("main/coursePanel_/song_name_detail", false);
            if (CourseSelectSong is not null && _courseSelect is { } courseAt)
            {
                var title = _session.GetBoardTexture(courseAt.Category, courseAt.Song, SongBoardTextureKind.HorizontalCourse);
                var extent = _session.GetHorizontalExtent(title);
                vertical.SetNativeFill("wd_course_title", title,
                    new(-280, -24, extent, DifficultySelectLayout.TitleHeight));
                layoutDifficultySelect(vertical);
                updateChartCursorStyles();
            }
            // Waiwai has no solo-ranking refresh after cancelling its difficulty boards.
            // Follow the native main timeline as well, so its browser can resume without that callback.
            if (CourseSelectSong is not null && vertical.InstanceFrame("main") is { } main)
            {
                if (vertical.TryGetLabelFrame("main", "course", out var courseFrame) && main.Frame == courseFrame)
                    _courseWasShown = true;
                else if (_courseWasShown && !main.Playing
                    && vertical.TryGetLabelFrame("main", "music", out var musicFrame) && main.Frame == musicFrame)
                {
                    CourseSelectSong = null;
                    LeavingCourseSelect = false;
                    ChartCarousel = null;
                    _courseWasShown = false;
                }
            }
            if (_verticalRows)
            {
                completeNativeBrowserDecision();
                updateVerticalBrowser();
            }
            else
                foreach (var board in Boards)
                {
                    var path = "main/genreSelect_/" + board;
                    if (vertical.TryGetInstanceLocalTransform(path, out var pose))
                        vertical.TrySetInstancePosition(path, pose.X, -SongSelectCircularPath.Inset(pose.X));
                }
            _titleAge++;
            showHorizontalTitle();
        }
        showSpeeds();
        if (_featureFolders.Count <= FeatureSlots || _player is null)
            return;
        // Each spine is one of the 13 recycled boards; its own folder names it (scoped native fill).
        for (var board = 0; board < Boards.Length; board++)
        {
            if (_player.ReadScriptValue($"_global.CppConnection.resource.musicboardList.{board}.musicInfo.genreIndex")
                    is not double shown || (int)shown == _boardFolders[board])
                continue;
            _boardFolders[board] = (int)shown;
            var folder = _featureFolders.FindIndex(entry => entry.Category == (int)shown);
            if (folder < 0)
                continue;
            _player.SetNativeFill($"{Boards[board]}/feature_board_tate_{_featureFolders[folder].Slot:00}",
                _session.GetFolderName(_session.Catalog.Categories[(int)shown].Name, VerticalLayout ? SongBoardTextureKind.HorizontalCompact : SongBoardTextureKind.Compact));
        }
        // The banner over the centre (feature_board_yoko_*) follows the cursor.
        if (_player.CallScriptNumber("_global.container", "GetCurrentMusicInfo", "genreIndex") is { } cursor
            && (int)cursor != _polledCursor)
        {
            _polledCursor = (int)cursor;
            if ((uint)_polledCursor < (uint)_session.Catalog.Categories.Length)
                nameFeatureFolders(_polledCursor);
        }
    }

    // BoardContainer's boards in musicboardList order (traced: song_name12 is in musicBoard_right6_).
    private static readonly string[] Boards =
    [
        "musicBoard_left6_", "musicBoard_left5_", "musicBoard_left4_", "musicBoard_left3_", "musicBoard_left2_",
        "musicBoard_left1_", "musicBoard_center_", "musicBoard_right1_", "musicBoard_right2_", "musicBoard_right3_",
        "musicBoard_right4_", "musicBoard_right5_", "musicBoard_right6_",
    ];
    private readonly int[] _boardFolders = [.. Enumerable.Repeat(-1, 13)];
    private int _polledCursor = -1;

    private LumenHostValue notifyGenreFolder(LumenHostCall call)
    {
        var category = integer(call, 0);
        var song = integer(call, 1);
        var isFolderOpen = boolean(call, 2);
        if (_featureFolders.Count > FeatureSlots && (uint)category < (uint)_session.Catalog.Categories.Length)
            nameFeatureFolders(category);
        if (song == -1 && !isFolderOpen && (uint)category < (uint)_session.Catalog.Categories.Length)
            _sounds?.SelectCategoryVoice(_session.Catalog.Categories[category].Name);
        return LumenHostValue.Undefined;
    }

    private (int Category, int Song)? _centre; // the song the centre board's titles show
    private int _openFolder = -1;
    private bool _favouritesChanged;

    // GenreResource.FAVORITE: the folder's id in the movie's genre list.
    private const int FavouriteGenreId = 10;

    // The movie's own genre.SetMusicNum(id, count) updates the folder's GenreInfo and its open index
    // (no callback exports it).
    private void relistFavourites()
    {
        _favouritesChanged = false;
        if (Favourites is null || _session.Catalog.RefreshFavourites(Favourites.Songs) is not { } count)
            return;
        if (!requirePlayer().TryCallScriptMethod("_global.CppConnection.resource.genre", "SetMusicNum",
                [LumenHostValue.FromNumber(FavouriteGenreId), LumenHostValue.FromNumber(count)]))
            Console.Error.WriteLine("Song Select has no genre list; the favourites folder updates on the next load.");
    }
    private readonly (int Category, int Song)?[] _boards = new (int, int)?[15]; // the song each board slot shows

    /// <summary>
    /// Marks or unmarks the song under the cursor; its boards redraw their icons at once. The favourites
    /// folder itself changes when Song Select next loads. False when the cursor is on no song.
    /// </summary>
    public bool ToggleFavourite()
    {
        if (Favourites is null || _centre is not { } centre
            || !_session.Catalog.TryGetSong(centre.Category, centre.Song, out var song))
            return false;
        var key = song.Descriptor.Key;
        Console.WriteLine($"Favourite {(Favourites.Toggle(key) ? "added" : "removed")}: {key}.");
        // Inside the favourites folder its list stays until the folder closes (the boards index it).
        _favouritesChanged = true;
        if (_openFolder != _session.Catalog.FavouritesCategory)
            relistFavourites();
        // The boards keep the info they asked for: invalidate the ones showing this song (the same song
        // may also sit in the favourites folder) and let them ask again (UpdateMusicBoard -> GetInfo).
        for (var slot = 0; slot < _boards.Length; slot++)
        {
            if (_boards[slot] is not { } board || !_session.Catalog.TryGetSong(board.Category, board.Song, out var shown)
                || shown.Descriptor.Key != key)
                continue;
            if (!requirePlayer().TryWriteScriptValue(
                    $"_global.CppConnection.resource.musicboardList.{slot}.musicInfo.isValidBasicInfo", false))
                Console.Error.WriteLine($"Song Select board {slot} has no musicInfo to refresh.");
            invoke("UpdateMusicBoard", LumenHostValue.FromNumber(slot));
        }
        return true;
    }

    private LumenHostValue clearSelectionSurfaces()
    {
        _centre = null;
        _scrollingTitle = null;
        requirePlayer().RemoveNativeFill("expand_/song_name_detail");
        requirePlayer().RemoveNativeFill("song_name_center");
        requirePlayer().RemoveNativeFill("song_name_detail");
        return LumenHostValue.Undefined;
    }

    private void showHorizontalTitle()
    {
        if (_scrollingTitle is not { } title) return;
        var offset = _verticalRows ? 0 : SongTitleMarquee.Offset(_titleAge, title.Extent);
        var height = _verticalRows && !_browserExpandedTitle ? 64 : 100;
        requirePlayer().SetNativeFill("expand_/song_name_detail", title.Surface,
            new(-height / 2f, 200 - title.Extent + offset + 10 / 1.1f, height, title.Extent));
    }

    private void showCategoryFrame(string body, int category, bool visible)
    {
        var player = requirePlayer();
        var root = body[..body.LastIndexOf('/')] + "/wd_category_frame";
        player.TrySetInstanceLayoutVisibility(root, visible);
        if (!visible || (uint)category >= (uint)_genreLabels.Count) return;
        if (player.TryGetLabelFrame(root, _genreLabels[category], out var frame)
            && player.InstanceFrame(root)?.Frame != frame)
            player.TryGotoFrame(root, frame);
        if (!player.TryGetInstanceLocalTransform(body + "/expand_/wd_native_top", out var top)
            || !player.TryGetInstanceLocalTransform(body + "/expand_/wd_native_bottom", out var bottom)) return;
        var start = top.X - 9;
        var height = (bottom.X + 9 - start) * .65f;
        sizeFrameOutline(root + "/", start, Math.Max(48, height) / .65f, 14, 474);
        for (var part = 0; part < 9; part++)
        {
            var slice = VerticalCategoryFrame.Slice(part / 3, part % 3, height, start, 14, 474);
            player.TrySetInstanceTransform(root + $"/part_{part}", new(slice.Width, 0, 0, slice.Height, slice.X, slice.Y));
        }
        foreach (var child in new[] { "folder_", "expand_", "backboard_" })
            player.TrySetInstanceLayoutVisibility(body + "/" + child, false);
        var art = (uint)category < (uint)_session.Catalog.Categories.Length
            ? _session.Catalog.Categories[category].Art : null;
        player.TrySetInstanceTransform(root + "/wd_pattern_mask",
            new((Math.Max(height, 48) - 8) / .65f / 640, 0, 0, (460 - 8 / 1.1f) / 720,
                start + Math.Max(height, 48) / .65f / 2, 244));
        for (var tile = 0; tile < 3; tile++)
        {
            var path = root + $"/wd_rows_pattern_{tile}";
            player.TrySetInstanceLayoutVisibility(path, art is not null && tile == 0);
            if (art is null || tile != 0) continue;
            var ancestor = root[..root.LastIndexOf('/')];
            var slot = ancestor[(ancestor.LastIndexOf('/') + 1)..] + $"/wd_rows_pattern_{tile}";
            player.SetNativeFill(slot, _session.GetFolderArt(art, FolderArtPart.Pattern));
            player.TrySetInstanceTransform(path,
                new(0, 460, 600f / .65f, 0, start + 300 / .65f, 244));
        }
        var peg = Math.Clamp((75 - height) / 20, 0, 1);
        player.TrySetInstanceAlpha(root + "/wd_peg_outline", peg);
        player.TrySetInstanceAlpha(root + "/wd_peg_fill", peg);
        player.TrySetInstanceTransform(root + "/wd_peg_outline",
            new(0, 35.2f / 1.1f, 16 / .65f, 0, start - 4 / .65f, 14 + 17.6f / 1.1f));
        player.TrySetInstanceTransform(root + "/wd_peg_fill",
            new(4 / .65f, 0, 0, 27.2f / 1.1f, start + 2 / .65f, 14 + 17.6f / 1.1f));
    }

    private void sizeFrameOutline(string root, float top, float length, float left, float right)
    {
        var player = requirePlayer();
        var center = top + length / 2;
        var middle = (left + right) / 2;
        // Opaque strips bridge the native corner textures' transparent fringe.
        // They meet on the same outer bounds instead of exposing a one-pixel notch.
        player.TrySetInstanceTransform(root + "wd_border_top", new(4 / .65f, 0, 0, right - left, top + 2 / .65f, middle));
        player.TrySetInstanceTransform(root + "wd_border_bottom", new(4 / .65f, 0, 0, right - left, top + length - 2 / .65f, middle));
        player.TrySetInstanceTransform(root + "wd_border_start", new(length, 0, 0, 4 / 1.1f, center, left + 2 / 1.1f));
        player.TrySetInstanceTransform(root + "wd_border_end", new(length, 0, 0, 4 / 1.1f, center, right - 2 / 1.1f));
    }
    // Starting on a song (back from a play), the movie asks for the centre board's titles and then
    // notifies the folder open for that same song: keep them. No NotifyStopBGM follows until the cursor
    // moves, so that song's preview starts here.
    private LumenHostValue openFolder(LumenHostCall call)
    {
        _openFolder = integer(call, 0);
        if (_centre != (integer(call, 0), integer(call, 1)))
            clearSelectionSurfaces();
        return integer(call, 1) >= 0 ? notifyPreview(call) : LumenHostValue.Undefined;
    }

    private LumenHostValue clearFolderSurfaces()
    {
        clearSelectionSurfaces();
        for (var slot = 0; slot <= 12; slot++)
        {
            // Other independently opened folders retain their recycled boards and title surfaces.
            if (_verticalRows && _boards[slot] is { } board
                && (requirePlayer().ReadScriptValue($"_global.container.isOpenFolder.{board.Category}") is true
                    || _browser.SelectedGenre >= 0 && _browser.Reveal(board.Category) > 0))
                continue;
            requirePlayer().RemoveNativeFill($"song_name{slot}");
        }
        _session.StopPreview();
        return LumenHostValue.Undefined;
    }

    private LumenHostValue notifySelection(LumenHostCall call)
    {
        var request = AuthoredSongSelectionRequest.FromHostCall(call);
        static int course(int? authored) => authored is null or < 0 ? -1
            : AuthoredSongSelectionRequest.ToCatalogCourse(authored.Value);
        // Each joined drum's play options (GetSession: the chosen items' bits), kept for its next song.
        foreach (var player in _sides)
            if (requirePlayer().TryInvokeCallback("GetSession", [LumenHostValue.FromNumber(player)], out var bits)
                && bits.Kind == LumenHostValueKind.Number)
                TaikoGuest.Options[player] = TaikoPlayOptions.FromSession((int)bits.AsNumber(), selectedSpeed(player));
        // Each joined drum's drum sound: the tone board's selected entry (ToneResource.selected / list).
        foreach (var player in _sides)
            if (requirePlayer().ReadScriptValue($"_global.CppConnection.resource.tone.selected.{player}") is IConvertible index
                && requirePlayer().ReadScriptValue($"_global.CppConnection.resource.tone.list.{player}.{(int)index.ToDouble(System.Globalization.CultureInfo.InvariantCulture)}") is IConvertible tone)
                TaikoGuest.Tones[player] = Math.Clamp((int)tone.ToDouble(System.Globalization.CultureInfo.InvariantCulture), 0, TaikoGuest.ToneCount - 1);
        var selection = ChartCarousel is { } choices
            ? _session.SelectCharts(request.Category, request.Song,
                _sides.Contains(0) ? choices.Choice(0).Key : null,
                _sides.Contains(1) ? choices.Choice(1).Key : null, TaikoGuest.Options)
            : _session.Select(request.Category, request.Song, course(request.PlayerOneCourse), course(request.PlayerTwoCourse),
                TaikoGuest.Options);
        Picked = (_session.Catalog.Categories[request.Category].Key, selection.Song);
        return LumenHostValue.Undefined;
    }

    // Nijiiro's speeds on Green's 音符のはやさ row (ふつう, ばいそく, さんばい, よんばい): the row's lists grow to
    // every speed, each shown with Green's art for its range (its item id too, so GetSession still reports
    // that range) and our text drawn in its place; the exact one is the row's selected index.
    private const string SessionRoot = "_global.CppConnection.resource.session";
    private const string SpeedRowLabel = "音符のはやさ";
    private readonly int?[] _speedRow = new int?[2];
    private readonly bool[] _optionOpen = new bool[2];
    private readonly int[] _optionMode = new int[2];
    private readonly int[] _speedShown = [-1, -1];
    private LumenNativeSurfacePlacement? _speedBox;

    private void extendSpeedRow(int player, int selected)
    {
        var lumen = requirePlayer();
        for (var row = 0; lumen.ReadScriptValue($"{SessionRoot}.list.{player}.{row}.label") is { } label; row++)
        {
            if (label.ToString() != SpeedRowLabel)
                continue;
            var path = $"{SessionRoot}.list.{player}.{row}";
            for (var index = 0; index < TaikoPlayOptions.Speeds.Length; index++)
            {
                var icon = new TaikoPlayOptions(false, index, false, false, TaikoRandom.None).SpeedIcon;
                (string Array, object Value)[] entries =
                [
                    ("itemIndex", (double)(icon - 1)),
                    ("itemLabel", icon switch { 1 => "ふつう", 2 => "ばいそく", 3 => "さんばい", _ => "よんばい" }),
                    ("itemID", (double)(icon == 1 ? 0 : icon)),
                ];
                foreach (var (array, value) in entries)
                    lumen.TryWriteScriptValue($"{path}.{array}.{index}", value);
            }
            lumen.TryWriteScriptValue($"{SessionRoot}.selected.{player}.{row}", (double)selected);
            _speedRow[player] = row;
            return;
        }
        Console.Error.WriteLine("Song Select has no 音符のはやさ row; speeds stay Green's.");
    }

    private int? selectedSpeed(int player) =>
        _speedRow[player] is { } row && requirePlayer().ReadScriptValue($"{SessionRoot}.selected.{player}.{row}") is IConvertible index
            ? Math.Clamp((int)index.ToDouble(System.Globalization.CultureInfo.InvariantCulture), 0, TaikoPlayOptions.Speeds.Length - 1) : null;

    // While a speed row is on screen: its selector's label clips (in_ the item shown, out_ the one leaving)
    // carry the speeds' text; ふつう keeps Green's art.
    private void showSpeeds()
    {
        if (_player is null)
            return;
        for (var player = 0; player < 2; player++)
        {
            if (!_optionOpen[player] || _speedRow[player] is not { } row)
                continue;
            var selector = $"option_{player + 1}p_/frame_/item{row}_";
            if (_player.ReadClipMember(selector, "current") is not { Kind: LumenHostValueKind.Number } current)
                continue;
            // The selector counted the row's items when the board was built, before the row grew.
            if (_player.ReadClipMember(selector, "itemNum") is { Kind: LumenHostValueKind.Number } count
                && (int)count.AsNumber() != TaikoPlayOptions.Speeds.Length)
                _player.TryWriteClipMember(selector, "itemNum", TaikoPlayOptions.Speeds.Length);
            var index = (int)current.AsNumber();
            if (index == _speedShown[player])
                continue;
            _speedBox ??= labelBox($"{selector}/in_");
            var leaving = _speedShown[player];
            _speedShown[player] = index;
            _player.SetInstanceOverlay($"{selector}/in_", SpeedText.Key(index, _speedBox.Value), _speedBox.Value, replace: true);
            _player.SetInstanceOverlay($"{selector}/out_", leaving < 0 ? null : SpeedText.Key(leaving, _speedBox.Value), _speedBox.Value, replace: true);
        }
    }

    // A name tag's lettering (NameText.Box's proportions) centred on the label art, in
    // its clip's own coordinates (measured once, before any text covers it).
    private LumenNativeSurfacePlacement labelBox(string clip)
    {
        // A third larger than a name tag: the board's pill is drawn smaller than the name boards.
        var (width, height) = (NameText.Box.Width * 1.3f, NameText.Box.Height * 1.3f);
        if (_player!.TryGetInstanceBounds(clip, out var bounds) && _player.TryGetInstanceTransform(clip, out var transform)
            && transform.M11 != 0 && transform.M22 != 0)
            return new((bounds.X + bounds.Width / 2 - transform.X) / transform.M11 - width / 2,
                (bounds.Y + bounds.Height / 2 - transform.Y) / transform.M22 - height / 2, width, height);
        return new(-width / 2, -height / 2, width, height); // ponytail: centred on the clip when the art cannot be measured
    }

    private LumenPlayer requirePlayer() =>
        _player ?? throw new InvalidOperationException("Song Select host is not attached to a Lumen player.");

    private void tryInvoke(string name, params LumenHostValue[] arguments)
    {
        if (!requirePlayer().TryInvokeCallback(name, arguments))
            Console.Error.WriteLine($"Song Select has no callback '{name}'.");
    }

    private void invoke(string name, params LumenHostValue[] arguments)
    {
        if (!requirePlayer().TryInvokeCallback(name, arguments))
            throw new InvalidOperationException($"Song Select did not accept callback '{name}'.");
    }

    private static int integer(LumenHostCall call, int index)
    {
        if ((uint)index >= (uint)call.Arguments.Length || call.Arguments[index].Kind != LumenHostValueKind.Number)
            throw new ArgumentException($"Host argument {index} must be a number.", nameof(call));
        var value = call.Arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(call), $"Host argument {index} must be a finite integer.");
        return (int)value;
    }

    private static bool boolean(LumenHostCall call, int index)
    {
        if ((uint)index >= (uint)call.Arguments.Length || call.Arguments[index].Kind != LumenHostValueKind.Boolean)
            throw new ArgumentException($"Host argument {index} must be a boolean.", nameof(call));
        return call.Arguments[index].AsBoolean();
    }

}
