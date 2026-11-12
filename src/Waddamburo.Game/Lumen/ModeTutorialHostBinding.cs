using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>The mode tutorials' independent movie/host protocols.</summary>
public sealed class ModeTutorialHostBinding(bool aiBattle, int side, bool twoPlayers,
    Action<string, int>? playCue = null, Func<string, bool>? prepareMusic = null,
    Action? playMusic = null, bool countdown = true) : ILumenHostBinding
{
    private readonly SongSelectTimer _timer = new();
    private LumenPlayer? _movie;
    private bool _initialized;
    private LumenPlayer? _counter;
    private bool _musicReady;
    private bool _musicStarted;
    private string? _musicName;
    public bool Finished { get; private set; }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var name in new[] { "Lumen", "LumenMethod", "LumenTutorial" })
            context.RegisterObject(name, methods =>
            {
                methods.RegisterMethod("AllFinish", _ => { Finished = true; return LumenHostValue.Undefined; });
                methods.RegisterMethod("PlayVO", call => { var id = number(call, 0); if (id is >= 0 and <= 5) playCue?.Invoke("VO_WAIATTR", id == 0 ? 0 : id + 1); return LumenHostValue.Undefined; });
                methods.RegisterMethod("PlaySE", call => { var id = number(call, 0); var cue = WaiwaiEffectCue(id); if (cue is { } value) playCue?.Invoke("SE_WAITUTOR", value); return LumenHostValue.Undefined; });
                methods.RegisterMethod("PlaySound", call =>
                {
                    var bank = number(call, 0) switch { 415 => "VO_AIBTL_INFO", 416 => "SE_AIBTL_INFO", var id => AiSectionResultBinding.Bank(id) };
                    if (bank is not null) playCue?.Invoke(bank, number(call, 1));
                    return LumenHostValue.Undefined;
                });
                methods.RegisterMethod("SetBGM", prepare);
                methods.RegisterMethod("SetBGMTutorial", prepare);
                methods.RegisterMethod("IsReadyStream", _ => LumenHostValue.FromBoolean(_musicReady));
                methods.RegisterMethod("IsReadyStreamTutorial", _ => LumenHostValue.FromBoolean(_musicReady));
                methods.RegisterMethod("PlayStream", _ => { if (_musicReady && !_musicStarted) { _musicStarted = true; playMusic?.Invoke(); } return LumenHostValue.Undefined; });
                methods.RegisterMethod("PlayStreamTutorial", _ => { if (_musicReady && !_musicStarted) { _musicStarted = true; playMusic?.Invoke(); } return LumenHostValue.Undefined; });
                methods.RegisterMethod("Pause", static _ => LumenHostValue.FromBoolean(false));
                methods.RegisterMethod("StartTimer", _ =>
                {
                    if (countdown) _timer.Start(120);
                    _counter?.TryInvokeCallback("Resume", []);
                    return LumenHostValue.Undefined;
                });
                methods.RegisterMethod("IsTimeup", _ => LumenHostValue.FromBoolean(_timer.IsTimeUp));
            });
    }

    private LumenHostValue prepare(LumenHostCall call)
    {
        var argument = call.Arguments.FirstOrDefault();
        var name = argument.Kind == LumenHostValueKind.Text ? argument.AsString()
            : argument.Kind == LumenHostValueKind.Fields && argument.AsObject().TryGetValue("jingle", out var jingle)
                && jingle.Kind == LumenHostValueKind.Text ? jingle.AsString() : null;
        if (name != _musicName)
        {
            _musicName = name;
            _musicStarted = false;
            _musicReady = name is not null && prepareMusic?.Invoke(name) == true;
        }
        return LumenHostValue.Undefined;
    }

    public void AttachTimer(LumenPlayer counter)
    {
        _counter = counter;
        counter.TryInvokeCallback("SetMusicNum", [LumenHostValue.FromNumber(0)]);
        counter.TryInvokeCallback("Start", [LumenHostValue.FromNumber(120), LumenHostValue.FromBoolean(countdown)]);
        counter.TryInvokeCallback("Pause", []);
    }

    public void Attach(LumenPlayer movie) { _movie = movie; Poll(); }

    public void Poll()
    {
        if (_movie is not { CallbackNames.Length: > 0 } movie) return;
        if (!_initialized)
        {
            _initialized = true;
            if (aiBattle)
                movie.TryInvokeCallback("SetPlayerJoin", [LumenHostValue.FromBoolean(twoPlayers || side == 0), LumenHostValue.FromBoolean(twoPlayers || side == 1)]);
            movie.TryInvokeCallback("Start", []);
        }
        else if (aiBattle) movie.TryInvokeCallback("Start", []);
        Finished |= RetryGameHostBinding.IsEnd(movie);
    }

    /// <summary>Observed request-to-bank mapping; unused requests remain unsupported.</summary>
    public static int? WaiwaiEffectCue(int request) => request switch
    {
        >= 0 and <= 8 => request,
        >= 9 and <= 12 => request + 1,
        13 => 15,
        14 => 17,
        _ => null,
    };

    private static int number(LumenHostCall call, int index) => call.Arguments.Length > index
        && call.Arguments[index].Kind == LumenHostValueKind.Number ? (int)call.Arguments[index].AsNumber() : 0;
}
