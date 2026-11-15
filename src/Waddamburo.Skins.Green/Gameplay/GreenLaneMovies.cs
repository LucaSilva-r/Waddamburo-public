using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>A lane's game movies: the board, gauge and counters (GameplayGameMovies) and the backdrop (TaikoSkinPresentation).</summary>
public sealed class GreenLaneMovies : ILaneMovies
{
    private readonly IReadOnlyDictionary<string, LumenSceneLayer> _layers;
    private readonly GameplayGameMovies _movies;
    private readonly TaikoSkinPresentation _backdrop;

    public GreenLaneMovies(IReadOnlyDictionary<string, LumenSceneLayer> layers, string gaugeRole, Func<int> gaugeSegments,
        LumenSceneLayer? kusudama)
    {
        _layers = layers;
        LumenPlayer? skin(string role) => layers.TryGetValue(role, out var layer) ? layer.Player : null;
        // Host label jumps traced in the game: gauge fire on full gauge, splash at Go-Go start (one player only; two-player
        // gameplay loads no splash), and the end-of-song banner. Waiwai has no player gauge, score popup, fire or banner.
        _movies = new GameplayGameMovies(layers["lane_obi"].Player, skin(gaugeRole), gaugeSegments, skin("score_add_don_1p"),
            (layers.GetValueOrDefault("combo_bonus_don_1p") ?? layers["sinuchi_combo_bonus_don_1p"]).Player,
            skin("gage_fire_1p"), skin("action_gogotime"), skin("action_result"), layers["lane_hit"].Player,
            skin("fukidashi_level_1p"), skin("dani_enso"), layers["renda_num"].Player, layers["action_fusen_1p"].Player,
            (kusudama ?? layers.GetValueOrDefault("action_kusudama"))?.Player, layers["lane_hit_effect"].Player);
        // Waiwai has its own roll art and backdrop in place of the normal ones.
        _backdrop = new TaikoSkinPresentation(new(skin("chibi"), skin("donbg") ?? skin("donbg_w_00"),
            skin("dance"), skin("bg_fever"), skin("fever"), skin("renda") ?? skin("renda_w_00")));
    }

    public void State(string name, LumenHostValue value) => _movies.State(name, value);

    public void Item(string name, int index, LumenHostValue value) => _movies.Item(name, index, value);

    public void Event(string name, ReadOnlySpan<LumenHostValue> arguments) => _movies.Event(name, arguments);

    public void SetUpBoard(bool twoPlayers, int side, bool sharedVoltage, TaikoCourse course, bool dan) =>
        _movies.SetUpBoard(twoPlayers, side, sharedVoltage, course, dan);

    // Traced call sequence; the name is host text (NameText).
    public void HideNameBoard(int side) => GuestNameBoard.Show(_layers["player_name"].Player, side, visible: false);

    public void ShowDanChallenge(LumenNativeSurfaceKey art) => GameplayGameMovies.ShowDanChallenge(_layers["dani_enso"].Player, art);

    public void ShowOptions(TaikoPlayOptions options) => _movies.ShowOptions(options);

    public void Start(bool routes, int kusudamaPlayers, bool waiwai) => _movies.Start(routes, kusudamaPlayers, waiwai);

    public void StartNotes(IEnumerable<LumenPlayer> notes) => GameplayGameMovies.StartNotes(notes);

    public void NoteFaces(IEnumerable<LumenPlayer> notes, double beat) => _movies.NoteFaces(notes, beat);

    public void LongNoteFace(LumenPlayer note) => _movies.LongNoteFace(note);

    public int LongNoteSteps(LumenPlayer note, double beat) => _movies.LongNoteSteps(note, beat);

    public void Shown(TaikoHitResult result, bool big, bool ka) => _movies.Judged(result, big, ka);

    public void Counted(TaikoNoteJudgement judgement, TaikoSoulGauge gauge) => _backdrop.OnJudged(judgement, gauge);

    public void RollHit() => _backdrop.OnRollHit();

    public void DanTick() => _movies.DanTick();

    public void KeepHitLight() => _movies.KeepHitLight();

    public void FlashTarget() => _movies.FlashTarget();

    public void Shown(LumenPlayer note) => _movies.Shown(note);

    public void Sized(LumenPlayer note, float length) => _movies.Sized(note, length);

    public void Stepped(LumenPlayer note) => _movies.Stepped(note);

    public void Struck(LumenPlayer note) => _movies.Struck(note);

    public bool Breaking(LumenPlayer kusudama) => _movies.Breaking(kusudama);
}
