using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Provider;

/// <summary>
/// The requests one screen takes (free-movies.md, Requests). Its flow says what each does; installed on the
/// screen's movies, it answers their <c>Wd.request</c>. Scope: a movie the scene places asks for what its screen
/// takes; everything inside it (child clips, attached sprites, movies loaded into its clips) runs in its player and
/// asks the same.
/// </summary>
/// <param name="screen">The screen, as WdNames scopes requests (entry, song_select, ...).</param>
public sealed class WdRequests(string screen)
{
    private readonly Dictionary<string, Func<LumenHostValue[], string?>> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// What a request does: <paramref name="handler"/> returns null when it took the request, else why not (said to
    /// whoever debugs the movie: a refusal is no error in it).
    /// </summary>
    public WdRequests Handle(string name, Func<LumenHostValue[], string?> handler)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.Request } known || known.Scope != screen)
            throw new ArgumentException($"'{name}' is not a request of {screen} (WdNames).", nameof(name));
        _handlers[name] = handler;
        return this;
    }

    /// <summary>The request's answer: null taken, else why not.</summary>
    public string? Take(string name, LumenHostValue[] arguments) =>
        _handlers.TryGetValue(name, out var handler) ? handler(arguments)
        : WdNames.Find(name) is { Scope: var scope } && scope != screen ? $"{name} is a request of {scope}, not of {screen}"
        : $"{screen} does not take {name} yet";

    /// <summary>The movies <paramref name="movies"/> ask this screen.</summary>
    public void Install(IEnumerable<LumenPlayer> movies)
    {
        ArgumentNullException.ThrowIfNull(movies);
        foreach (var movie in movies)
            movie.RequestHandler = Take;
    }
}
