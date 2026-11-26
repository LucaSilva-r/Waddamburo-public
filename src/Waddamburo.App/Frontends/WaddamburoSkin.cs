using Waddamburo.Catalog;
using Waddamburo.Game;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's frontend's skin: its movies read everything from the provider (the gameplay presentation publishes
/// play.*, the transitions transition.phase), so nothing here drives a movie. No lane character, no note flights of the
/// engine's (our flight movie flies the hits itself), no patches: none of the game's movies ever loads.
/// </summary>
internal sealed class WaddamburoSkin : ISkin
{
    public IGameplaySkin Gameplay { get; } = new GameplaySkin();

    public ITransitionSkin Transitions { get; } = new TransitionSkin();

    public void Decoded(string archive, LumenMovieContent content, Action applyUpscales, Action<byte[]> made) => applyUpscales();

    public bool IsPixelArt(string archive) => false;

    public IEnumerable<SceneDefinition> OwnScenes => [];

    public Func<LumenTextureContent, LumenTextureContent>? Upscaled { set { } }

    private sealed class GameplaySkin : IGameplaySkin
    {
        public LumenSceneLayer? KadonCopy(LumenSceneLayer layer, LumenMovieContent content) => null;

        public INoteFlights Flights(IEnumerable<LumenSceneLayer> layers, IEnumerable<LumenSceneLayer>? kadonLayers, LumenSceneLayer sceneOwn) =>
            new OwnFlights();

        public ILaneMovies LaneMovies(IReadOnlyDictionary<string, LumenSceneLayer> layers, string gaugeRole, Func<int> gaugeSegments,
            LumenSceneLayer? kusudama) => new LaneMovies();

        public ILaneCharacter? Character(LumenSceneLayer layer, int lane, bool stage, (LumenPlayer Balloon, LumenPlayer? Kusudama) overlays) => null;

        public void Waiwai(WaiwaiVoltage voltage, IReadOnlyDictionary<string, LumenPlayer> movies)
        {
        }

        public ILumenHostBinding? LongNoteHost() => null;
    }

    // Our flight movie is a scene layer of its own: it hears play.hit and flies the notes itself.
    private sealed class OwnFlights : INoteFlights
    {
        public IEnumerable<LumenSceneLayer> ActiveLayers => [];

        public bool Owns(LumenPlayer player) => false;

        public void Trigger(TaikoNoteJudgement judgement)
        {
        }

        public void Trigger(TaikoLongNoteProgress progress)
        {
        }

        public void Advance()
        {
        }
    }

    // Our lane movies read play.* from the provider: nothing to call.
    private sealed class LaneMovies : ILaneMovies
    {
        public void State(string name, LumenHostValue value)
        {
        }

        public void Item(string name, int index, LumenHostValue value)
        {
        }

        public void Event(string name, ReadOnlySpan<LumenHostValue> arguments)
        {
        }

        public void SetUpBoard(bool twoPlayers, int side, bool sharedVoltage, TaikoCourse course, bool dan)
        {
        }

        public void HideNameBoard(int side)
        {
        }

        public void ShowDanChallenge(LumenNativeSurfaceKey art)
        {
        }

        public void ShowOptions(TaikoPlayOptions options)
        {
        }

        public void Start(bool routes, int kusudamaPlayers, bool waiwai)
        {
        }

        public void StartNotes(IEnumerable<LumenPlayer> notes)
        {
        }

        public void NoteFaces(IEnumerable<LumenPlayer> notes, double beat)
        {
        }

        public void LongNoteFace(LumenPlayer note)
        {
        }

        // Ours play one frame a tick.
        public int LongNoteSteps(LumenPlayer note, double beat) => 1;

        public void Shown(TaikoHitResult result, bool big, bool ka)
        {
        }

        public void Counted(TaikoNoteJudgement judgement, TaikoSoulGauge gauge)
        {
        }

        public void RollHit()
        {
        }

        public void DanTick()
        {
        }

        public void KeepHitLight()
        {
        }

        public void FlashTarget()
        {
        }

        public void Shown(LumenPlayer note)
        {
        }

        public void Sized(LumenPlayer note, float length)
        {
        }

        public void Stepped(LumenPlayer note)
        {
        }

        public void Struck(LumenPlayer note)
        {
        }

        public bool Breaking(LumenPlayer kusudama) => false;
    }

    // Our intermissions read transition.phase: covering is at once, revealing is theirs.
    private sealed class TransitionSkin : ITransitionSkin
    {
        public bool Cover(LumenPlayer movie, bool title, int side) => true;

        public void Reveal(LumenPlayer movie, bool title)
        {
        }

        public void ShowTitle(LumenPlayer movie, LumenNativeSurfaceKey title)
        {
        }

        // Our rainbow keeps the title in a clip of this name (its own layout, read from song.title).
        public bool TitleShown(LumenPlayer movie) =>
            movie.TryGetInstanceBounds("scene_change_song_name", out var bounds)
            && bounds is { Width: > 0, Height: > 0 } && bounds.X + bounds.Width > 0 && bounds.X < 1280;
    }
}
