using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>
/// AI battle's own movies, told from <see cref="AiBattleProgress"/> (traced session-ai-battle-3): the section timer
/// (SetProgress, percent of the section), the score board (each side's 良 / 可 / 不可 / 連打 this section; side 1 is
/// AI-don's box in its plate), the backdrops' territory needle (UpdateTargetGaugeNum), the 1/3 and 2/3 cues, and at
/// each section's end the win icon, the board and timer moving on, and the backdrops' win or lose; after the last, the
/// battle's result. Also the battle's voice cues.
/// </summary>
public sealed class GameAiBattleMovies
{
    private readonly LumenPlayer? _backdrop, _hitEffect, _timer, _board, _icons, _result;
    private int _section;

    public GameAiBattleMovies(AiBattleProgress battle, LumenGameSceneInstance scene, Action<string, int>? cue)
    {
        LumenPlayer? movie(string name) => scene.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == name) is var index and >= 0 ? scene.Player.Layers[index].Player : null;
        (_backdrop, _hitEffect, _timer, _board, _icons, _result) =
            (movie("ai_enso_bg"), movie("ai_hit_effect"), movie("time"), movie("score_board"), movie("section_win_icon"), movie("battle_result"));
        // Their callbacks register on their first frame: nothing is told before.
        battle.Ready = () => !new[] { _backdrop, _hitEffect, _timer, _icons }.Any(static player => player is { CallbackNames.Length: 0 });
        battle.Initialized += sections =>
        {
            both("AIEnsoBGInitialize", number(sections));
            call(_timer, "SetSectionType", number(sections == 5 ? 1 : 0));
            call(_icons, "AIEnsoWinIconInitialize", number(sections));
        };
        battle.Started += () =>
        {
            both("StartReadyAction");
            cue?.Invoke("VO_AIBTL_ENSO", 0);
        };
        battle.Ticked += traceDons;
        battle.ProgressChanged += percent => call(_timer, "SetProgress", number(percent));
        battle.SectionCue += middle => call(_backdrop, middle ? "OnSectionMiddle" : "OnSectionClosing");
        battle.CountsChanged += (side, counts) =>
        {
            call(_board, "SetPlayerSide", number(side));
            call(_board, "UpdateValue", number(counts.Great), number(counts.Good), number(counts.Miss), number(counts.Rolls));
            both("UpdateTargetGaugeNum", number(battle.Needle));
        };
        battle.SectionEnded += (won, last) =>
        {
            call(_icons, "AIEnsoWinIconMove", LumenHostValue.FromBoolean(won));
            if (!last)
            {
                call(_board, "Reset");
                call(_timer, "ChangeNextSection");
            }
            both(won ? "StartSectionWin" : "StartSectionLose");
            // Traced: VO_AIBTL_ENSO 3 with a section won, 8 with one lost.
            cue?.Invoke("VO_AIBTL_ENSO", won ? 3 : 8);
            _section++;
            _traceTicks = 0;
        };
        battle.EmotionChanged += emotion => call(_hitEffect, "ChangeAIEmotion", number(emotion));
        battle.ResultShown += won => call(_result, "StartAction", LumenHostValue.FromBoolean(won), LumenHostValue.FromBoolean(false));
    }

    private void both(string name, params LumenHostValue[] arguments)
    {
        call(_backdrop, name, arguments);
        call(_hitEffect, name, arguments);
    }

    // Diagnostic: WADDAMBURO_AI_TRACE=1 logs each call to the battle movies, and the hit effect's Don clips for 4 s after
    // each section's end.
    private static readonly bool Trace = Environment.GetEnvironmentVariable("WADDAMBURO_AI_TRACE") == "1";
    private int _traceTicks;

    private static bool call(LumenPlayer? player, string name, params LumenHostValue[] arguments)
    {
        if (Trace && name is not ("SetProgress" or "SetPlayerSide" or "UpdateValue"))
            Console.WriteLine($"[ai] {name}({string.Join(", ", arguments.Select(static argument => argument.Kind == LumenHostValueKind.Number ? argument.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture) : argument.Kind.ToString()))})");
        return player?.TryInvokeCallback(name, arguments) == true;
    }

    private void traceDons()
    {
        if (!Trace || _hitEffect is null) return;
        _traceTicks++;
        if (_traceTicks % 4 == 0 && _section > 0 && _traceTicks <= 240)
            foreach (var line in _hitEffect.DescribeDisplayList().Where(static line => line.Contains("don", StringComparison.Ordinal)))
                Console.WriteLine($"[ai tree {_traceTicks}] {line}");
        if (_traceTicks % 4 == 0 && _section > 0 && _traceTicks <= 240)
            foreach (var quad in _hitEffect.CreateRenderSnapshot().Quads.Where(static quad => quad.NativeSurface is not null))
                Console.WriteLine($"[ai quad {_traceTicks}] {quad.NativeSurface} tl=({quad.TopLeft.X:0},{quad.TopLeft.Y:0}) br=({quad.BottomRight.X:0},{quad.BottomRight.Y:0})");
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
}
