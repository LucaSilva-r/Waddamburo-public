using System.Collections.Immutable;
using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Scenes;

/// <summary>A composition-owned mapping from authored requests to product scenes.</summary>
public sealed class SceneCatalog
{
    private ImmutableDictionary<SceneId, SceneDefinition> _definitions;
    private readonly ImmutableDictionary<SceneRouteKey, SceneId> _routes;

    public SceneCatalog(
        IEnumerable<SceneDefinition> definitions,
        IEnumerable<SceneTransitionRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(routes);

        var definitionBuilder = ImmutableDictionary.CreateBuilder<SceneId, SceneDefinition>();
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!definitionBuilder.TryAdd(definition.Id, definition))
                throw new ArgumentException($"Scene '{definition.Id}' is defined more than once.", nameof(definitions));
        }
        if (definitionBuilder.Count == 0)
            throw new ArgumentException("The scene catalog must contain at least one definition.", nameof(definitions));

        var routeBuilder = ImmutableDictionary.CreateBuilder<SceneRouteKey, SceneId>();
        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            if (!definitionBuilder.ContainsKey(route.Source))
                throw new ArgumentException($"Route source scene '{route.Source}' is not defined.", nameof(routes));
            if (!definitionBuilder.ContainsKey(route.Target))
                throw new ArgumentException($"Route target scene '{route.Target}' is not defined.", nameof(routes));
            if (!routeBuilder.TryAdd(new SceneRouteKey(route.Source, route.Request), route.Target))
                throw new ArgumentException($"Scene '{route.Source}' has more than one route for request {route.Request}.", nameof(routes));
        }

        _definitions = definitionBuilder.ToImmutable();
        _routes = routeBuilder.ToImmutable();
    }

    /// <summary>Replaces an existing scene's definition, e.g. a per-song gameplay composition.</summary>
    public void Replace(SceneDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_definitions.ContainsKey(definition.Id))
            throw new KeyNotFoundException($"Scene '{definition.Id}' is not defined.");
        _definitions = _definitions.SetItem(definition.Id, definition);
    }

    public SceneDefinition GetDefinition(SceneId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _definitions.TryGetValue(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Scene '{id}' is not defined.");
    }

    public bool TryResolve(SceneId source, LumenSceneRequest request, out SceneDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_routes.TryGetValue(new SceneRouteKey(source, request), out var target))
        {
            definition = _definitions[target];
            return true;
        }

        definition = null;
        return false;
    }

    private readonly record struct SceneRouteKey(SceneId Source, LumenSceneRequest Request);
}

public sealed record SceneTransitionRoute
{
    public SceneTransitionRoute(SceneId source, LumenSceneRequest request, SceneId target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        Source = source;
        Request = request;
        Target = target;
    }

    public SceneId Source { get; }

    public LumenSceneRequest Request { get; }

    public SceneId Target { get; }
}
