using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public interface IAttractSoundController
{
    /// <summary>Starts a named BGM stream (the title's JINGLE_OPENING) once.</summary>
    void PlayAttractStream(string name);

    /// <summary>Whether the stream started by <see cref="PlayAttractStream"/> is still audible.</summary>
    bool IsAttractStreamPlaying { get; }

    /// <summary>keikoku's SEPlay/VOPlay: cue n of SE_ATTRACT/VO_ATTRACT (traced one to one).</summary>
    void PlayAttractCue(bool voice, int cue);
}

/// <summary>
/// The attract movies (logo_namco, title, keikoku) talk to the game through
/// ExternalInterface.call("Command", name, ...): "End" when done, the title's
/// "AssignStream"/"IsReadyStream"/"PlayStream" for JINGLE_OPENING, and keikoku's
/// "SEPlay"/"VOPlay" cue numbers (research/traces/session1.md, session3-countdown.md).
/// </summary>
public sealed class AttractHostBinding(IAttractSoundController? sounds = null) : ILumenHostBinding
{
    private string? _stream;
    private bool _streamStarted;

    /// <summary>The movie called Command("End").</summary>
    public bool Ended { get; private set; }

    /// <summary>Done: ended and, for the title, its jingle has finished (traced: the scene swaps then).</summary>
    public bool Finished => Ended && !(_streamStarted && sounds?.IsAttractStreamPlaying == true);

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The title only polls its stream when a Lumen object exists.
        context.RegisterObject("Lumen", static _ => { });
        context.RegisterExternalInterfaceCall(call =>
        {
            var arguments = call.Arguments;
            if (arguments.Length < 2 || arguments[0].Kind != LumenHostValueKind.Text
                || arguments[0].AsString() != "Command" || arguments[1].Kind != LumenHostValueKind.Text)
                return LumenHostValue.Undefined;
            var number = arguments.Length > 2 && arguments[2].Kind == LumenHostValueKind.Number
                ? (int)arguments[2].AsNumber() : -1;
            switch (arguments[1].AsString())
            {
                case "End":
                    Ended = true;
                    break;
                case "AssignStream" when arguments.Length > 2 && arguments[2].Kind == LumenHostValueKind.Text:
                    _stream = arguments[2].AsString();
                    break;
                case "IsReadyStream":
                    return LumenHostValue.FromBoolean(true);
                case "PlayStream" when _stream is not null:
                    _streamStarted = true;
                    sounds?.PlayAttractStream(_stream);
                    break;
                case "SEPlay" when number >= 0:
                    sounds?.PlayAttractCue(voice: false, number);
                    break;
                case "VOPlay" when number >= 0:
                    sounds?.PlayAttractCue(voice: true, number);
                    break;
            }
            return LumenHostValue.FromNumber(1);
        });
    }

    /// <summary>kidou (boot notice, logos) sets _global.is_end after its last screen.</summary>
    public static bool IsBootEnd(LumenPlayer player) => player.ReadScriptValue("_global.is_end") is true;
}
