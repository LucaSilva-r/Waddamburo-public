using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
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
            // Nonzero restrictions activate a different authored selection mode.
            // Missing charts are represented by zero stars, not restriction bits.
            AuthoredSongCourseBits.None,
            song.Level(TaikoCourse.Easy) ?? 0,
            song.Level(TaikoCourse.Normal) ?? 0,
            song.Level(TaikoCourse.Hard) ?? 0,
            song.Level(TaikoCourse.Oni) ?? 0,
            0,
            0,
            0,
            song.Level(TaikoCourse.Ura) ?? 0);
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
public sealed class SongSelectHostBinding : ILumenHostBinding, IDisposable
{
    private readonly SongSelectSession _session;
    private readonly ISongSelectSoundController? _sounds;
    private readonly IDonPresentationController? _don;
    private LumenPlayer? _player;
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

    /// <summary>The song whose difficulty selector is open (traced NotifyBeginCourseSelect(genre, song)).</summary>
    public SongSelectSong? CourseSelectSong { get; private set; }

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
        IReadOnlyDictionary<string, TaikoCrown>?[]? crowns = null)
    {
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

    /// <summary>The players decided a library folder: Song Select reloads with that library.</summary>
    public SongSourceKind? LibraryRequested { get; private set; }

    /// <summary>The folder the cursor starts on.</summary>
    public int StartCategory { get; init; }

    /// <summary>The song the cursor starts on inside <see cref="StartCategory"/> (-1: on the folder).</summary>
    public int StartSong { get; init; } = -1;

    /// <summary>The folder and song of the last course decided (to come back to it).</summary>
    public (CategoryKey Category, SongKey Song)? Picked { get; private set; }

    public void Attach(LumenPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
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
                LumenHostValue.FromBoolean((_parts?.PollReady() ?? true) && assignInitialData()));
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
            lumen.RegisterMethod("NotifyBeginCourseSelect", call =>
            {
                CourseSelectSong = _session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song) ? song : null;
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
                    else if ((uint)genre < (uint)_session.Catalog.Categories.Length)
                        LibraryRequested = _session.Catalog.Categories[genre].Link;
                }
                return LumenHostValue.FromBoolean(true);
            });
            lumen.RegisterMethod(
                "NotifyGenreFolder",
                notifyGenreFolder);
            if (_don is not null)
                DonLumenBinding.RegisterMotion(context, lumen, _don);
            lumen.RegisterMethod("RequestSE", call => requestSound(SongSelectSoundRequestKind.Effect, call));
            lumen.RegisterMethod("RequestSystemSE", call => requestSound(SongSelectSoundRequestKind.SystemEffect, call));
            lumen.RegisterMethod("RequestPlayerSE", call => requestSound(SongSelectSoundRequestKind.PlayerEffect, call));
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
        // A library switch reloads Song Select under the rainbow: its music plays on.
        if (LibraryRequested is null)
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
        installFavouriteIcon();
        var feature = 0;
        foreach (var category in _session.Catalog.Categories)
        {
            // AssignMusic's last argument picks a feature folder's slot (00-09) whose spine and banner
            // show host-drawn text; -1 = none (the baked イベント art).
            var featureSlot = -1;
            if (category.AuthoredLabel == SongSelectCatalogView.FeatureLabel && feature < 10)
            {
                featureSlot = feature++;
                requirePlayer().SetNativeFill($"feature_board_tate_{featureSlot:00}",
                    _session.GetFolderName(category.Name, SongBoardTextureKind.Compact));
                requirePlayer().SetNativeFill($"feature_board_yoko_{featureSlot:00}",
                    _session.GetFolderName(category.Name, SongBoardTextureKind.Expanded));
            }
            invoke(
                "AssignMusic",
                LumenHostValue.FromString(category.AuthoredLabel),
                // A folder that decides at once still counts one item (traced 1 for the mode switch).
                LumenHostValue.FromNumber((category.Presentation & SongCategoryPresentation.FolderEnd) != 0
                    ? 1 : category.Songs.Length),
                LumenHostValue.FromNumber((int)category.Presentation),
                LumenHostValue.FromNumber(featureSlot));
        }
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
        }
        _parts?.ShowGuestNames(_sides);
        _sounds?.SelectCategoryVoice(_session.Catalog.Categories[StartCategory].Name);
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
        string?[] labels = IconGallery ?? [.. Enumerable.Repeat<string?>(null, FavouriteIconBit), "usercup_1P"];
        for (var bit = 0; bit < labels.Length; bit++)
            if (labels[bit] is { } label
                && !requirePlayer().TryWriteScriptValue($"_global.MusicInfo.LABEL_M_ICON.{bit}", label))
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
        var surface = _session.GetBoardTexture(category, songIndex, kind);
        if ((uint)slot < (uint)_boards.Length)
            _boards[slot] = (category, songIndex);
        if (slot is 13 or 14)
            _centre = (category, songIndex);
        requirePlayer().SetNativeFill(slot switch
        {
            13 => "song_name_center",
            14 => "song_name_detail",
            _ when slot is >= 0 and <= 12 => $"song_name{slot}",
            _ => throw new ArgumentOutOfRangeException(nameof(call), "Song board slot must be between 0 and 14."),
        }, surface);
        invoke("UpdateMusicBoard", LumenHostValue.FromNumber(slot));
        return LumenHostValue.Undefined;
    }

    // Answered inside the call (the movie only stores RankingScore while it waits): per course 0-4
    // (ura 4), ranks 0-2, empty ones as (course, rank, 0, "") (traced).
    private LumenHostValue publishRanking(LumenHostCall call)
    {
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
        _sounds?.RequestSound(new SongSelectSoundRequest(kind, call.Arguments));
        return LumenHostValue.Undefined;
    }

    private LumenHostValue notifyGenreFolder(LumenHostCall call)
    {
        var category = integer(call, 0);
        var song = integer(call, 1);
        var isFolderOpen = boolean(call, 2);
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
        requirePlayer().RemoveNativeFill("song_name_center");
        requirePlayer().RemoveNativeFill("song_name_detail");
        return LumenHostValue.Undefined;
    }

    // Starting on a song (back from a play), the movie asks for the centre board's titles and then
    // notifies the folder open for that same song: keep them.
    private LumenHostValue openFolder(LumenHostCall call)
    {
        _openFolder = integer(call, 0);
        return _centre == (integer(call, 0), integer(call, 1)) ? LumenHostValue.Undefined : clearSelectionSurfaces();
    }

    private LumenHostValue clearFolderSurfaces()
    {
        clearSelectionSurfaces();
        for (var slot = 0; slot <= 12; slot++)
            requirePlayer().RemoveNativeFill($"song_name{slot}");
        _session.StopPreview();
        return LumenHostValue.Undefined;
    }

    private LumenHostValue notifySelection(LumenHostCall call)
    {
        var request = AuthoredSongSelectionRequest.FromHostCall(call);
        static int course(int? authored) => authored is null or < 0 ? -1
            : AuthoredSongSelectionRequest.ToCatalogCourse(authored.Value);
        var selection = _session.Select(request.Category, request.Song, course(request.PlayerOneCourse), course(request.PlayerTwoCourse));
        Picked = (_session.Catalog.Categories[request.Category].Key, selection.Song);
        return LumenHostValue.Undefined;
    }

    private LumenPlayer requirePlayer() =>
        _player ?? throw new InvalidOperationException("Song Select host is not attached to a Lumen player.");

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
