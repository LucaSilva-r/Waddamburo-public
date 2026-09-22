using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;

/// <summary>Maps platform input and composition roles to platform-neutral gameplay.</summary>
internal sealed class TaikoGameplayPresentation(Action<TaikoInputAction>? playHitSound = null)
{
    private TaikoJudgementSession? _session;
    private TaikoLumenPresentation? _presentation;
    private TaikoHitFlights? _flights;

    public void Start(PlayableChart chart, LumenGameSceneInstance scene, TaikoCourse course)
    {
        var layers = scene.Layers.Select((layer, index) =>
            (Name: Path.GetFileNameWithoutExtension(layer.Definition.MovieId), Layer: scene.Player.Layers[index]))
            .ToDictionary(pair => pair.Name, pair => pair.Layer);
        var board = layers["lane_obi"].Player;
        var flightTemplate = layers["onp_kiseki_don_1p"];
        var flightContent = scene.Layers.Single(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "onp_kiseki_don_1p").Content;
        _flights = new TaikoHitFlights(Enumerable.Range(0, 16)
            .Select(_ => flightTemplate with { Player = flightContent.CreatePlayer() }));
        var courseLabel = course switch
        {
            TaikoCourse.Easy => "easy",
            TaikoCourse.Normal => "normal",
            TaikoCourse.Hard => "hard",
            TaikoCourse.Oni => "mania",
            TaikoCourse.Ura => "extreme",
            _ => throw new ArgumentOutOfRangeException(nameof(course)),
        };
        if (!board.TryInvokeCallback("SetPlaySide", [LumenHostValue.FromNumber(0)])
            || !board.TryInvokeCallback("SetCourse", [LumenHostValue.FromString(courseLabel)]))
            throw new InvalidDataException("Gameplay board is missing player/course initialization callbacks.");
        // Present the authored empty gauge; scoring/gauge gain rules are still separate work.
        if (!layers["gage_don_1p_normal"].Player.TryInvokeCallback("SetCurrentGauge", [LumenHostValue.FromNumber(0)]))
            throw new InvalidDataException("Gameplay gauge is missing its initialization callback.");
        foreach (var name in new[] { "onp_don", "onp_katsu", "onp_don_dai", "onp_katsu_dai" })
            if (!layers[name].Player.TryGotoLabel("", "level01"))
                throw new InvalidDataException("Gameplay note movie is missing its initial state.");
        _session = new TaikoJudgementSession(chart, new TaikoJudgementWindows(
            TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)),
            TimeSpan.FromMilliseconds(30));
        _presentation = new TaikoLumenPresentation(chart, _session,
            layers.Where(pair => pair.Key is "bg_nomal_b_32" or "dance_b_32"
                or "donbg_b_32_common" or "lane" or "gage_don_1p_normal" or "lane_hit")
                .Select(pair => pair.Value),
            [layers["lane_hit_effect"], layers["lane_obi"]],
            new Dictionary<PlayableNoteKind, LumenSceneLayer>
            {
                [PlayableNoteKind.Don] = layers["onp_don"],
                [PlayableNoteKind.Ka] = layers["onp_katsu"],
                [PlayableNoteKind.BigDon] = layers["onp_don_dai"],
                [PlayableNoteKind.BigKa] = layers["onp_katsu_dai"],
            }, layers["lane_syousetsu"], layers["lane_hit"], layers["lane_hit_effect"].Player, layers["lane_obi"].Player,
            _flights);
    }

    public void Stop()
    {
        _session = null;
        _presentation = null;
        _flights = null;
    }

    public void AdvanceAnimations() => _flights?.Advance();

    public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        if (_session is null || _presentation is null)
            return;
        foreach (var press in keyboard.Presses)
        {
            TaikoInputAction? action = press.Key switch
            {
                SdlKeyboardKey.F => TaikoInputAction.LeftDon,
                SdlKeyboardKey.J => TaikoInputAction.RightDon,
                SdlKeyboardKey.D => TaikoInputAction.LeftKa,
                SdlKeyboardKey.K => TaikoInputAction.RightKa,
                _ => null,
            };
            if (action is not { } hit)
                continue;
            var eventTime = chartTime - (keyboard.Timestamp - press.Timestamp);
            eventTime = eventTime < _session.CurrentTime ? _session.CurrentTime : eventTime;
            eventTime = eventTime > chartTime ? chartTime : eventTime;
            _presentation.Hit(hit);
            playHitSound?.Invoke(hit);
            _session.SubmitInput(hit, eventTime);
        }
        _session.AdvanceTo(chartTime);
        _presentation.Update(chartTime);
    }

    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation) =>
        (_presentation ?? throw new InvalidOperationException("Gameplay is not prepared."))
        .CreateSnapshot(time, interpolation);
}
