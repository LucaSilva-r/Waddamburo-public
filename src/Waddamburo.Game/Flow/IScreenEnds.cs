using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Flow;

/// <summary>
/// How a skin's movies tell the engine a screen is over (results, retry, the AI battle farewell, tutorials, game
/// over): the flows ask here instead of reading a movie's own flags. Movies on the provider say it with requests.
/// </summary>
public interface IScreenEnds
{
    /// <summary>The screen's movies are done with it: the flow moves on.</summary>
    bool Ended(SceneId screen, IReadOnlyList<LumenPlayer> movies);
}
