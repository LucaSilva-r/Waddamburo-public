using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Don;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;

/// <summary>Maps platform input and composition roles to platform-neutral gameplay.</summary>
internal enum GameplaySoundEvent
{
    LongNoteStarted,
    BalloonPopped,
    KusudamaPopped,
    KusudamaFailed,
    FiftyCombo,
    HundredCombo,
    FailBanner,
    ClearBanner,
    FullComboBanner,
    SongFinished,
}

internal sealed class TaikoGameplayPresentation(Action<TaikoInputAction>? playHitSound = null,
    IDonPresentationController? don = null,
    Action<GameplaySoundEvent>? playEventSound = null)
{
    private TaikoJudgementSession? _session;
    private TaikoLumenPresentation? _presentation;
    private TaikoHitFlights? _flights;
    private TaikoLongNotePresentation? _longNotes;
    private TaikoDonPresentation? _character;
    private TaikoAnimationClock? _animationClock;
    private int _characterSlot;
    private int _side;
    private LumenSceneLayer[] _beatLayers = [];
    private LumenSceneLayer[] _fixedLayers = [];
    private Action<TimeSpan>? _onChartTime; // end-of-song banner check
    private Func<TaikoPlayResult>? _result;

    /// <summary>The play's numbers for the results screen (null before a song).</summary>
    public TaikoPlayResult? Result => _result?.Invoke();


    /// <param name="side">The player's drum: 0 left (P1), 1 right (P2) playing alone on the same lane.</param>
    public void Start(PlayableChart chart, LumenGameSceneInstance scene, TaikoCourse course, int side = 0)
    {
        _side = side;
        var PlayerName = TaikoGuest.Name(side); // no title (traced)
        // Skin parts are addressed by role ("bg_nomal"), whatever variant the composition picked.
        var layers = scene.Layers.Select((layer, index) =>
            (Name: GameplaySceneComposition.Role(Path.GetFileNameWithoutExtension(layer.Definition.MovieId)),
             Layer: scene.Player.Layers[index]))
            .ToDictionary(pair => pair.Name, pair => pair.Layer);
        LumenPlayer? skin(string role) => layers.TryGetValue(role, out var layer) ? layer.Player : null;
        var board = layers["lane_obi"].Player;
        var flightTemplate = layers["onp_kiseki_don_1p"];
        var flightContent = scene.Layers.Single(layer => GameplaySceneComposition.Role(
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId)) == "onp_kiseki_don_1p").Content;
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
        // Traced SetEntryType 1 / SetPlaySide 0 for the left player, 2 / 1 for the right one alone.
        board.TryInvokeCallback("SetEntryType", [LumenHostValue.FromNumber(side + 1)]);
        if (!board.TryInvokeCallback("SetPlaySide", [LumenHostValue.FromNumber(side)])
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
        var score = new TaikoScore(chart);
        var scoreAdd = layers["score_add_don_1p"].Player;
        if (!board.TryInvokeCallback("SetScore", [LumenHostValue.FromNumber(0)]))
            throw new InvalidDataException("Gameplay board is missing its score initialization callback.");
        void updateScore(long award)
        {
            if (!board.TryInvokeCallback("SetScore", [LumenHostValue.FromNumber(score.Value)]))
                throw new InvalidDataException("Gameplay board is missing SetScore.");
            if (!scoreAdd.TryInvokeCallback("Create", []))
                throw new InvalidDataException("Gameplay score-add movie is missing Create.");
            if (!scoreAdd.TryInvokeCallback("SetCount", [LumenHostValue.FromNumber(award)]))
                throw new InvalidDataException("Gameplay score-add movie is missing SetCount after Create.");
        }
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
                PlayableLongNoteKind.Kusudama => "onp_kusudama",
                _ => "onp_fusen",
            };
            var content = scene.Layers.Single(layer => Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == name).Content;
            return layers[name] with { Player = content.CreatePlayer(hostBinding: new TaikoGameplayHostBinding()) };
        }, layers["renda_num"], layers["action_fusen_1p"], _flights, don, layers["action_kusudama"],
            (kind, succeeded) =>
            {
                var sound = (kind, succeeded) switch
                {
                    (PlayableLongNoteKind.Balloon, true) => GameplaySoundEvent.BalloonPopped,
                    (PlayableLongNoteKind.Kusudama, true) => GameplaySoundEvent.KusudamaPopped,
                    (PlayableLongNoteKind.Kusudama, false) => GameplaySoundEvent.KusudamaFailed,
                    _ => (GameplaySoundEvent?)null,
                };
                if (sound is { } cue) playEventSound?.Invoke(cue);
            }, _ => playEventSound?.Invoke(GameplaySoundEvent.LongNoteStarted));
        _character = don is null ? null : new(don, layers["don3d"]);
        _animationClock = new(chart.TimingPoints);
        // ponytail: which skin parts follow the beat is carried over from the first skin
        // (backgrounds, dancers, backdrop); runners, roll characters and fever run in real time.
        _beatLayers = layers.Where(pair => pair.Key is "bg_nomal" or "dance" or "dodai"
            or "bg_fever" or "donbg").Select(pair => pair.Value).ToArray();
        _fixedLayers = layers.Values.Except(_beatLayers).ToArray();
        var skinPresentation = new TaikoSkinPresentation(new(skin("chibi"), skin("donbg"), skin("dance"),
            skin("bg_fever"), skin("fever"), skin("renda")));
        _session.LongNoteHit += progress =>
        {
            var previousScore = score.Value;
            if (score.Apply(progress, _session.CurrentTime)) updateScore(score.Value - previousScore);
            if (!progress.Note.IsBalloon) skinPresentation.OnRollHit();
        };
        var comboBonus = layers["combo_bonus_don_1p"].Player;
        // Player name board (traced call sequence; one SetChar per character).
        var nameBoard = layers["player_name"].Player;
        var characters = System.Globalization.StringInfo.GetTextElementEnumerator(PlayerName);
        var nameLength = new System.Globalization.StringInfo(PlayerName).LengthInTextElements;
        call(nameBoard, "SetPlayer", LumenHostValue.FromNumber(side));
        call(nameBoard, "SetKinotake", LumenHostValue.FromNumber(-1));
        call(nameBoard, "SetTitleName", LumenHostValue.FromString(""));
        call(nameBoard, "SetTitlePanelID", LumenHostValue.FromNumber(0));
        call(nameBoard, "SetDani", LumenHostValue.FromNumber(0), LumenHostValue.FromBoolean(false));
        call(nameBoard, "SetCover", LumenHostValue.FromBoolean(false));
        call(nameBoard, "SetNameSize", LumenHostValue.FromNumber(nameLength));
        for (var index = 0; characters.MoveNext(); index++)
            call(nameBoard, "SetChar", LumenHostValue.FromNumber(index), LumenHostValue.FromString(characters.GetTextElement()));
        call(nameBoard, "Apply");
        // Host label jumps traced in the game: gauge fire on full gauge, splash at Go-Go start, and
        // the end-of-song banner.
        var gaugeFire = layers["gage_fire_1p"].Player;
        var goGoSplash = layers["action_gogotime"].Player;
        var banner = layers["action_result"].Player;
        var full = false;
        var missed = false;
        int great = 0, good = 0, miss = 0, maxCombo = 0;
        _result = () => new TaikoPlayResult(course, score.Value, great, good, miss, maxCombo,
            score.RollHits + score.BalloonHits, gauge.FilledSegments, gauge.State != TaikoGaugeState.BelowClear);
        var bannerTime = TaikoResultBanner.Time(chart);
        var bannerShown = false;
        _onChartTime = time =>
        {
            if (bannerShown || time < bannerTime) return;
            bannerShown = true;
            var cleared = gauge.State != TaikoGaugeState.BelowClear;
            label(banner, TaikoResultBanner.Label(cleared, !missed));
            playEventSound?.Invoke(!cleared ? GameplaySoundEvent.FailBanner
                : missed ? GameplaySoundEvent.ClearBanner : GameplaySoundEvent.FullComboBanner);
            if (cleared && !missed) _character?.React("don_full_combo");
        };
        _session.Judged += judgement =>
        {
            var previousScore = score.Value;
            if (score.Apply(judgement, _session.CurrentTime)) updateScore(score.Value - previousScore);
            // Combo bonus popup at every 100 combo (the movie picks its label from the count).
            if (!judgement.StrongHitCompleted && score.Combo > 0 && score.Combo % 100 == 0)
                comboBonus.TryInvokeCallback("SetComboBonus", [LumenHostValue.FromNumber(score.Combo)]);
            if (!judgement.StrongHitCompleted && judgement.Result is TaikoHitResult.Great or TaikoHitResult.Good)
            {
                if (score.Combo == 50) playEventSound?.Invoke(GameplaySoundEvent.FiftyCombo);
                else if (score.Combo == 100) playEventSound?.Invoke(GameplaySoundEvent.HundredCombo);
            }
            var segments = gauge.FilledSegments;
            if (gauge.Apply(judgement))
            {
                if (gauge.FilledSegments != segments
                    && !gaugeMovie.TryInvokeCallback("SetCurrentGauge", [LumenHostValue.FromNumber(gauge.FilledSegments)]))
                    throw new InvalidDataException("Gameplay gauge is missing SetCurrentGauge.");
                _character?.SetGauge(gauge.State);
            }
            if (!judgement.StrongHitCompleted && judgement.Result is { } hit)
            {
                if (hit == TaikoHitResult.Great) great++;
                else if (hit == TaikoHitResult.Good) good++;
                else { miss++; missed = true; }
                maxCombo = Math.Max(maxCombo, score.Combo);
            }
            if (gauge.State == TaikoGaugeState.Full != full)
            {
                full = !full;
                label(gaugeFire, full ? "fever_start" : "fever_end");
            }
            _character?.OnJudged(judgement);
            skinPresentation.OnJudged(judgement, gauge);
        };
        // Static layers in the scene's traced depth order: behind the bar lines (2000) and notes (1002+),
        // or in front of them. Host-placed templates, Don (drawn by his presentation) and the other
        // courses' gauges are left out; so are skin movies without a traced depth.
        bool drawn(string name) => name != "don3d" && name != "lane_syousetsu" && !name.StartsWith("onp_")
            && !(name.StartsWith("gage_don_1p_") && name != gaugeName);
        var background = layers.Where(pair => drawn(pair.Key) && GameplaySceneComposition.Depth(pair.Key) > 2000).ToArray();
        var foreground = layers.Where(pair => GameplaySceneComposition.Depth(pair.Key) <= 1000
            && (drawn(pair.Key) || pair.Key == "onp_kiseki_don_1p")).ToArray();
        var flightsAt = Array.FindIndex(foreground, pair => pair.Key == "onp_kiseki_don_1p");
        // Don is drawn right after his backdrop (reference frame order).
        _characterSlot = Array.FindIndex(background, pair => pair.Key == "donbg") + 1;
        _presentation = new TaikoLumenPresentation(chart, _session,
            background.Select(pair => pair.Value),
            foreground.Where(pair => pair.Key != "onp_kiseki_don_1p").Select(pair => pair.Value),
            new Dictionary<PlayableNoteKind, LumenSceneLayer>
            {
                [PlayableNoteKind.Don] = layers["onp_don"],
                [PlayableNoteKind.Ka] = layers["onp_katsu"],
                [PlayableNoteKind.BigDon] = layers["onp_don_dai"],
                [PlayableNoteKind.BigKa] = layers["onp_katsu_dai"],
            }, layers["lane_syousetsu"], layers["lane_hit"], layers["lane_hit_effect"].Player, layers["lane_obi"].Player,
            _flights, _longNotes, flightsAt < 0 ? null : flightsAt);
        _presentation.GoGoChanged += active =>
        {
            if (active) label(goGoSplash, "splash");
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
            _character?.SetGoGo(active);
            Console.WriteLine($"Gameplay Go-Go: {(active ? "start" : "end")} at {_session.CurrentTime.TotalSeconds:F3}s.");
        };
    }

    /// <summary>A balloon/kusudama result is still animating; the song should not end under it.</summary>
    public bool OverlayActive => _longNotes?.BalloonVisible == true;

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
        _onChartTime = null;
    }

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Gameplay movie is missing callback '{name}'.");
    }

    private static void label(LumenPlayer player, string name)
    {
        if (!player.TryGotoLabel("", name))
            throw new InvalidDataException($"Gameplay movie has no '{name}' label.");
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
            // The player's own drum only: D/F/J/K on the left, Z/X/C/V on the right (ka, don, don, ka).
            TaikoInputAction? action = (_side, press.Key) switch
            {
                (0, SdlKeyboardKey.F) or (1, SdlKeyboardKey.X) => TaikoInputAction.LeftDon,
                (0, SdlKeyboardKey.J) or (1, SdlKeyboardKey.C) => TaikoInputAction.RightDon,
                (0, SdlKeyboardKey.D) or (1, SdlKeyboardKey.Z) => TaikoInputAction.LeftKa,
                (0, SdlKeyboardKey.K) or (1, SdlKeyboardKey.V) => TaikoInputAction.RightKa,
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
        _onChartTime?.Invoke(chartTime);
        _character?.SetBalloonVisible(_longNotes?.BalloonVisible == true);
    }

    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation)
    {
        var snapshot = (_presentation ?? throw new InvalidOperationException("Gameplay is not prepared."))
            .CreateSnapshot(time, interpolation, player => _beatLayers.Any(layer => layer.Player == player)
                ? _animationClock?.Interpolation ?? 1 : interpolation, _character?.Layer, _characterSlot);
        return _character?.Tint(snapshot) ?? snapshot;
    }
}
