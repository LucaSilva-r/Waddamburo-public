using Waddamburo.Catalog;
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
    AuthoredSongCourseBits InvalidCourses,
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

/// <summary>Translates the authored Song Select protocol into one catalog-pinned session.</summary>
public sealed class SongSelectHostBinding : ILumenHostBinding, IDisposable
{
    private static readonly string[] NotificationMethods =
    [
        "SetMotion", "RequestSE", "RequestSystemSE",
    ];

    private readonly SongSelectSession _session;
    private LumenPlayer? _player;
    private bool _assigned;

    public SongSelectHostBinding(SongSelectSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void Attach(LumenPlayer player) => _player = player ?? throw new ArgumentNullException(nameof(player));

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(_ => LumenHostValue.Undefined);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("GetMusicData", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("GetPlayerData", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("IsStart", _ => LumenHostValue.FromBoolean(true));
            lumen.RegisterMethod("IsInitWait", _ => LumenHostValue.FromBoolean(assignInitialData()));
            lumen.RegisterMethod("GetMusicInfo_Basic", publishMusicInfo);
            lumen.RegisterMethod("RequestSongBoardTexture_Short", call => publishBoard(call, SongBoardTextureKind.Compact));
            lumen.RegisterMethod("RequestSongBoardTexture_Long", call => publishBoard(call, SongBoardTextureKind.Expanded));
            lumen.RegisterMethod("NotifyStopBGM", notifyPreview);
            lumen.RegisterMethod("NotifyOpenFolder", _ => clearSelectionSurfaces());
            lumen.RegisterMethod("NotifyCloseFolder", _ => clearFolderSurfaces());
            lumen.RegisterMethod("NotifyEndCourseSelect", notifySelection);
            lumen.RegisterMethod("NotifyGenreFolder", _ => LumenHostValue.Undefined);
            foreach (var name in NotificationMethods)
                lumen.RegisterMethod(name, _ => LumenHostValue.Undefined);
        });
    }

    public void Dispose() => _session.StopPreview();

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
                LumenHostValue.FromNumber(category.Songs.Length),
                LumenHostValue.FromNumber((int)category.Presentation),
                LumenHostValue.FromNumber(-1));
        }
        invoke("SetSelectedMusic", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(-1));
        invoke(
            "SetPlayer",
            LumenHostValue.FromNumber(0),
            LumenHostValue.FromBoolean(true),
            LumenHostValue.FromBoolean(true),
            LumenHostValue.FromNumber(30));
        return true;
    }

    private LumenHostValue publishMusicInfo(LumenHostCall call)
    {
        invoke("SetMusicData", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        invoke("SetPlayerBits", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
        if (!_session.Catalog.TryGetSong(integer(call, 0), integer(call, 1), out var song))
            return LumenHostValue.Undefined;
        var courses = SongSelectCoursePresentation.FromSong(song);
        invoke("SetInvalidCourse", LumenHostValue.FromNumber((int)courses.InvalidCourses));
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
        return LumenHostValue.Undefined;
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

    private LumenHostValue clearSelectionSurfaces()
    {
        requirePlayer().RemoveNativeFill("song_name_center");
        requirePlayer().RemoveNativeFill("song_name_detail");
        return LumenHostValue.Undefined;
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
        _session.Select(integer(call, 0), integer(call, 1), integer(call, 2), optionalInteger(call, 3, -1));
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

    private static int optionalInteger(LumenHostCall call, int index, int fallback)
    {
        if ((uint)index >= (uint)call.Arguments.Length
            || call.Arguments[index].Kind is LumenHostValueKind.Undefined or LumenHostValueKind.Null)
        {
            return fallback;
        }
        return integer(call, index);
    }

}
