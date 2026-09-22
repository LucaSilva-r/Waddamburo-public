using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Don;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;

/// <summary>Maps platform input and composition roles to platform-neutral gameplay.</summary>
internal sealed class TaikoGameplayPresentation(Action<TaikoInputAction>? playHitSound = null,
    IDonPresentationController? don = null)
{
    private TaikoJudgementSession? _session;
    private TaikoLumenPresentation? _presentation;
    private TaikoHitFlights? _flights;
    private TaikoLongNotePresentation? _longNotes;
    private TaikoDonPresentation? _character;
    private TaikoAnimationClock? _animationClock;
    private LumenSceneLayer[] _beatLayers = [];
    private LumenSceneLayer[] _fixedLayers = [];
    private LumenPlayer[] _feverMovies = [];

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
        // The three gauge movies differ in their authored clear line (60/70/80%).
        var gaugeName = course switch
        {
            TaikoCourse.Easy => "gage_don_1p_easy",
            TaikoCourse.Normal or TaikoCourse.Hard => "gage_don_1p_normal",
            _ => "gage_don_1p_hard",
        };
        var gaugeMovie = layers[gaugeName].Player;
        if (!gaugeMovie.TryInvokeCallback("SetCurrentGauge", [LumenHostValue.FromNumber(0)]))
            throw new InvalidDataException("Gameplay gauge is missing its initialization callback.");
        var gauge = new TaikoSoulGauge(course, chart.Level, chart.NoteCount);
        foreach (var name in new[] { "onp_don", "onp_katsu", "onp_don_dai", "onp_katsu_dai" })
            if (!layers[name].Player.TryGotoLabel("", "level01"))
                throw new InvalidDataException("Gameplay note movie is missing its initial state.");
        _session = new TaikoJudgementSession(chart, new TaikoJudgementWindows(
            TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)),
            TimeSpan.FromMilliseconds(30));
        _longNotes = new TaikoLongNotePresentation(chart, _session, kind =>
        {
            var name = kind switch
            {
                PlayableLongNoteKind.Roll => "onp_renda",
                PlayableLongNoteKind.BigRoll => "onp_renda_dai",
                _ => "onp_fusen",
            };
            var content = scene.Layers.Single(layer => Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == name).Content;
            return layers[name] with { Player = content.CreatePlayer(hostBinding: new TaikoGameplayHostBinding()) };
        }, layers["renda_num"], layers["action_fusen_1p"], _flights, don);
        _character = don is null ? null : new(don, layers["don3d"]);
        _animationClock = new(chart.TimingPoints);
        _beatLayers = layers.Where(pair => pair.Key is "bg_nomal_b_32" or "dance_b_32"
            or "bg_fever_b_32" or "donbg_b_32_common").Select(pair => pair.Value).ToArray();
        _fixedLayers = layers.Values.Except(_beatLayers).ToArray();
        _feverMovies = [layers["bg_fever_b_32"].Player, layers["donbg_b_32_common"].Player];
        _session.Judged += judgement =>
        {
            var segments = gauge.FilledSegments;
            if (gauge.Apply(judgement))
            {
                if (gauge.FilledSegments != segments
                    && !gaugeMovie.TryInvokeCallback("SetCurrentGauge", [LumenHostValue.FromNumber(gauge.FilledSegments)]))
                    throw new InvalidDataException("Gameplay gauge is missing SetCurrentGauge.");
                _character?.SetGauge(gauge.State);
            }
            _character?.OnJudged(judgement);
        };
        _presentation = new TaikoLumenPresentation(chart, _session,
            layers.Where(pair => pair.Key is "bg_nomal_b_32" or "dance_b_32"
                or "bg_fever_b_32" or "donbg_b_32_common" or "lane" or "lane_hit" || pair.Key == gaugeName)
                .Select(pair => pair.Value),
            [layers["lane_hit_effect"], layers["lane_obi"]],
            new Dictionary<PlayableNoteKind, LumenSceneLayer>
            {
                [PlayableNoteKind.Don] = layers["onp_don"],
                [PlayableNoteKind.Ka] = layers["onp_katsu"],
                [PlayableNoteKind.BigDon] = layers["onp_don_dai"],
                [PlayableNoteKind.BigKa] = layers["onp_katsu_dai"],
            }, layers["lane_syousetsu"], layers["lane_hit"], layers["lane_hit_effect"].Player, layers["lane_obi"].Player,
            _flights, _longNotes);
        _presentation.GoGoChanged += active =>
        {
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
            _character?.SetGoGo(active);
            foreach (var movie in _feverMovies)
                if (!movie.TryInvokeCallback("SetFever", [LumenHostValue.FromBoolean(active)]))
                    throw new InvalidDataException("Gameplay background is missing SetFever.");
            Console.WriteLine($"Gameplay Go-Go: {(active ? "start" : "end")} at {_session.CurrentTime.TotalSeconds:F3}s.");
        };
    }

    public void Stop()
    {
        _session = null;
        _presentation = null;
        _flights = null;
        _longNotes = null;
        _character = null;
        _animationClock = null;
        _beatLayers = [];
        _fixedLayers = [];
        _feverMovies = [];
    }

    public double AdvanceAnimations(TimeSpan chartTime, LumenInputSnapshot input)
    {
        var delta = _animationClock?.Advance(chartTime) ?? 0;
        foreach (var layer in _fixedLayers) layer.Player.Advance(input);
        for (var i = 0; i < (_animationClock?.FramesToAdvance ?? 0); i++)
            foreach (var layer in _beatLayers) layer.Player.Advance(input);
        _flights?.Advance();
        _longNotes?.AdvanceAnimations();
        _character?.SetBalloonVisible(_longNotes?.BalloonVisible == true);
        // Balloon one-shots follow their authored real-time overlay.
        return _longNotes?.BalloonVisible == true ? 1 : delta;
    }

    public void ReportDiagnostics()
    {
        if (_longNotes is null) return;
        foreach (var player in _longNotes.Players)
            foreach (var diagnostic in player.Diagnostics)
                Console.Error.WriteLine($"Long-note presentation: {diagnostic.Code}: {diagnostic.Message}");
    }

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
        _character?.SetBalloonVisible(_longNotes?.BalloonVisible == true);
    }

    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation)
    {
        var snapshot = (_presentation ?? throw new InvalidOperationException("Gameplay is not prepared."))
            .CreateSnapshot(time, interpolation, player => _beatLayers.Any(layer => layer.Player == player)
                ? _animationClock?.Interpolation ?? 1 : interpolation, _character?.Layer);
        return _character?.Tint(snapshot) ?? snapshot;
    }
}
