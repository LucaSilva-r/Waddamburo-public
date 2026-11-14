using Waddamburo.Skins.Green.Gameplay;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game;
using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Skins.Green;
using Waddamburo.Game.Scenes;
using Waddamburo.Skins.Green.Patching;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>Maps platform input and composition roles to platform-neutral gameplay.</summary>
internal enum GameplaySoundEvent
{
    LongNoteStarted,
    BalloonPopped,
    KusudamaPopped,
    KusudamaFailed,
    /// <summary>Don-chan's combo voice (50, then every 100 to 5000).</summary>
    Combo,
    FailBanner,
    ClearBanner,
    FullComboBanner,
    SongFinished,
    WaiwaiStart,
    RareHit,
    SynchroCutIn,
    SoloCutIn,
    /// <summary>A big note hit (no sound: controllers rumble, #35).</summary>
    BigHit,
    /// <summary>A small don hit (no sound: controllers rumble).</summary>
    DonHit,
    /// <summary>A small ka hit (no sound: controllers rumble).</summary>
    KaHit,
    /// <summary>A don on a roll, balloon or kusudama (no sound: controllers rumble).</summary>
    LongDonHit,
    /// <summary>A ka on a roll (no sound: controllers rumble).</summary>
    LongKaHit,
    /// <summary>A hit on a big roll (no sound: controllers rumble).</summary>
    BigLongHit,
}

/// <summary>
/// One or two players' gameplay lanes. Sound callbacks get the lane in two-player play (the game
/// pans each player's drum and picks per-player cues), null when one player plays alone.
/// </summary>
internal sealed class TaikoGameplayPresentation(Action<int?, TaikoInputAction>? playHitSound = null,
    IDonPresentationController? don = null,
    Action<int?, GameplaySoundEvent, int>? playEventSound = null, Func<TimeSpan>? drumDebounce = null)
{
    private Lane[] _lanes = [];
    private WaiwaiStage? _stage;
    private LumenSceneLayer[] _shared = []; // two players: drawn once, over both lanes
    // What the flow knows and the lanes do not (the song's title, the players' names): kept, and given to the lanes
    // each time they are built.
    private readonly Dictionary<(int? Lane, string Name), LumenHostValue> _provided = [];

    /// <summary>Publishes a provider state (WdNames) to the movies of one lane, or of every lane.</summary>
    public void Publish(string name, LumenHostValue value, int? lane = null)
    {
        _provided[(lane, name)] = value;
        for (var index = 0; index < _lanes.Length; index++)
            if (lane is null || lane == index)
                _lanes[index].Publish(name, value);
    }

    /// <summary>The song's clear / fail banner shows at its end (off in practice; set before <see cref="Start"/>).</summary>
    /// <remarks>Read each frame; lanes rebuilt (a seek back in a review) start without a banner shown.</remarks>
    public bool EndBanner
    {
        get => _endBanner;
        set { _endBanner = value; foreach (var lane in _lanes) lane.EndBanner = value; }
    }
    private bool _endBanner = true;

    /// <summary>Each lane's play options (set before <see cref="Start"/>; none when missing).</summary>
    public IReadOnlyList<TaikoPlayOptions> Options { get; set; } = [];

    /// <summary>
    /// Online play (set before <see cref="Start"/>): the second lane is the other player's, driven by their hits through this
    /// feed and running its delay behind; the lanes don't share hand notes, so each ends as its player's own game did.
    /// </summary>
    public RemoteLaneFeed? RemoteLane { get; set; }

    /// <summary>Online play: which of the two lanes is the other player's (the drum they'd stand at).</summary>
    public int RemoteLaneIndex { get; set; } = 1;

    /// <summary>Online play: this player's hits go to the other player through it.</summary>
    public HitRelay? Relay { get; set; }

    /// <summary>The dan course being played (null: a normal song): its gauge and requirements panel show.</summary>
    public Waddamburo.Game.Dan.DanCourse? Dan { get; set; }

    /// <summary>A challenge course's name art for dani_enso's fill challenge_lv_mini.</summary>
    public LumenNativeSurfaceKey? DanChallengeArt { get; set; }

    /// <summary>The first lane's numbers for the results screen (null before a song).</summary>
    public TaikoPlayResult? Result => _lanes.Length == 0 ? null : _lanes[0].Result;

    /// <summary>The Waiwai results' numbers (null outside Waiwai).</summary>
    public WaiwaiOutcome? WaiwaiOutcome => _stage?.Outcome;

    /// <summary>Every lane's numbers, in lane order (the second is the right drum's player).</summary>
    public IReadOnlyList<TaikoPlayResult> Results => [.. _lanes.Select(lane => lane.Result)];

    /// <summary>Calls <paramref name="handler"/> with each of a lane's judgements in this play.</summary>
    public void OnJudged(int lane, Action<TaikoNoteJudgement> handler) => _lanes[lane].Judged += handler;

    public void FollowBranch(int lane, int leader) => _lanes[lane].FollowBranch(_lanes[leader]);

    /// <summary>Every lane's drum inputs, in lane order.</summary>
    public IReadOnlyList<TaikoReplay> Replays => [.. _lanes.Select(lane => lane.Replay)];

    /// <summary>
    /// One chart: one player on <paramref name="side"/> (0 left, 1 right playing alone on the same
    /// lane). Two charts: two players, the left drum on the top lane.
    /// </summary>
    /// <remarks><paramref name="reviews"/>: a reviewed play's baked timeline per lane (the lanes then show it, not live input).</remarks>
    public void Start(IReadOnlyList<PlayableChart> charts, LumenGameSceneInstance scene,
        IReadOnlyList<TaikoCourse> courses, int side = 0, WaiwaiComposition? waiwai = null,
        IReadOnlyList<ReviewTimeline>? reviews = null)
    {
        start(charts, scene, courses, side, waiwai, reviews);
        // Lanes are built one after the other: what each published while being built reaches every movie here.
        var everyone = scene.Player.Layers.Select(static layer => layer.Player).ToArray();
        foreach (var built in _lanes)
        {
            built.Everyone = everyone;
            foreach (var (name, value) in built.Published)
                foreach (var movie in everyone)
                    if (name.IndexOf('#') is var at and >= 0) movie.PublishItem(name[..at], int.Parse(name.AsSpan(at + 1)), value, built.Drum);
                    else movie.Publish(name, value, built.Drum);
        }
        foreach (var ((lane, name), value) in _provided)
            for (var index = 0; index < _lanes.Length; index++)
                if (lane is null || lane == index)
                    _lanes[index].Publish(name, value);
    }

    private void start(IReadOnlyList<PlayableChart> charts, LumenGameSceneInstance scene,
        IReadOnlyList<TaikoCourse> courses, int side, WaiwaiComposition? waiwai,
        IReadOnlyList<ReviewTimeline>? reviews)
    {
        var layers = scene.Layers.Select((layer, index) => (layer.Definition, Layer: scene.Player.Layers[index],
            layer.Content)).ToArray();
        _stage = null;
        if (charts.Count == 1)
        {
            _shared = [];
            _lanes = [new Lane(charts[0], layers, courses[0], side, lane: 0, players: 1, null,
                playHitSound, don, playEventSound, review: reviews?[0], options: Options.ElementAtOrDefault(0), relay: Relay, dan: Dan, danArt: DanChallengeArt, endBanner: EndBanner)];
            return;
        }
        // The kusudama and song title belong to the first lane's layers; both lanes use the kusudama. Online only this
        // player's lane has kusudamas (the other's are balloons), so it's theirs alone.
        var kusudama = new TaikoSharedKusudama(layers.Single(entry => role(entry.Definition) == "action_kusudama").Layer,
            RemoteLane is null ? 2 : 1);
        _shared = [.. layers.Where(entry => GameplaySceneComposition.SharedRoles.Contains(role(entry.Definition)))
            .Select(entry => entry.Layer)];
        // Waiwai: one voltage stage for both players (its movies are the first lane's).
        var stage = waiwai is null ? null : new WaiwaiStage(
            layers.Where(entry => entry.Definition.HostId == GameplaySceneComposition.StaticHostId)
                .ToDictionary(entry => role(entry.Definition), entry => entry.Layer.Player),
            waiwai.Timeline(charts[0]), cue => playEventSound?.Invoke(null, cue, 0));
        _stage = stage;
        var aiStage = layers.FirstOrDefault(entry => role(entry.Definition) == "ai_hit_effect").Layer;
        _lanes = [.. Enumerable.Range(0, 2).Select(lane => new Lane(charts[lane],
            layers.Where(entry => entry.Definition.HostId == (lane == 1
                ? GameplaySceneComposition.PlayerTwoHostId : GameplaySceneComposition.StaticHostId)).ToArray(),
            courses[lane], lane, lane, players: 2, kusudama,
            // The other player's drum isn't heard: only this player's own drum sounds under their hands (AI-don's
            // neither, user-asked).
            lane == RemoteLaneIndex && RemoteLane is not null || lane == 1 && aiStage is not null ? null : playHitSound,
            don, playEventSound, stage, reviews?[lane],
            Options.ElementAtOrDefault(lane), EndBanner, remote: lane == RemoteLaneIndex ? RemoteLane : null,
            relay: lane != RemoteLaneIndex ? Relay : null,
            donStage: aiStage))];
        // AI battle: AI-don's roll counter shows the player's count (its rolls copy theirs, user-asked).
        if (aiStage is not null)
            _lanes[1].LongNotes.ShownHits = index => _lanes[0].Session.GetLongNoteProgress(index).Hits;
        // Online, each lane has its own notes: nothing links hand notes across the network.
        if (RemoteLane is not null || aiStage is not null)
            return;
        var handNotes = new TaikoHandNoteLink();
        for (var lane = 0; lane < 2; lane++)
            handNotes.Add(_lanes[lane].Session, charts[lane].HitObjects);
    }

    private static string role(SceneLayerDefinition definition) =>
        GameplaySceneComposition.Role(Path.GetFileNameWithoutExtension(definition.MovieId));

    /// <summary>Updates one lane's drawing effects while its practice session carries on.</summary>
    public void ReplaceEffects(int lane, ChartEffects effects)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lane, _lanes.Length);
        _lanes[lane].ReplaceEffects(effects);
    }

    /// <remarks><paramref name="allLanes"/>: both lanes' elements, the second lane's named *_p2 (a two-player layout).</remarks>
    public IReadOnlyList<StageEffectElement> EffectElements(TimeSpan time, bool allLanes = false) =>
        _lanes.Length == 1 ? _lanes[0].EffectElements(time)
        : !allLanes ? []
        : [.. _lanes.SelectMany((lane, index) => lane.EffectElements(time).Select(element => index == 0 || element.Target == "camera"
            ? element : element with { Target = element.Target + Waddamburo.Catalog.ChartMods.SecondLane }))];

    /// <summary>A balloon/kusudama result is still animating; the song should not end under it.</summary>
    public bool OverlayActive => _lanes.Any(lane => lane.OverlayActive);

    /// <summary>A host-drawn movie the layout places as <paramref name="target"/> (the first lane's).</summary>
    public LumenSceneLayer PlaceOverlay(LumenSceneLayer layer, string target)
    {
        if (_lanes.Length == 0) return layer;
        _lanes[0].Presentation.SetOverlay(target, layer);
        return _lanes[0].Presentation.PlaceOverlay(layer, target, _lanes[0].Session.CurrentTime);
    }

    /// <summary>The layout editor's "Show hidden": parts that only show now and then are kept on screen.</summary>
    public void ShowHiddenElements()
    {
        foreach (var lane in _lanes) lane.Presentation.ShowCombo();
    }

    /// <summary>A dan course's requirement can no longer be met: the course ends with the song being played.</summary>
    public bool DanFailed => _lanes.Any(static lane => lane.DanFailed);

    /// <summary>A dan course's whole play.</summary>
    public Waddamburo.Game.Dan.DanProgress DanTotal => _lanes[0].DanTotal;

    /// <summary>Each of a course's songs' own counts; songs not reached have none.</summary>
    public IReadOnlyList<Waddamburo.Game.Dan.DanSongCounts> DanSongs(int songs) => _lanes[0].DanSongs(songs);

    /// <summary>The play at each finished song's end.</summary>
    public IReadOnlyList<Waddamburo.Game.Dan.DanProgress> DanSongEnds => _lanes[0].DanSongEnds;

    /// <summary>Brings the end banner to <paramref name="time"/>, for a course ending early.</summary>
    public void EndCourseAt(TimeSpan time)
    {
        foreach (var lane in _lanes)
            lane.EndsAt = time;
    }

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

    /// <summary>Takes a note off every lane with no judgement (the audio calibration's hit notes).</summary>
    public void RemoveNote(int index)
    {
        foreach (var lane in _lanes)
            lane.RemoveNote(index);
    }

    /// <summary>Every lane's target hit ring, with no judgement (the audio calibration's beat flash).</summary>
    public void FlashTarget()
    {
        foreach (var lane in _lanes)
            lane.FlashTarget();
    }

    /// <summary>Before a reviewed play's lanes are rebuilt: their open roll, balloon or kusudama overlays close.</summary>
    public void CloseLongNotes()
    {
        foreach (var lane in _lanes)
            lane.CloseLongNotes();
    }

    /// <summary>After rebuilding a reviewed play's lanes: Don back in his plain pose (a balloon may have left him stretched).</summary>
    public void ResetCharacters()
    {
        foreach (var lane in _lanes)
            lane.ResetCharacter();
    }

    /// <summary>Fresh live lanes start judging at <paramref name="time"/>: the notes before it are passed by (a training attempt).</summary>
    public void StartAt(TimeSpan time)
    {
        foreach (var lane in _lanes)
            lane.Session.SkipTo(time);
    }

    /// <summary>A reviewed play's lanes at <paramref name="time"/>: played on to it, or jumped there (<see cref="Lane.Review"/>).</summary>
    public void Review(TimeSpan time, bool play)
    {
        foreach (var lane in _lanes)
            lane.Review(time, play);
    }

    /// <param name="keyboard">The frame's input, its presses with their timestamps.</param>
    /// <param name="chartTime">The chart time the lanes are at now.</param>
    /// <param name="clockSpeed">Chart time per real second (training's speed): a hit's time within the frame is scaled to chart time.</param>
    /// <param name="aiInput">AI battle's generated presses, after the player's inputs have been accepted.</param>
    public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime, double clockSpeed = 1,
        Func<SdlKeyboardSnapshot, TimeSpan, SdlKeyboardSnapshot>? aiInput = null)
    {
        var debounce = drumDebounce?.Invoke() ?? TimeSpan.Zero;
        for (var index = 0; index < _lanes.Length; index++)
            _lanes[index].Advance(index == 1 && aiInput is not null ? aiInput(keyboard, chartTime) : keyboard,
                chartTime, index == 1 && aiInput is not null ? TimeSpan.Zero : debounce, clockSpeed);
        _stage?.Update(chartTime);
    }

    /// <summary>A course's judgement windows (osu! charts are judged as Oni).</summary>
    public static TaikoJudgementWindows WindowsFor(PlayableChart chart, TaikoCourse course) =>
        TaikoJudgementWindows.ForCourse(chart.Key.Song.Source.IsOsu() ? TaikoCourse.Oni : course);


    /// <summary>Each lane's latest judged hit, early or late, for the timing glow.</summary>
    public IEnumerable<TimingMark> TimingMarks => _lanes.Select(static lane => lane.LastTiming).OfType<TimingMark>();

    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation)
    {
        if (_lanes.Length == 0)
            throw new InvalidOperationException("Gameplay is not prepared.");
        // Both lanes' backs (backdrops, lanes, notes), then their fronts, then the shared overlays:
        // each lane is separate on screen, and the kusudama covers both.
        var parts = _lanes.Select(lane => lane.CreateLayers(time)).ToArray();
        var layers = parts.SelectMany(part => part.Back).Concat(parts.SelectMany(part => part.Front)).Concat(_shared);
        var snapshot = new LumenScenePlayer(1280, 720, layers).CreateRenderSnapshot(interpolation, player =>
        {
            foreach (var lane in _lanes)
                if (lane.BeatInterpolation(player) is { } beat)
                    return beat;
            return interpolation;
        });
        foreach (var lane in _lanes)
            snapshot = lane.Tint(snapshot);
        return snapshot;
    }

    /// <summary>One player's lane: judgement, score, gauge, Don and the lane's movies.</summary>
    private sealed class Lane
    {
        /// <summary>The end banner shows when the song reaches it (read each frame: the editor turns it off mid-song).</summary>
        public bool EndBanner { get; set; }

        private static readonly string[] NoteMovieNames = ["onp_don", "onp_katsu", "onp_don_dai",
            "onp_katsu_dai", "onp_tetunagidon_1p", "onp_tetunagikatsu_1p", "onp_synchro_don_1p", "onp_synchro_katsu_1p",
            "onp_synchro_daidon_1p", "onp_synchro_daikatsu_1p", "onp_rare_0_w_00", "onp_kadon"];
        private static readonly string[] NoteTextLabels = ["don", "do", "ko", "katsu", "ka", "don_dai", "katsu_dai",
            "geki_renda", "imo"];
        private readonly TaikoJudgementSession _session;
        // What the presentation reads: the live session, or a reviewed play's baked timeline at its playhead.
        private readonly ITaikoJudgementSource _source;
        private readonly ReviewJudgementSource? _review;
        private readonly Action<ReviewState?> _restore = static _ => { };
        private readonly TaikoLumenPresentation _presentation;
        public TaikoLumenPresentation Presentation => _presentation;
        public TaikoJudgementSession Session => _session;
        private readonly TaikoHitFlights _flights;
        private readonly TaikoLongNotePresentation _longNotes;
        private readonly Action<string, LumenHostValue> _publish;
        private readonly Action<string, LumenHostValue[]> _emit;
        private long _wholeBeat = long.MinValue; // the last beat.passed told

        /// <summary>This drum's player number for the provider: 1 the left drum, 2 the right.</summary>
        public int Drum { get; }

        /// <summary>What this lane has published so far.</summary>
        public Dictionary<string, LumenHostValue> Published { get; } = new(StringComparer.Ordinal);

        /// <summary>Every movie on stage, told this drum's values under its player number.</summary>
        public LumenPlayer[] Everyone { get; set; } = [];

        public void Publish(string name, LumenHostValue value) => _publish(name, value);

        public TaikoLongNotePresentation LongNotes => _longNotes;
        private readonly TaikoDonPresentation? _character;
        private readonly TaikoAnimationClock _animationClock;
        private readonly LiveLaneSource? _live; // an online partner's lane
        private readonly HitRelay? _relay;

        /// <summary>An online partner's banner waits for their play to catch up, this long at most.</summary>
        private static readonly TimeSpan PartnerGrace = TimeSpan.FromSeconds(3);

        private static TimeSpan shown(TimeSpan time) => time;
        private readonly int _characterSlot;
        private readonly int _side;
        private readonly int? _soundLane;
        private readonly LumenSceneLayer[] _beatLayers;
        private readonly LumenPlayer? _dancers;
        private readonly LumenPlayer[] _noteFaces;
        private readonly GameplayGameMovies _gameMovies;
        private readonly LumenSceneLayer[] _fixedLayers;
        private readonly Action<TimeSpan> _onChartTime; // end-of-song banner check
        private readonly Action<bool> _setMissed = static _ => { }; // a reviewed play's full combo, from its bake
        private readonly Func<TaikoPlayResult> _result;
        private readonly Action<int?, TaikoInputAction>? _playHitSound;
        private bool _stopped;

        public TaikoPlayResult Result => _result();

        /// <summary>A dan course's requirement can no longer be met.</summary>
        public bool DanFailed { get; private set; }

        /// <summary>A dan course's play so far.</summary>
        public Waddamburo.Game.Dan.DanProgress DanTotal { get; private set; }

        /// <summary>The play at each finished song's end.</summary>
        public IReadOnlyList<Waddamburo.Game.Dan.DanProgress> DanSongEnds => _danSongEnds;

        private readonly List<Waddamburo.Game.Dan.DanProgress> _danSongEnds = [];

        /// <summary>Each song's own counts, the song being played last, for <paramref name="songs"/> songs.</summary>
        public List<Waddamburo.Game.Dan.DanSongCounts> DanSongs(int songs)
        {
            var counts = new List<Waddamburo.Game.Dan.DanSongCounts>();
            var before = default(Waddamburo.Game.Dan.DanProgress);
            for (var index = 0; index < songs; index++)
            {
                var end = index < _danSongEnds.Count ? _danSongEnds[index] : index == _danSongEnds.Count ? DanTotal : before;
                counts.Add(new(end.Goods - before.Goods, end.Oks - before.Oks, end.Bads - before.Bads, end.Rolls - before.Rolls));
                before = end;
            }
            return counts;
        }

        /// <summary>The end banner's time, when a course ends before its last song.</summary>
        public TimeSpan? EndsAt { get; set; }


        public TaikoReplay Replay { get; } = new();

        public TimingMark? LastTiming { get; private set; }

        /// <summary>Each of the lane's judgements (AI battle's AI-don learns the player's timing from them).</summary>
        public event Action<TaikoNoteJudgement>? Judged;

        /// <summary>
        /// <paramref name="lane"/> 0 = the top lane, 1 = the second player's lower lane;
        /// <paramref name="side"/> = the player's drum (keys, name, lane_obi side).
        /// </summary>
        public Lane(PlayableChart chart,
            IReadOnlyList<(SceneLayerDefinition Definition, LumenSceneLayer Layer, LumenMovieContent Content)> sceneLayers,
            TaikoCourse course, int side, int lane, int players, TaikoSharedKusudama? kusudama,
            Action<int?, TaikoInputAction>? playHitSound, IDonPresentationController? don,
            Action<int?, GameplaySoundEvent, int>? playEventSound, WaiwaiStage? stage = null, ReviewTimeline? review = null,
            TaikoPlayOptions options = default, bool endBanner = true, Waddamburo.Game.Dan.DanCourse? dan = null, LumenNativeSurfaceKey? danArt = null,
            RemoteLaneFeed? remote = null, HitRelay? relay = null, LumenSceneLayer? donStage = null)
        {
            _relay = relay;
            _side = side;
            _soundLane = players == 2 ? lane : null;
            _playHitSound = playHitSound;
            void sound(GameplaySoundEvent cue, int combo = 0) => playEventSound?.Invoke(_soundLane, cue, combo);
            // Skin parts are addressed by role ("bg_nomal"), whatever variant the composition picked.
            var layers = sceneLayers.ToDictionary(entry => role(entry.Definition), entry => entry.Layer);
            LumenMovieContent content(string name) => sceneLayers.Single(entry => role(entry.Definition) == name).Content;
            // OpenTaiko's Kadon (TJA G) has no art in the game's files: the big ka drawn from its purple copies
            // (KadonNotePatch), a player of its own so its faces and bounce follow the notes'.
            if (chart.HitObjects.Any(static note => note.IsKadon) && content("onp_katsu_dai") is { } katsu
                && KadonNotePatch.AppliesTo(katsu) && KadonNotePatch.CopiesAt(katsu) is var copies and > 0)
            {
                var big = layers["onp_katsu_dai"];
                layers["onp_kadon"] = big with
                {
                    Player = katsu.CreatePlayer(),
                    TextureOffset = big.TextureOffset + (uint)copies,
                    TextureCount = (uint)copies,
                };
            }
            LumenPlayer? skin(string role) => layers.TryGetValue(role, out var layer) ? layer.Player : null;
            var board = layers["lane_obi"].Player;
            // Waiwai has its own flights, combo bonus, roll art and backdrop in place of the normal ones.
            var flightKey = layers.ContainsKey("onp_kiseki_don_1p") ? "onp_kiseki_don_1p" : "onp_kiseki_w_00_1p";
            var flightTemplate = layers[flightKey];
            var flightContent = content(flightKey);
            // Kadon flights: the flight movie on its purple copies (KadonNotePatch), when the chart has Kadons.
            var kadonFlights = chart.HitObjects.Any(static note => note.IsKadon) && KadonNotePatch.AppliesTo(flightContent)
                && KadonNotePatch.CopiesAt(flightContent) is var flightCopies and > 0
                ? Enumerable.Range(0, 4).Select(_ => flightTemplate with
                {
                    Player = flightContent.CreatePlayer(),
                    TextureOffset = flightTemplate.TextureOffset + (uint)flightCopies,
                    TextureCount = (uint)flightCopies,
                })
                : null;
            _flights = new TaikoHitFlights(Enumerable.Range(0, 16)
                .Select(_ => flightTemplate with { Player = flightContent.CreatePlayer() }), kadonFlights, flightTemplate);
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
            // The game's board only: ours reads the same from the provider (player.side, play.course, ...).
            if (!board.UsesProvider)
            {
                board.TryInvokeCallback("SetEntryType", [LumenHostValue.FromNumber(players == 2 ? 3 : side + 1)]);
                // Waiwai's lanes, and AI battle's AI-don lane (traced SetWaiwai(true) on its bar only).
                if (stage is not null || donStage is not null && lane == 1) board.TryInvokeCallback("SetWaiwai", [LumenHostValue.FromBoolean(true)]);
                if (!board.TryInvokeCallback("SetPlaySide", [LumenHostValue.FromNumber(side)])
                    || !board.TryInvokeCallback("SetCourse", [LumenHostValue.FromString(dan is null ? courseLabel : "dani")]))
                    throw new InvalidDataException("Gameplay board is missing player/course initialization callbacks.");
                // The three gauge movies differ in their authored clear line (60/70/80%).
                if (dan is not null)
                    board.TryInvokeCallback("SetDani", []);
            }
            // osu! charts fill as Oni (TaikoSoulGauge), so they show Oni's clear line.
            var gaugeName = dan is not null ? "gage_don_1p_dani" : (chart.Key.Song.Source.IsOsu() ? TaikoCourse.Oni : course) switch
            {
                TaikoCourse.Easy => "gage_don_1p_easy",
                TaikoCourse.Normal or TaikoCourse.Hard => "gage_don_1p_normal",
                _ => "gage_don_1p_hard",
            };
            // Waiwai has no player gauge: both play on the shared voltage.
            var gaugeMovie = layers.GetValueOrDefault(gaugeName)?.Player;
            var gauge = !chart.Segments.IsEmpty ? new TaikoSoulGauge(chart.Segments, dan?.ExactGauge == true) : new TaikoSoulGauge(course, chart);
            var score = new TaikoScore(chart, WindowsFor(chart, course).Good);
            var scoreAdd = layers.GetValueOrDefault("score_add_don_1p")?.Player; // none in Waiwai
            var comboBonus = (layers.GetValueOrDefault("combo_bonus_don_1p") ?? layers["sinuchi_combo_bonus_don_1p"]).Player;
            // Host label jumps traced in the game: gauge fire on full gauge, splash at Go-Go start
            // (one player only; two-player gameplay loads no splash), and the end-of-song banner.
            var gaugeFire = skin("gage_fire_1p"); // none in Waiwai
            var goGoSplash = skin("action_gogotime");
            var banner = skin("action_result"); // none in Waiwai
            // The game's movies are driven from what is published, by their adapter (ours read it themselves).
            var gameMovies = new GameplayGameMovies(board, gaugeMovie, () => gauge.FilledSegments, scoreAdd, comboBonus,
                gaugeFire, goGoSplash, banner, layers["lane_hit"].Player, layers.GetValueOrDefault("fukidashi_level_1p")?.Player,
                layers.GetValueOrDefault("dani_enso")?.Player, layers["renda_num"].Player, layers["action_fusen_1p"].Player,
                (kusudama?.Layer ?? layers.GetValueOrDefault("action_kusudama"))?.Player);
            // The provider (movie-provider-design.md): this drum's numbers under their names, for any movie of the lane
            // that asks (Wd.get, Wd.on). The game's movies never ask and are driven by their callbacks as before.
            // This drum's movies get them as their own. The movies both drums share (song_info) belong to neither; they,
            // and every other movie on stage, get them under the player's number (Everyone, set once all lanes exist).
            Drum = (players == 2 ? lane : side) + 1;
            var mine = layers.Where(pair => !(players == 2 && GameplaySceneComposition.SharedRoles.Contains(pair.Key)))
                .Select(static pair => pair.Value.Player).ToArray();
            var published = Published;
            void publish(string name, LumenHostValue value)
            {
                if (published.TryGetValue(name, out var before) && before == value)
                    return; // most ticks nothing changed: not worth a walk over the movies
                published[name] = value;
                foreach (var movie in mine) movie.Publish(name, value);
                foreach (var movie in Everyone) movie.Publish(name, value, Drum);
                gameMovies.State(name, value);
            }
            // A place of a list (WdKind.List), kept in Published under "name#index".
            void publishItem(string name, int index, LumenHostValue value)
            {
                var key = $"{name}#{index}";
                if (published.TryGetValue(key, out var before) && before == value)
                    return;
                published[key] = value;
                foreach (var movie in mine) movie.PublishItem(name, index, value);
                foreach (var movie in Everyone) movie.PublishItem(name, index, value, Drum);
                gameMovies.Item(name, index, value);
            }
            void emit(string name, params ReadOnlySpan<LumenHostValue> arguments)
            {
                foreach (var movie in mine) movie.Emit(name, arguments);
                foreach (var movie in Everyone) movie.EmitFor(Drum, name, arguments);
                gameMovies.Event(name, arguments);
            }
            _emit = (name, arguments) => emit(name, arguments);
            publish("play.score", LumenHostValue.FromNumber(0));
            publish("play.gauge", LumenHostValue.FromNumber(0));
            void updateScore(long award, bool popup = true)
            {
                publish("play.score", LumenHostValue.FromNumber(score.Value));
                if (popup) emit("score.added", LumenHostValue.FromNumber(award));
            }
            _noteFaces = [.. NoteMovieNames.Where(layers.ContainsKey).Select(name => layers[name].Player)];
            GameplayGameMovies.StartNotes(_noteFaces);
            _gameMovies = gameMovies;
            // osu! charts sit in course slots by star order only: all play with Oni's windows.
            // Online lanes play alone, as each player's own game plays them.
            _session = new TaikoJudgementSession(chart, WindowsFor(chart, course),
                partnerHandNotes: players == 2 && remote is null && relay is null && donStage is null);
            if (review is null && remote is not null)
            {
                // Online: the lane shows in time with this player's song; its numbers are the shadow's (their own play).
                _live = new LiveLaneSource(chart, _session, remote);
                _source = _live;
            }
            else if (review is null)
                _source = _session;
            else
            {
                _review = new ReviewJudgementSource(review);
                _source = _review;
                // Played on, a review's drum hits sound and animate as they did.
                _review.Hit += hit =>
                {
                    _presentation!.Hit(hit);
                    _playHitSound?.Invoke(_soundLane, hit);
                };
            }
            _longNotes = new TaikoLongNotePresentation(chart, _source, kind =>
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
                }, _ => sound(GameplaySoundEvent.LongNoteStarted), lane, kusudama,
                createText: kind =>
                {
                    var name = kind == PlayableLongNoteKind.BigRoll ? "onp_renda_dai_moji" : "onp_renda_moji";
                    return layers[name] with { Player = content(name).CreatePlayer(hostBinding: new TaikoGameplayHostBinding()) };
                }) { KeepCamera = donStage is not null && !layers.ContainsKey("don3d") };
            // The roll, balloon and kusudama as they go: their counts at once (and again each tick, for the shared
            // kusudama the other drum counts down), their starts and ends as events.
            void publishLongNotes()
            {
                var under = _longNotes.Active;
                publish("play.roll", under is { Balloon: false } roll ? LumenHostValue.FromNumber(roll.Count) : LumenHostValue.Undefined);
                publish("play.kusudama", under is { Kusudama: true } ball ? LumenHostValue.FromNumber(ball.Count) : LumenHostValue.Undefined);
                publish("play.balloon", under is { Balloon: true, Kusudama: false } balloon ? LumenHostValue.FromNumber(balloon.Count) : LumenHostValue.Undefined);
            }
            _longNotes.Changed += publishLongNotes;
            _longNotes.Started += (kind, hits, goGo) => emit(kind == PlayableLongNoteKind.Kusudama ? "kusudama.started" : "balloon.started",
                LumenHostValue.FromNumber(hits), LumenHostValue.FromBoolean(goGo));
            _longNotes.Ended += (kind, popped) => emit(kind == PlayableLongNoteKind.Kusudama ? "kusudama.ended" : "balloon.ended",
                LumenHostValue.FromBoolean(popped));
            // AI battle: both Dons stand in its hit-effect movie (no don3d of their own).
            _character = don is null ? null : donStage is not null && !layers.ContainsKey("don3d")
                ? new(don, donStage, lane, stage: true) : new(don, layers["don3d"], lane);
            if (stage is not null) stage.StateChanged += state => _character?.SetGauge(state);
            _animationClock = new(chart.TimingPoints);
            // Traced (session21): the game steps the dancers, the chibi runners, the fever crowd and
            // the notes by the tempo; the backgrounds (normal and Go-Go), stage, Don-chan's backdrop,
            // rolls' characters and the lane run in real time.
            _beatLayers = [.. layers.Where(pair => pair.Key is "dance" or "fever" or "chibi"
                || NoteMovieNames.Contains(pair.Key)).Select(pair => pair.Value)];
            _dancers = layers.GetValueOrDefault("dance")?.Player;
            // Shared movies are advanced by the first lane only.
            _fixedLayers = [.. layers.Values.Except(_beatLayers)];
            var skinPresentation = new TaikoSkinPresentation(new(skin("chibi"), skin("donbg") ?? skin("donbg_w_00"),
                skin("dance"), skin("bg_fever"), skin("fever"), skin("renda") ?? skin("renda_w_00")));
            // What the score, combo, gauge and results count: the play itself (an online partner's shadow), not what's shown.
            ITaikoJudgementSource counted = _live is null ? _source : _session;
            counted.LongNoteHit += progress =>
            {
                var previousScore = score.Value;
                if (score.Apply(progress, counted.CurrentTime)) updateScore(score.Value - previousScore);
                if (!progress.Note.IsBalloon) skinPresentation.OnRollHit();
                sound(progress.Note.Kind == PlayableLongNoteKind.BigRoll ? GameplaySoundEvent.BigLongHit
                    : progress.LastAction is TaikoInputAction.LeftKa or TaikoInputAction.RightKa ? GameplaySoundEvent.LongKaHit
                    : GameplaySoundEvent.LongDonHit);
            };
            // Player name board (traced call sequence; the name is host text, see NameText).
            var nameBoard = layers["player_name"].Player;
            GuestNameBoard.Show(nameBoard, side, visible: false);
            // Lanes built again (a seek back in a review or the editors): a banner already shown goes away.
            banner?.GotoFrame(0, play: false);
            var missed = false;
            _setMissed = value => missed = value;
            int great = 0, good = 0, miss = 0, maxCombo = 0;
            // Waiwai: both players share the voltage (clear at its norm).
            _result = () => new TaikoPlayResult(course, score.Value, great, good, miss, maxCombo,
                score.RollHits, stage?.Segments ?? gauge.FilledSegments,
                (stage?.State ?? gauge.State) != TaikoGaugeState.BelowClear) { Options = options, BalloonHits = score.BalloonHits };
            if (dan is not null && danArt is { } challengeArt)
                layers["dani_enso"].Player.SetNativeFill("challenge_lv_mini", challengeArt);
            var danProgress = default(Waddamburo.Game.Dan.DanProgress);
            var danSongEnds = new List<Waddamburo.Game.Dan.DanProgress>();
            _danSongEnds = danSongEnds;
            var danPanel = dan is null ? null : new DanExamPanel(dan, publish, publishItem);
            void publishPlay()
            {
                publish("play.score", LumenHostValue.FromNumber(score.Value));
                publish("play.combo", LumenHostValue.FromNumber(score.Combo));
                publish("play.maxCombo", LumenHostValue.FromNumber(maxCombo));
                publish("play.gauge", LumenHostValue.FromNumber((double)gauge.Value / TaikoSoulGauge.Max));
                publish("play.gaugeCleared", LumenHostValue.FromBoolean(gauge.State != TaikoGaugeState.BelowClear));
                publish("play.gaugeFull", LumenHostValue.FromBoolean(gauge.State == TaikoGaugeState.Full));
                publish("play.good", LumenHostValue.FromNumber(great));
                publish("play.ok", LumenHostValue.FromNumber(good));
                publish("play.bad", LumenHostValue.FromNumber(miss));
                var judged = great + good + miss;
                publish("play.accuracy", LumenHostValue.FromNumber(judged == 0 ? 1 : (great + good * .5) / judged));
            }
            publish("play.course", LumenHostValue.FromString(course.ToString().ToLowerInvariant()));
            publish("play.gaugeClearLine", LumenHostValue.FromNumber((double)gauge.Clear / TaikoSoulGauge.Max));
            _publish = publish;
            publish("player.side", LumenHostValue.FromNumber(side));
            publish("player.speed", LumenHostValue.FromNumber(options.ScrollSpeed));
            publish("player.doron", LumenHostValue.FromBoolean(options.Doron));
            publish("player.abekobe", LumenHostValue.FromBoolean(options.Abekobe));
            publish("player.random", LumenHostValue.FromNumber((int)options.Random));
            publish("player.shinuchi", LumenHostValue.FromBoolean(options.Shinuchi));
            publishPlay();
            var bannerTime = TaikoResultBanner.Time(chart);
            EndBanner = endBanner;
            var bannerShown = false;
            _onChartTime = time =>
            {
                publishLongNotes();
                // In hundredths: a bar does not need more, and a movie is not told sixty times a second.
                publish("play.progress", LumenHostValue.FromNumber(
                    Math.Round(Math.Clamp(time / TaikoResultBanner.Time(chart), 0, 1), 2)));
                // Balloons and kusudama count as rolls here (user-confirmed on the game; the traced totals fit).
                danProgress = new Waddamburo.Game.Dan.DanProgress(gauge.Value * 100 / TaikoSoulGauge.Max,
                    great, good, miss, maxCombo, score.RollHits + score.BalloonHits, score.Value);
                var danSong = Math.Min(danSongEnds.Count, Math.Max(chart.Segments.Length - 1, 0));
                var danStart = danSong == 0 ? default : danSongEnds[danSong - 1];
                danPanel?.Update(danProgress, danSong, danStart);
                gameMovies.DanTick();
                if (danPanel is not null && !chart.Segments.IsEmpty
                    && time >= chart.Segments[danSong].Start + TaikoResultBanner.Time(chart.Segments[danSong].Source))
                    danPanel.EndSong(danProgress, danSong, danStart);
                // A song's counts are what has built up by the time the next song's card starts.
                while (danSongEnds.Count + 1 < chart.Segments.Length && time >= chart.Segments[danSongEnds.Count + 1].Start)
                    danSongEnds.Add(danProgress);
                DanTotal = danProgress;
                DanFailed = danPanel?.Failed == true;
                if (EndsAt is { } end)
                    bannerTime = end;
                if (bannerShown || !EndBanner || time < bannerTime) return;
                bannerShown = true;
                var cleared = dan is not null ? Waddamburo.Game.Dan.DanExams.Passes(dan, danProgress, gold: false, danSongEnds)
                    : (stage?.State ?? gauge.State) != TaikoGaugeState.BelowClear;
                emit("song.ended", LumenHostValue.FromBoolean(cleared), LumenHostValue.FromBoolean(cleared && !missed));
                sound(!cleared ? GameplaySoundEvent.FailBanner
                    : missed ? GameplaySoundEvent.ClearBanner : GameplaySoundEvent.FullComboBanner);
                if (cleared && !missed) _character?.React("don_full_combo");
            };
            var partnerHands = players == 2 && remote is null && relay is null; // as the session below
            (int Note, long Award)? heldAward = null; // a big hit's first half, shown with its completion
            (float X, float Y) hitPoint = default; // the lane's target (moved by #JPOSSCROLL), once its presentation exists below
            var hitRadius = 0f;
            counted.Judged += judgement =>
            {
                Judged?.Invoke(judgement);
                // Every hit was early or late (the setting picks which are shown); a note that passed
                // unhit has no hit to time.
                if (!judgement.StrongHitCompleted && !judgement.TimedOut && judgement.Offset is { } offset && judgement.Result is { } result)
                {
                    var (x, y) = _presentation?.HitPoint ?? hitPoint;
                    LastTiming = new TimingMark(offset < TimeSpan.Zero, result == TaikoHitResult.Great, x, y, hitRadius,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _presentation?.PlayfieldPlacement(counted.CurrentTime) ?? Waddamburo.Lumen.Runtime.LumenMatrix.Identity);
                }
                stage?.Apply(judgement);
                var previousScore = score.Value;
                if (score.Apply(judgement, counted.CurrentTime))
                {
                    var award = score.Value - previousScore;
                    // A big hit is one popup with both halves (Green: one Create per hit; two "+1090" read as a big
                    // note scoring single, reported on Discord). Its completion follows at once, except a duet's hand
                    // note, whose other half is the partner's.
                    if (!judgement.StrongHitCompleted && judgement.HitObject.IsStrong && judgement.Result is TaikoHitResult.Great or TaikoHitResult.Good
                        && !(judgement.HitObject.IsHand && partnerHands))
                    {
                        if (heldAward is { } stale) updateScore(stale.Award); // ponytail: a completion that never came
                        heldAward = (judgement.NoteIndex, award);
                        updateScore(award, popup: false);
                    }
                    else
                    {
                        if (heldAward is { } held && judgement.StrongHitCompleted && held.Note == judgement.NoteIndex) award += held.Award;
                        else if (heldAward is { } stale) updateScore(stale.Award);
                        heldAward = null;
                        updateScore(award);
                    }
                }
                if (!judgement.StrongHitCompleted && judgement.Result is TaikoHitResult.Great or TaikoHitResult.Good)
                {
                    if (judgement.HitObject.IsRare) sound(GameplaySoundEvent.RareHit);
                    sound(judgement.HitObject.IsStrong ? GameplaySoundEvent.BigHit
                        : judgement.HitObject.Kind == PlayableNoteKind.Ka ? GameplaySoundEvent.KaHit : GameplaySoundEvent.DonHit);
                    if (score.Combo == 50 || score.Combo % 100 == 0 && score.Combo <= 5000)
                        sound(GameplaySoundEvent.Combo, score.Combo);
                }
                if (gauge.Apply(judgement) && gaugeMovie is not null)
                    _character?.SetGauge(gauge.State);
                if (!judgement.StrongHitCompleted && judgement.Result is { } hit)
                {
                    if (hit == TaikoHitResult.Great) great++;
                    else if (hit == TaikoHitResult.Good) good++;
                    else { miss++; missed = true; }
                    maxCombo = Math.Max(maxCombo, score.Combo);
                }
                if (_live is null)
                    _character?.OnJudged(judgement);
                else
                    _presentation!.SetCombo(score.Combo);
                skinPresentation.OnJudged(judgement, gauge);
                publishPlay();
                if (!judgement.StrongHitCompleted && judgement.Result is { } told)
                {
                    emit("note.judged", LumenHostValue.FromString(told switch
                    {
                        TaikoHitResult.Great => "good",
                        TaikoHitResult.Good => "ok",
                        _ => "bad",
                    }), LumenHostValue.FromBoolean(judgement.HitObject.IsStrong),
                        LumenHostValue.FromString(judgement.HitObject.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa ? "ka" : "don"));
                    if (score.Combo > 0 && score.Combo % 100 == 0)
                        emit("combo.milestone", LumenHostValue.FromNumber(score.Combo));
                }
            };
            // A reviewed play shows the baked state of its moment: the score, combo and gauge are set
            // from it after every move (a seek, or playing on: a Go-Go edge timed by the playhead
            // could otherwise score differently from the play itself).
            _restore = state =>
            {
                score.Restore(state?.Score ?? new TaikoScoreState(0, 0, 0, 0, -1, 0));
                gauge.Restore(state?.Gauge ?? 0);
                _presentation!.SetCombo(score.Combo);
                _character?.SetGauge(gauge.State);
                publishPlay();
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
            // Waiwai: both Dons stand in front of the stage lights and projectors (user-confirmed: the
            // second lane's Don, drawn after them, looked right).
            _characterSlot = stage is not null
                ? background.Count(pair => GameplaySceneComposition.Depth(pair.Key) >= 3000)
                : Array.FindIndex(background, pair => pair.Key is "donbg" or "donbg_w_00") + 1;
            _presentation = new TaikoLumenPresentation(chart, _source,
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
                    : null,
                layers.GetValueOrDefault("onp_rare_0_w_00"),
                layers.GetValueOrDefault("onp_kadon"),
                // One onp_moji player per text label (a traced clone per note, parked on its label).
                NoteTextLabels.ToDictionary(label => label, label =>
                    {
                        var text = layers["onp_moji"] with { Player = content("onp_moji").CreatePlayer() };
                        if (!text.Player.TryGotoLabel("", label, play: false))
                            throw new InvalidDataException($"Note text movie is missing label '{label}'.");
                        return text;
                    }),
                // The bar line of a 譜面分岐 check: lane_syousetsu's yellow "bunki" frame.
                chart.Branching is null ? null : layers["lane_syousetsu"] with { Player = branchBar() },
                layers.GetValueOrDefault("fukidashi_level_1p")?.Player,
                // Each scene movie's role, the modcharts' universal markers (dancers, gauge, …).
                roles: layers.GroupBy(static pair => pair.Value.Player).ToDictionary(static group => group.Key, static group => group.First().Key));
            LumenPlayer branchBar()
            {
                var player = content("lane_syousetsu").CreatePlayer();
                if (!player.TryGotoLabel("", "bunki", play: false))
                    throw new InvalidDataException("Bar line movie is missing label 'bunki'.");
                return player;
            }
            hitPoint = _presentation.HitPoint;
            // A modchart turns Don-chan's model (yaw, pitch, roll); 0 when it doesn't.
            _presentation.DonTurn = (yaw, pitch, roll) => _character?.Turn(yaw, pitch, roll);
            if (_live is not null)
            {
                _presentation.ExternalCombo = true;
                _source.Judged += judgement => _character?.OnJudged(judgement);
            }
            _presentation.Doron = options.Doron;
            // The options' icons on the lane (traced SetSpeed(1) for none). lane_obi's setOption shows
            // speedx<n> above 1, doron and abekobe when true, random<level> above 0 (1 きまぐれ, 2 でたらめ),
            // and option_serious "on" for SetSeriousMode(true) (真打).
            if (gameMovies.Board is { } gameBoard)
            {
                gameBoard.TryInvokeCallback("SetSpeed", [LumenHostValue.FromNumber(options.SpeedIcon)]); // Green's art by range
                gameBoard.TryInvokeCallback("SetDoron", [LumenHostValue.FromBoolean(options.Doron)]);
                gameBoard.TryInvokeCallback("SetAbekobe", [LumenHostValue.FromBoolean(options.Abekobe)]);
                gameBoard.TryInvokeCallback("SetRandomLevel", [LumenHostValue.FromNumber((int)options.Random)]);
                gameBoard.TryInvokeCallback("SetSeriousMode", [LumenHostValue.FromBoolean(options.Shinuchi)]);
            }
            gameMovies.Start(chart.Branching is not null, kusudama?.Players ?? 1, waiwai: stage is not null);
            hitRadius = _presentation.HitRadius;
            counted.LongNoteHit += progress =>
            {
                if (progress.IsPopped) emit("balloon.popped");
                else if (!progress.Note.IsBalloon && progress.LastAction is { } hand)
                    emit("roll.hit", LumenHostValue.FromString(hand is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? "don" : "ka"),
                        LumenHostValue.FromBoolean(progress.Note.Kind == PlayableLongNoteKind.BigRoll));
            };
            _presentation.DrumHit += action => emit("drum.hit",
                LumenHostValue.FromString(action is TaikoInputAction.LeftDon or TaikoInputAction.LeftKa ? "left" : "right"),
                LumenHostValue.FromString(action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? "don" : "ka"));
            // No value on a chart without routes.
            if (chart.Branching is not null) publish("play.branch", LumenHostValue.FromString("normal"));
            _presentation.BranchChanged += route => publish("play.branch", LumenHostValue.FromString(route.ToString().ToLowerInvariant()));
            _presentation.GoGoChanged += active =>
            {
                publish("play.goGo", LumenHostValue.FromBoolean(active));
                _character?.SetBalloonVisible(_longNotes.BalloonVisible);
                _character?.SetGoGo(active);
                Console.WriteLine($"Gameplay Go-Go (lane {lane}): {(active ? "start" : "end")} at {_source.CurrentTime.TotalSeconds:F3}s.");
            };
        }

        /// <summary>A balloon/kusudama result is still animating; the song should not end under it.</summary>
        public bool OverlayActive => !_stopped && _longNotes.BalloonVisible;

        public void Stop() => _stopped = true;

        public double AdvanceAnimations(TimeSpan chartTime, LumenInputSnapshot input)
        {
            if (_stopped) return 0;
            chartTime = shown(chartTime);
            var delta = _animationClock.Advance(chartTime);
            foreach (var layer in _fixedLayers) layer.Player.Advance(input);
            // The dance movies bring their first dancer in on their first frame; the game holds them until the
            // song starts (reported on Discord: the dancers were already in when the stage appeared).
            for (var i = 0; i < _animationClock.FramesToAdvance; i++)
                foreach (var layer in _beatLayers)
                    if (layer.Player != _dancers || chartTime >= TimeSpan.Zero)
                        layer.Player.Advance(input);
            // Note faces by combo, at the beat's phase: the game's by their adapter, ours by themselves.
            var beat = _animationClock.Position(chartTime) * 89 / 3600;
            _publish("play.beat", LumenHostValue.FromNumber(beat));
            // Each whole beat once, going forward; a review played back counts again from where it lands.
            var whole = (long)Math.Floor(beat);
            if (whole != _wholeBeat)
            {
                if (whole > _wholeBeat && _wholeBeat != long.MinValue) _emit("beat.passed", [LumenHostValue.FromNumber(whole)]);
                _wholeBeat = whole;
            }
            _gameMovies.NoteFaces(_noteFaces, beat);
            _longNotes.ShowFaces(_gameMovies.LongNoteFace);
            _flights.Advance();
            _longNotes.AdvanceAnimations(note => _gameMovies.LongNoteSteps(note, beat));
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
            // Balloon one-shots follow their authored real-time overlay. Otherwise Don plays BPM / 120 frames a tick
            // (traced: don_combo's 45 frames take 29 ticks at 185 BPM), not the Lumen movies' BPM / 89: he ran
            // 120 / 89 too fast (reported on Discord).
            return _longNotes.BalloonVisible ? 1 : delta * 89 / 120;
        }

        public void ReportDiagnostics()
        {
            foreach (var player in _longNotes.Players)
                foreach (var diagnostic in player.Diagnostics)
                    Console.Error.WriteLine($"Long-note presentation: {diagnostic.Code}: {diagnostic.Message}");
        }

        // Diagnostic: WADDAMBURO_DRUM_TRACE=1 prints each drum hit with the time since the pad's last one
        // (a drum that sends double hits shows pairs a few milliseconds apart) and the ones the debounce drops.
        private static readonly bool TraceDrum = Environment.GetEnvironmentVariable("WADDAMBURO_DRUM_TRACE") == "1";
        private readonly TimeSpan[] _lastPadPress = new TimeSpan[4];
        private readonly DrumDebounce _debounce = new();

        public void ResetCharacter() => _character?.ResetPose();

        public void CloseLongNotes() => _longNotes.Abandon();

        /// <summary>
        /// A reviewed play at <paramref name="time"/>: played on to it (<paramref name="play"/>, the judgements
        /// passed show as they did) or jumped there; either way its baked state is shown.
        /// </summary>
        public void Review(TimeSpan time, bool play)
        {
            if (_review is not { } review) return;
            if (play) review.Play(time);
            else review.Jump(time);
            _restore(review.Timeline.StateAt(time));
            _presentation.Update(time);
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
            // The end banner, as in play (seeking back rebuilds the lanes, which takes it down).
            // ponytail: a pass over the bake each frame; keep a cursor if long charts show it.
            _setMissed(review.Timeline.Events.Any(e => e.Time <= time && e.Judgement?.Result == TaikoHitResult.Miss));
            _onChartTime(time);
        }

        public void FollowBranch(Lane leader) => _session.FollowBranch = leader._session.BranchAt;

        public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime, TimeSpan debounce = default, double clockSpeed = 1)
        {
            if (_stopped) return;
            if (_live is not null)
            {
                _live.Advance(chartTime, _presentation.Hit, Replay.Add);
                _presentation.Update(chartTime);
                // Their banner shows once their play has caught up, or after a short wait (a partner who dropped out).
                var late = chartTime - PartnerGrace;
                _onChartTime(_session.CurrentTime > late ? _session.CurrentTime : late);
                _character?.SetBalloonVisible(_longNotes.BalloonVisible);
                return;
            }
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
                var accepted = _debounce.Accept(hit, press.Timestamp, debounce);
                if (TraceDrum)
                {
                    Console.WriteLine($"[drum] P{_side + 1} {hit} +{(press.Timestamp - _lastPadPress[(int)hit]).TotalMilliseconds:F1} ms"
                        + (accepted ? "" : " dropped"));
                    _lastPadPress[(int)hit] = press.Timestamp;
                }
                if (!accepted)
                    continue;
                var eventTime = chartTime - (keyboard.Timestamp - press.Timestamp) * clockSpeed;
                eventTime = eventTime < _session.CurrentTime ? _session.CurrentTime : eventTime;
                eventTime = eventTime > chartTime ? chartTime : eventTime;
                _presentation.Hit(hit);
                _playHitSound?.Invoke(_soundLane, hit);
                Replay.Add(hit, eventTime);
                _relay?.Record(hit, eventTime);
                // ponytail: live play only; a replay's second hand still shows its press flash.
                if (_session.SubmitInput(hit, eventTime) == TaikoInputResult.SecondHand)
                    _presentation.KeepHitLight();
            }
            _session.AdvanceTo(chartTime);
            _relay?.SetClock(chartTime);
            _presentation.Update(chartTime);
            _onChartTime(chartTime);
            _character?.SetBalloonVisible(_longNotes.BalloonVisible);
        }

        public void ReplaceEffects(ChartEffects effects) => _presentation.ReplaceEffects(effects);

        public IReadOnlyList<StageEffectElement> EffectElements(TimeSpan time) =>
            _presentation.EffectElements(shown(time), _character?.Layer, _characterSlot);

        public void RemoveNote(int index) => _presentation.RemoveNote(index);

        public void FlashTarget() => _presentation.FlashTarget();

        public (List<LumenSceneLayer> Back, List<LumenSceneLayer> Front) CreateLayers(TimeSpan time) =>
            _presentation.CreateLayers(shown(time), _character?.Layer, _characterSlot);

        /// <summary>The beat clock's interpolation for this lane's beat-driven movies, else null.</summary>
        public float? BeatInterpolation(LumenPlayer player)
        {
            foreach (var layer in _beatLayers)
                if (layer.Player == player)
                    return _animationClock.Interpolation;
            return null;
        }

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

/// <summary>
/// A judged hit, early or late (<paramref name="Great"/>: within the Great window), at a lane's hit
/// target (X, Y and Radius on the 1280x720 stage); <paramref name="At"/> is a Stopwatch timestamp.
/// <paramref name="Placement"/> is the playfield's placement at the hit (modchart, layout): the glow is drawn through it.
/// </summary>
internal sealed record TimingMark(bool Early, bool Great, float X, float Y, float Radius, long At,
    Waddamburo.Lumen.Runtime.LumenMatrix Placement);
