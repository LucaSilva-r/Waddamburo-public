using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The skin's side of the gameplay lanes. The lanes keep the scene's slots by role (the game's movie names, shared by
/// every skin) and their engine state; the skin drives its movies from it (the game's by their callbacks, ours read
/// the provider themselves) and draws the lane's character.
/// </summary>
public interface IGameplaySkin
{
    /// <summary>The purple copy (a Kadon) of a note or flight movie whose textures the skin doubled at load; null: none.</summary>
    LumenSceneLayer? KadonCopy(LumenSceneLayer layer, LumenMovieContent content);

    /// <summary>The note flights to the gauge, on copies of the flight movie (and its Kadon copies).</summary>
    INoteFlights Flights(IEnumerable<LumenSceneLayer> layers, IEnumerable<LumenSceneLayer>? kadonLayers, LumenSceneLayer sceneOwn);

    /// <summary>A lane's movies, by role; <paramref name="gaugeRole"/> is the gauge the lane shows.</summary>
    ILaneMovies LaneMovies(IReadOnlyDictionary<string, LumenSceneLayer> layers, string gaugeRole, Func<int> gaugeSegments,
        LumenSceneLayer? kusudama);

    /// <summary>
    /// A lane's character on <paramref name="layer"/> (lane 0 top, 1 the second player's); <paramref name="stage"/>: it
    /// stands in a movie the scene draws itself; <paramref name="overlays"/>: the lane's balloon and kusudama movies.
    /// </summary>
    ILaneCharacter Character(IDonPresentationController don, LumenSceneLayer layer, int lane, bool stage,
        (LumenPlayer Balloon, LumenPlayer? Kusudama) overlays);

    /// <summary>Waiwai's shared voltage starts: the skin's stage movies (the first lane's, by role) follow it.</summary>
    void Waiwai(WaiwaiVoltage voltage, IReadOnlyDictionary<string, LumenPlayer> movies);

    /// <summary>The host of a long note's movie made for a note.</summary>
    ILumenHostBinding? LongNoteHost();
}

/// <summary>The note flights to the gauge: started by judgements and roll hits, drawn by the lane.</summary>
public interface INoteFlights
{
    IEnumerable<LumenSceneLayer> ActiveLayers { get; }

    bool Owns(LumenPlayer player);

    void Trigger(TaikoNoteJudgement judgement);

    void Trigger(TaikoLongNoteProgress progress);

    void Advance();
}

/// <summary>A lane's movies beyond the notes the lane places: told the provider's values and the play's events.</summary>
public interface ILaneMovies : ITaikoLongNoteMovies
{
    /// <summary>A provider state of the lane changed (or is told for the first time).</summary>
    void State(string name, LumenHostValue value);

    void Item(string name, int index, LumenHostValue value);

    void Event(string name, ReadOnlySpan<LumenHostValue> arguments);

    /// <summary>The board at the play's start (one or two players, the drum, a shared-voltage lane, the course, a dan course).</summary>
    void SetUpBoard(bool twoPlayers, int side, bool sharedVoltage, TaikoCourse course, bool dan);

    /// <summary>The player's name board starts hidden (the flow shows it).</summary>
    void HideNameBoard(int side);

    /// <summary>A dan challenge course's level art.</summary>
    void ShowDanChallenge(LumenNativeSurfaceKey art);

    void ShowOptions(TaikoPlayOptions options);

    /// <summary>The play starts (whether the chart has routes, the kusudama's players, Waiwai).</summary>
    void Start(bool routes, int kusudamaPlayers, bool waiwai);

    /// <summary>The note movies start on their first face.</summary>
    void StartNotes(IEnumerable<LumenPlayer> notes);

    /// <summary>Once a tick: the note movies' faces at the beat's phase.</summary>
    void NoteFaces(IEnumerable<LumenPlayer> notes, double beat);

    void LongNoteFace(LumenPlayer note);

    /// <summary>Frames a long note's movie plays this tick.</summary>
    int LongNoteSteps(LumenPlayer note, double beat);

    /// <summary>A judgement as shown on the lane.</summary>
    void Shown(TaikoHitResult result, bool big, bool ka);

    /// <summary>A judgement counted, with the gauge after it (the backdrop's runners, dancers, fever).</summary>
    void Counted(TaikoNoteJudgement judgement, TaikoSoulGauge gauge);

    void RollHit();

    /// <summary>Once a tick, after the lane has published (the dan panel).</summary>
    void DanTick();

    void KeepHitLight();

    void FlashTarget();
}

/// <summary>A moment of the play the lane's character reacts to (see free-movies.md, Characters).</summary>
public enum CharacterMoment
{
    FullCombo,
}

/// <summary>The lane's character: the engine says what happens, the skin's character shows it.</summary>
public interface ILaneCharacter : ITaikoLongNoteCharacter
{
    void SetGoGo(bool active);

    void SetGauge(TaikoGaugeState state);

    void ResetPose();

    void SetBalloonVisible(bool visible);

    void OnJudged(TaikoNoteJudgement judgement);

    void React(CharacterMoment moment);

    /// <summary>A modchart's turn (degrees).</summary>
    void Turn(double yaw, double pitch, double roll);

    LumenRenderSnapshot Tint(LumenRenderSnapshot scene);

    /// <summary>The layer the lane draws it on (null: none this frame).</summary>
    LumenSceneLayer? Layer { get; }
}
