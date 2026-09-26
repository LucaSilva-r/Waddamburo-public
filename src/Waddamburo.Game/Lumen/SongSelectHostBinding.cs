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
            lumen.RegisterMethod("NotifyOpenFolder", _ => openFolder());
            lumen.RegisterMethod("NotifyCloseFolder", _ => clearFolderSurfaces());
            lumen.RegisterMethod("NotifyEndCourseSelect", notifySelection);
            // SetSelectedMusic(genre, song): the movie's final pick; genre = the mode-switch folder with
            // song -1 moves to the other song select (traced SetSelectedMusic(7|8, -1), then Terminate).
            lumen.RegisterMethod("SetSelectedMusic", call =>
            {
                if (call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Number
                    && (int)call.Arguments[0].AsNumber() == _session.Catalog.ModeSwitchCategory)
                    ModeSwitchRequested = true;
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
        _session.StopPreview();
        _sounds?.StopVoice();
    }

    private bool assignInitialData()
    {
        if (_assigned)
            return true;
        if (_player is null)
            return false;
        _assigned = true;
        foreach (var category in _session.Catalog.Categories)
        {
            invoke(
                "AssignMusic",
                LumenHostValue.FromString(category.AuthoredLabel),
                // A folder that decides at once still counts one item (traced 1 for the mode switch).
                LumenHostValue.FromNumber((category.Presentation & SongCategoryPresentation.FolderEnd) != 0
                    ? 1 : category.Songs.Length),
                LumenHostValue.FromNumber((int)category.Presentation),
                LumenHostValue.FromNumber(-1));
        }
        invoke("SetSelectedMusic", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(-1));
        // SetPlayer(player, joined, hasData, ...): traced (1, true, false, ...) for a guest on the right
        // drum alone, both joined for two players. hasData (a profile) lets the boards show crowns.
        foreach (var player in new[] { 0, 1 })
        {
            invoke("SetPlayer", LumenHostValue.FromNumber(player), LumenHostValue.FromBoolean(_sides.Contains(player)),
                LumenHostValue.FromBoolean(_crowns[player] is not null && _sides.Contains(player)),
                // ponytail: jukuLevel -1 (none); the game sent 12 for a carded player (session13-card), from
                // player data we do not keep yet.
                LumenHostValue.FromNumber(-1));
            invoke("SetScoreType", LumenHostValue.FromNumber(player), LumenHostValue.FromNumber(0),
                LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        }
        _parts?.ShowGuestNames(_sides);
        _sounds?.SelectCategoryVoice(_session.Catalog.Categories[0].Name);
        return true;
    }

    private LumenHostValue publishMusicInfo(LumenHostCall call)
    {
        invoke("SetMusicData", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        invoke("SetPlayerBits", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        if (!_session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song))
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

    private LumenHostValue notifyPreview(LumenHostCall call)
    {
        var category = integer(call, 0);
        var song = integer(call, 1);
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

    private LumenHostValue clearSelectionSurfaces()
    {
        requirePlayer().RemoveNativeFill("song_name_center");
        requirePlayer().RemoveNativeFill("song_name_detail");
        return LumenHostValue.Undefined;
    }

    private LumenHostValue openFolder()
    {
        return clearSelectionSurfaces();
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
        _session.Select(request.Category, request.Song, course(request.PlayerOneCourse), course(request.PlayerTwoCourse));
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
