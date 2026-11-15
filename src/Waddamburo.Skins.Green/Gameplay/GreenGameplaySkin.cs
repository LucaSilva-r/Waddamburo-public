using Waddamburo.Game;
using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Skins.Green.Patching;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>The game's gameplay movies and Don-chan's model behind the lanes.</summary>
public sealed class GreenGameplaySkin : IGameplaySkin
{
    // OpenTaiko's Kadon (TJA G) has no art in the game's files: KadonNotePatch doubled the big ka's and the flight's
    // textures at load, purple; the copy draws on those.
    public LumenSceneLayer? KadonCopy(LumenSceneLayer layer, LumenMovieContent content) =>
        KadonNotePatch.AppliesTo(content) && KadonNotePatch.CopiesAt(content) is var copies and > 0
            ? layer with
            {
                Player = content.CreatePlayer(),
                TextureOffset = layer.TextureOffset + (uint)copies,
                TextureCount = (uint)copies,
            }
            : null;

    public INoteFlights Flights(IEnumerable<LumenSceneLayer> layers, IEnumerable<LumenSceneLayer>? kadonLayers, LumenSceneLayer sceneOwn) =>
        new TaikoHitFlights(layers, kadonLayers, sceneOwn);

    public ILaneMovies LaneMovies(IReadOnlyDictionary<string, LumenSceneLayer> layers, string gaugeRole, Func<int> gaugeSegments,
        LumenSceneLayer? kusudama) => new GreenLaneMovies(layers, gaugeRole, gaugeSegments, kusudama);

    public ILaneCharacter Character(IDonPresentationController don, LumenSceneLayer layer, int lane, bool stage,
        (LumenPlayer Balloon, LumenPlayer? Kusudama) overlays) => new TaikoDonPresentation(don, layer, lane, stage, overlays);

    public void Waiwai(WaiwaiVoltage voltage, IReadOnlyDictionary<string, LumenPlayer> movies) => _ = new GameWaiwaiMovies(voltage, movies);

    public ILumenHostBinding? LongNoteHost() => new TaikoGameplayHostBinding();
}
