namespace Waddamburo.Game.Lumen;

public sealed partial class SongSelectHostBinding
{
    private static readonly string[] CourseInputPriority =
        ["INPUT_DOUBLE_KATSU", "INPUT_DON", "INPUT_L_KATSU", "INPUT_R_KATSU"];
    private void bindIndependentCourseInput()
    {
        if (_sides.Length != 2) return;
        var player = requirePlayer();
        // The shared list arbitrates simultaneous hits in favour of P1. Its helper is also used
        // by the difficulty selector, where the two cursors need independent input instead.
        player.TryAfterScriptMethod("_global.CppConnection.prototype", "GetInputs", () =>
        {
            if (CourseSelectSong is null || LeavingCourseSelect
                || ChartCarousel is { } choices && !choices.IsUtility(1)) return;
            // Custom chart keys were consumed by the app; P2's native utility keys
            // still need arbitration removed, including the first rim into Settings.
            const string key = "main.cpp.key";
            if (player.ReadScriptValue(key + ".m_inputEnable.1") is not true) return;
            var buffer = player.ReadScriptValue(key + ".isAccumulation") is true ? "m_keyBuffAcm" : "m_keyBuffTrg";
            if (player.ReadScriptValue(key + "." + buffer + ".1") is not double bits) return;
            foreach (var name in CourseInputPriority)
            {
                if (player.ReadScriptValue("_global.KeyMgr." + name) is not double input || ((int)bits & (int)input) != (int)input)
                    continue;
                player.TryWriteScriptValue("main.cpp.input.1", input);
                break;
            }
        });
    }
}
