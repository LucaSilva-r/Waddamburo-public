using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green;

/// <summary>The mode tutorials' independent movie/host protocols.</summary>
public sealed class ModeTutorialHostBinding(bool aiBattle, int side, bool twoPlayers,
    Action<string, int>? playCue = null, Func<string, bool>? prepareMusic = null,
    Action? playMusic = null) : ILumenHostBinding
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
                    // The AI tutorial calls it on every page; the counter on screen only resumes, so the time runs on.
                    // A tutorial always runs out, timer freeze or not.
                    if (!_timer.IsRunning) _timer.Start(120);
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
        counter.TryInvokeCallback("Start", [LumenHostValue.FromNumber(120), LumenHostValue.FromBoolean(true)]);
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
        // The AI tutorial's CppConnect.AllFinish only sets _global.isAllTutorialEnd for the game to read (Waiwai's calls
        // the host), and it never asks IsTimeup: the game closes it when the time runs out.
        if (aiBattle)
            Finished |= movie.ReadScriptValue("_global.isAllTutorialEnd") is true || _timer.IsTimeUp;
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
