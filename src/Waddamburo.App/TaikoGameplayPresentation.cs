using Waddamburo.Catalog;
using Waddamburo.Game;
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
    WaiwaiStart,
    SynchroCutIn,
    SoloCutIn,
}

/// <summary>
/// One or two players' gameplay lanes. Sound callbacks get the lane in two-player play (the game
/// pans each player's drum and picks per-player cues), null when one player plays alone.
/// </summary>
internal sealed class TaikoGameplayPresentation(Action<int?, TaikoInputAction>? playHitSound = null,
    IDonPresentationController? don = null,
    Action<int?, GameplaySoundEvent>? playEventSound = null)
{
    private static readonly TimeSpan GreatWindow = TimeSpan.FromMilliseconds(35);
    private Lane[] _lanes = [];
    private WaiwaiStage? _stage;
    private LumenSceneLayer[] _shared = []; // two players: drawn once, over both lanes

    /// <summary>The first lane's numbers for the results screen (null before a song).</summary>
    public TaikoPlayResult? Result => _lanes.Length == 0 ? null : _lanes[0].Result;

    /// <summary>Every lane's numbers, in lane order (the second is the right drum's player).</summary>
    public IReadOnlyList<TaikoPlayResult> Results => [.. _lanes.Select(lane => lane.Result)];

    /// <summary>
    /// One chart: one player on <paramref name="side"/> (0 left, 1 right playing alone on the same
    /// lane). Two charts: two players, the left drum on the top lane.
    /// </summary>
    public void Start(IReadOnlyList<PlayableChart> charts, LumenGameSceneInstance scene,
        IReadOnlyList<TaikoCourse> courses, int side = 0, WaiwaiComposition? waiwai = null)
    {
        var layers = scene.Layers.Select((layer, index) => (layer.Definition, Layer: scene.Player.Layers[index],
            layer.Content)).ToArray();
        if (charts.Count == 1)
        {
            _shared = [];
            _lanes = [new Lane(charts[0], layers, courses[0], side, lane: 0, players: 1, null,
                playHitSound, don, playEventSound)];
            return;
        }
        // The kusudama and song title belong to the first lane's layers; both lanes use the kusudama.
        var kusudama = new TaikoSharedKusudama(layers.Single(entry => role(entry.Definition) == "action_kusudama").Layer, 2);
        _shared = [.. layers.Where(entry => GameplaySceneComposition.SharedRoles.Contains(role(entry.Definition)))
            .Select(entry => entry.Layer)];
        // Waiwai: one voltage stage for both players (its movies are the first lane's).
        var stage = waiwai is null ? null : new WaiwaiStage(
            layers.Where(entry => entry.Definition.HostId == GameplaySceneComposition.StaticHostId)
                .ToDictionary(entry => role(entry.Definition), entry => entry.Layer.Player),
            waiwai.Timeline(charts[0]), cue => playEventSound?.Invoke(null, cue));
        _stage = stage;
        _lanes = [.. Enumerable.Range(0, 2).Select(lane => new Lane(charts[lane],
            layers.Where(entry => entry.Definition.HostId == (lane == 1
                ? GameplaySceneComposition.PlayerTwoHostId : GameplaySceneComposition.StaticHostId)).ToArray(),
            courses[lane], lane, lane, players: 2, kusudama, playHitSound, don, playEventSound, stage))];
        var handNotes = new TaikoHandNoteLink(GreatWindow);
        for (var lane = 0; lane < 2; lane++)
            handNotes.Add(_lanes[lane].Session, charts[lane].HitObjects);
    }

    private static string role(SceneLayerDefinition definition) =>
        GameplaySceneComposition.Role(Path.GetFileNameWithoutExtension(definition.MovieId));

    /// <summary>A balloon/kusudama result is still animating; the song should not end under it.</summary>
    public bool OverlayActive => _lanes.Any(lane => lane.OverlayActive);

    public void Stop()
    {
        foreach (var lane in _lanes)
            lane.Stop();
    }

    public double AdvanceAnimations(TimeSpan chartTime, LumenInputSnapshot input) =>
        _lanes.Select(lane => lane.AdvanceAnimations(chartTime, input)).DefaultIfEmpty(0).Max();

    public void ReportDiagnostics()
    {
        foreach (var lane in _lanes)
            lane.ReportDiagnostics();
    }

    public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        foreach (var lane in _lanes)
            lane.Advance(keyboard, chartTime);
        _stage?.Update(chartTime);
    }

    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation)
    {
        if (_lanes.Length == 0)
            throw new InvalidOperationException("Gameplay is not prepared.");
        // Both lanes' backs (backdrops, lanes, notes), then their fronts, then the shared overlays:
        // each lane is separate on screen, and the kusudama covers both.
        var parts = _lanes.Select(lane => lane.CreateLayers(time)).ToArray();
        var layers = parts.SelectMany(part => part.Back).Concat(parts.SelectMany(part => part.Front)).Concat(_shared);
        var snapshot = new LumenScenePlayer(1280, 720, layers).CreateRenderSnapshot(interpolation,
            player => _lanes.Select(lane => lane.BeatInterpolation(player)).FirstOrDefault(value => value is not null)
                ?? interpolation);
        foreach (var lane in _lanes)
            snapshot = lane.Tint(snapshot);
        return snapshot;
    }

    /// <summary>One player's lane: judgement, score, gauge, Don and the lane's movies.</summary>
    private sealed class Lane
    {
        private static readonly string[] NoteMovieNames = ["onp_don", "onp_katsu", "onp_don_dai",
            "onp_katsu_dai", "onp_tetunagidon_1p", "onp_tetunagikatsu_1p", "onp_synchro_don_1p", "onp_synchro_katsu_1p",
            "onp_synchro_daidon_1p", "onp_synchro_daikatsu_1p"];
        private readonly TaikoJudgementSession _session;
        private readonly TaikoLumenPresentation _presentation;
        private readonly TaikoHitFlights _flights;
        private readonly TaikoLongNotePresentation _longNotes;
        private readonly TaikoDonPresentation? _character;
        private readonly TaikoAnimationClock _animationClock;
        private readonly int _characterSlot;
        private readonly int _side;
        private readonly int? _soundLane;
        private readonly LumenSceneLayer[] _beatLayers;
        private readonly LumenSceneLayer[] _fixedLayers;
        private readonly Action<TimeSpan> _onChartTime; // end-of-song banner check
        private readonly Func<TaikoPlayResult> _result;
        private readonly Action<int?, TaikoInputAction>? _playHitSound;
        private bool _stopped;

        public TaikoPlayResult Result => _result();

        public TaikoJudgementSession Session => _session;

        /// <summary>
        /// <paramref name="lane"/> 0 = the top lane, 1 = the second player's lower lane;
        /// <paramref name="side"/> = the player's drum (keys, name, lane_obi side).
        /// </summary>
        public Lane(PlayableChart chart,
            IReadOnlyList<(SceneLayerDefinition Definition, LumenSceneLayer Layer, LumenMovieContent Content)> sceneLayers,
            TaikoCourse course, int side, int lane, int players, TaikoSharedKusudama? kusudama,
            Action<int?, TaikoInputAction>? playHitSound, IDonPresentationController? don,
            Action<int?, GameplaySoundEvent>? playEventSound, WaiwaiStage? stage = null)
        {
            _side = side;
            _soundLane = players == 2 ? lane : null;
            _playHitSound = playHitSound;
            void sound(GameplaySoundEvent cue) => playEventSound?.Invoke(_soundLane, cue);
            var PlayerName = TaikoGuest.Name(side); // no title (traced)
            // Skin parts are addressed by role ("bg_nomal"), whatever variant the composition picked.
            var layers = sceneLayers.ToDictionary(entry => role(entry.Definition), entry => entry.Layer);
            LumenMovieContent content(string name) => sceneLayers.Single(entry => role(entry.Definition) == name).Content;
            LumenPlayer? skin(string role) => layers.TryGetValue(role, out var layer) ? layer.Player : null;
            var board = layers["lane_obi"].Player;
            // Waiwai has its own flights, combo bonus, roll art and backdrop in place of the normal ones.
            var flightKey = layers.ContainsKey("onp_kiseki_don_1p") ? "onp_kiseki_don_1p" : "onp_kiseki_w_00_1p";
            var flightTemplate = layers[flightKey];
            var flightContent = content(flightKey);
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
            // Traced SetEntryType 1 / SetPlaySide 0 for the left player, 2 / 1 for the right one alone,
            // and 3 with each lane's side for two players.
            board.TryInvokeCallback("SetEntryType", [LumenHostValue.FromNumber(players == 2 ? 3 : side + 1)]);
            if (stage is not null) board.TryInvokeCallback("SetWaiwai", [LumenHostValue.FromBoolean(true)]);
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
            // Waiwai has no player gauge: both play on the shared voltage.
            var gaugeMovie = layers.GetValueOrDefault(gaugeName)?.Player;
            if (gaugeMovie?.TryInvokeCallback("SetCurrentGauge", [LumenHostValue.FromNumber(0)]) == false)
                throw new InvalidDataException("Gameplay gauge is missing its initialization callback.");
            var gauge = new TaikoSoulGauge(course, chart.Level, chart.NoteCount);
            var score = new TaikoScore(chart);
            var scoreAdd = layers.GetValueOrDefault("score_add_don_1p")?.Player; // none in Waiwai
            if (!board.TryInvokeCallback("SetScore", [LumenHostValue.FromNumber(0)]))
                throw new InvalidDataException("Gameplay board is missing its score initialization callback.");
            void updateScore(long award)
            {
                if (!board.TryInvokeCallback("SetScore", [LumenHostValue.FromNumber(score.Value)]))
                    throw new InvalidDataException("Gameplay board is missing SetScore.");
                if (scoreAdd is null) return;
                if (!scoreAdd.TryInvokeCallback("Create", []))
                    throw new InvalidDataException("Gameplay score-add movie is missing Create.");
                if (!scoreAdd.TryInvokeCallback("SetCount", [LumenHostValue.FromNumber(award)]))
                    throw new InvalidDataException("Gameplay score-add movie is missing SetCount after Create.");
            }
            foreach (var name in NoteMovieNames.Where(layers.ContainsKey))
                if (!layers[name].Player.TryGotoLabel("", "level01"))
                    throw new InvalidDataException("Gameplay note movie is missing its initial state.");
            _session = new TaikoJudgementSession(chart, new TaikoJudgementWindows(
                GreatWindow, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)),
                TimeSpan.FromMilliseconds(30), partnerHandNotes: players == 2);
            _longNotes = new TaikoLongNotePresentation(chart, _session, kind =>
            {
                var name = kind switch
                {
                    PlayableLongNoteKind.Roll => "onp_renda",
                    PlayableLongNoteKind.BigRoll => "onp_renda_dai",
                    PlayableLongNoteKind.Kusudama => "onp_kusudama",
                    _ => "onp_fusen",
                };
                return layers[name] with { Player = content(name).CreatePlayer(hostBinding: new TaikoGameplayHostBinding()) };
            }, layers["renda_num"], layers["action_fusen_1p"], _flights, don,
                kusudama is null ? layers["action_kusudama"] : null,
                (kind, succeeded) =>
                {
                    var cue = (kind, succeeded) switch
                    {
                        (PlayableLongNoteKind.Balloon, true) => GameplaySoundEvent.BalloonPopped,
                        (PlayableLongNoteKind.Kusudama, true) => GameplaySoundEvent.KusudamaPopped,
                        (PlayableLongNoteKind.Kusudama, false) => GameplaySoundEvent.KusudamaFailed,
                        _ => (GameplaySoundEvent?)null,
                    };
                    if (cue is { } value) sound(value);
                }, _ => sound(GameplaySoundEvent.LongNoteStarted), lane, kusudama, waiwai: stage is not null);
            _character = don is null ? null : new(don, layers["don3d"], lane);
            if (stage is not null) stage.StateChanged += state => _character?.SetGauge(state);
            _animationClock = new(chart.TimingPoints);
            // ponytail: which skin parts follow the beat is carried over from the first skin
            // (backgrounds, dancers, backdrop); runners, roll characters and fever run in real time.
            _beatLayers = [.. layers.Where(pair => pair.Key is "bg_nomal" or "dance" or "dodai"
                or "bg_fever" or "donbg" or "donbg_w_00").Select(pair => pair.Value)];
            // Shared movies are advanced by the first lane only.
            _fixedLayers = [.. layers.Values.Except(_beatLayers)];
            var skinPresentation = new TaikoSkinPresentation(new(skin("chibi"), skin("donbg") ?? skin("donbg_w_00"),
                skin("dance"), skin("bg_fever"), skin("fever"), skin("renda") ?? skin("renda_w_00")));
            _session.LongNoteHit += progress =>
            {
                var previousScore = score.Value;
                if (score.Apply(progress, _session.CurrentTime)) updateScore(score.Value - previousScore);
                if (!progress.Note.IsBalloon) skinPresentation.OnRollHit();
            };
            var comboBonus = (layers.GetValueOrDefault("combo_bonus_don_1p") ?? layers["sinuchi_combo_bonus_don_1p"]).Player;
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
            // Host label jumps traced in the game: gauge fire on full gauge, splash at Go-Go start
            // (one player only; two-player gameplay loads no splash), and the end-of-song banner.
            var gaugeFire = skin("gage_fire_1p"); // none in Waiwai
            var goGoSplash = skin("action_gogotime");
            var banner = skin("action_result"); // none in Waiwai
            var full = false;
            var missed = false;
            int great = 0, good = 0, miss = 0, maxCombo = 0;
            // Waiwai: both players share the voltage (clear at its norm).
            _result = () => new TaikoPlayResult(course, score.Value, great, good, miss, maxCombo,
                score.RollHits + score.BalloonHits, stage?.Segments ?? gauge.FilledSegments,
                (stage?.State ?? gauge.State) != TaikoGaugeState.BelowClear);
            var bannerTime = TaikoResultBanner.Time(chart);
            var bannerShown = false;
            _onChartTime = time =>
            {
                if (bannerShown || time < bannerTime) return;
                bannerShown = true;
                var cleared = (stage?.State ?? gauge.State) != TaikoGaugeState.BelowClear;
                if (banner is not null) label(banner, TaikoResultBanner.Label(cleared, !missed));
                sound(!cleared ? GameplaySoundEvent.FailBanner
                    : missed ? GameplaySoundEvent.ClearBanner : GameplaySoundEvent.FullComboBanner);
                if (cleared && !missed) _character?.React("don_full_combo");
            };
            _session.Judged += judgement =>
            {
                stage?.Apply(judgement);
                var previousScore = score.Value;
                if (score.Apply(judgement, _session.CurrentTime)) updateScore(score.Value - previousScore);
                // Combo bonus popup at every 100 combo (the movie picks its label from the count).
                if (!judgement.StrongHitCompleted && score.Combo > 0 && score.Combo % 100 == 0)
                    comboBonus.TryInvokeCallback("SetComboBonus", [LumenHostValue.FromNumber(score.Combo)]);
                if (!judgement.StrongHitCompleted && judgement.Result is TaikoHitResult.Great or TaikoHitResult.Good)
                {
                    if (score.Combo == 50) sound(GameplaySoundEvent.FiftyCombo);
                    else if (score.Combo == 100) sound(GameplaySoundEvent.HundredCombo);
                }
                var segments = gauge.FilledSegments;
                if (gauge.Apply(judgement) && gaugeMovie is not null)
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
                if (gaugeFire is not null && gauge.State == TaikoGaugeState.Full != full)
                {
                    full = !full;
                    label(gaugeFire, full ? "fever_start" : "fever_end");
                }
                _character?.OnJudged(judgement);
                skinPresentation.OnJudged(judgement, gauge);
            };
            // Static layers in the scene's traced depth order: behind the bar lines (2000) and notes (1002+),
            // or in front of them. Host-placed templates, Don (drawn by his presentation), the other
            // courses' gauges and the movies both players share are left out; so are skin movies
            // without a traced depth.
            bool drawn(string name) => name != "don3d" && name != "lane_syousetsu" && !name.StartsWith("onp_", StringComparison.Ordinal)
                && !(name.StartsWith("gage_don_1p_", StringComparison.Ordinal) && name != gaugeName)
                && !(players == 2 && GameplaySceneComposition.SharedRoles.Contains(name));
            var background = layers.Where(pair => drawn(pair.Key) && GameplaySceneComposition.Depth(pair.Key) > 2000).ToArray();
            var foreground = layers.Where(pair => GameplaySceneComposition.Depth(pair.Key) <= 1000
                && (drawn(pair.Key) || pair.Key == flightKey)).ToArray();
            var flightsAt = Array.FindIndex(foreground, pair => pair.Key == flightKey);
            // Don is drawn right after his backdrop (reference frame order).
            _characterSlot = Array.FindIndex(background, pair => pair.Key is "donbg" or "donbg_w_00") + 1;
            _presentation = new TaikoLumenPresentation(chart, _session,
                background.Select(pair => pair.Value),
                foreground.Where(pair => pair.Key != flightKey).Select(pair => pair.Value),
                new Dictionary<PlayableNoteKind, LumenSceneLayer>
                {
                    [PlayableNoteKind.Don] = layers["onp_don"],
                    [PlayableNoteKind.Ka] = layers["onp_katsu"],
                    [PlayableNoteKind.BigDon] = layers["onp_don_dai"],
                    [PlayableNoteKind.BigKa] = layers["onp_katsu_dai"],
                }, layers["lane_syousetsu"], layers["lane_hit"], layers["lane_hit_effect"].Player, layers["lane_obi"].Player,
                _flights, _longNotes, flightsAt < 0 ? null : flightsAt,
                layers.TryGetValue("onp_tetunagidon_1p", out var handDonLayer)
                    ? new Dictionary<PlayableNoteKind, LumenSceneLayer>
                    {
                        [PlayableNoteKind.BigDon] = handDonLayer,
                        [PlayableNoteKind.BigKa] = layers["onp_tetunagikatsu_1p"],
                    }
                    : null,
                layers.TryGetValue("onp_synchro_don_1p", out var synchroDon)
                    ? new Dictionary<PlayableNoteKind, LumenSceneLayer>
                    {
                        [PlayableNoteKind.Don] = synchroDon,
                        [PlayableNoteKind.Ka] = layers["onp_synchro_katsu_1p"],
                        [PlayableNoteKind.BigDon] = layers["onp_synchro_daidon_1p"],
                        [PlayableNoteKind.BigKa] = layers["onp_synchro_daikatsu_1p"],
                    }
                    : null);
            _presentation.GoGoChanged += active =>
            {
                if (active && goGoSplash is not null) label(goGoSplash, "splash");
                _character?.SetBalloonVisible(_longNotes.BalloonVisible);
                _character?.SetGoGo(active);
                Console.WriteLine($"Gameplay Go-Go (lane {lane}): {(active ? "start" : "end")} at {_session.CurrentTime.TotalSeconds:F3}s.");
            };
        }

        /// <summary>A balloon/kusudama result is still animating; the song should not end under it.</summary>
        public bool OverlayActive => !_stopped && _longNotes.BalloonVisible;

        public void Stop() => _stopped = true;

        public double AdvanceAnimations(TimeSpan chartTime, LumenInputSnapshot input)
        {
            if (_stopped) return 0;
            var delta = _animationClock.Advance(chartTime);
            foreach (var layer in _fixedLayers) layer.Player.Advance(input);
            for (var i = 0; i < _animationClock.FramesToAdvance; i++)
                foreach (var layer in _beatLayers) layer.Player.Advance(input);
            _flights.Advance();
            _longNotes.AdvanceAnimations();
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
            // Balloon one-shots follow their authored real-time overlay.
            return _longNotes.BalloonVisible ? 1 : delta;
        }

        public void ReportDiagnostics()
        {
            foreach (var player in _longNotes.Players)
                foreach (var diagnostic in player.Diagnostics)
                    Console.Error.WriteLine($"Long-note presentation: {diagnostic.Code}: {diagnostic.Message}");
        }

        public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
        {
            if (_stopped) return;
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
                _playHitSound?.Invoke(_soundLane, hit);
                _session.SubmitInput(hit, eventTime);
            }
            _session.AdvanceTo(chartTime);
            _presentation.Update(chartTime);
            _onChartTime(chartTime);
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
        }

        public (List<LumenSceneLayer> Back, List<LumenSceneLayer> Front) CreateLayers(TimeSpan time) =>
            _presentation.CreateLayers(time, _character?.Layer, _characterSlot);

        /// <summary>The beat clock's interpolation for this lane's beat-driven movies, else null.</summary>
        public float? BeatInterpolation(LumenPlayer player) =>
            _beatLayers.Any(layer => layer.Player == player) ? _animationClock.Interpolation : null;

        public LumenRenderSnapshot Tint(LumenRenderSnapshot snapshot) => _character?.Tint(snapshot) ?? snapshot;

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
    }
}
