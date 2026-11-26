using Waddamburo.Game.Flow;

namespace Waddamburo.App.Frontends;

/// <summary>
/// What a frontend is made of and offers (docs/development/frontends.md): the code asks for a feature or a kind of
/// content, never which frontend runs. Picked by config.cfg's frontend at start.
/// </summary>
/// <param name="GameMovies">The game's movies may load (else packages only, a missing one a placeholder).</param>
/// <param name="GameSounds">The game's sounds, jingles and screen sounds may play (else our pack only).</param>
/// <param name="Mascot">Don-chan: Green's 3D Don on the stands and lanes.</param>
/// <param name="Attract">An attract loop between sessions (else who's playing and the entry).</param>
/// <param name="Waiwai">Two players play Waiwai (else side by side).</param>
/// <param name="AiBattle">AI battle is offered.</param>
/// <param name="Rewards">Don Medals, the reward shop and "New rewards".</param>
internal sealed record Frontend(string Name, bool GameMovies, bool GameSounds, bool Mascot, bool Attract, bool Waiwai, bool AiBattle,
    bool Rewards)
{
    public static Frontend Green { get; } = new("green", GameMovies: true, GameSounds: true, Mascot: true, Attract: true, Waiwai: true,
        AiBattle: true, Rewards: true);

    // ponytail: home only; a cabinet runs Green's.
    public static Frontend Waddamburo { get; } = new("waddamburo", GameMovies: false, GameSounds: false, Mascot: false, Attract: false,
        Waiwai: false, AiBattle: false, Rewards: false);

    public static Frontend For(ArcadeSettings settings) => settings.WaddamburoFrontend ? Waddamburo : Green;
}
