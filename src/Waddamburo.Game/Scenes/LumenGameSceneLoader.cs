using System.Collections.Immutable;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

public interface ILumenMovieContentSource
{
    ValueTask<LumenMovieContent> LoadAsync(
        string archiveId,
        string movieId,
        CancellationToken cancellationToken);
}

public interface ILumenMovieContentScope : ILumenMovieContentSource, IDisposable;

public interface IScopedLumenMovieContentSource : ILumenMovieContentSource
{
    ILumenMovieContentScope CreateSceneScope();
}

/// <summary>Creates a fresh host and optional post-construction initializer for one layer.</summary>
public interface ILumenLayerHostFactory
{
    LumenLayerHost Create(SceneLayerDefinition layer);
}

public sealed record LumenLayerHost(
    ILumenHostBinding? Binding,
    Action<LumenPlayer>? Initialize = null);

public sealed record LoadedLumenLayer(
    SceneLayerDefinition Definition,
    LumenMovieContent Content,
    uint TextureOffset,
    uint TextureCount);

/// <summary>CPU-owned loaded Lumen scene; platform adapters own uploaded textures.</summary>
public sealed class LumenGameSceneInstance : IGameSceneInstance
{
    private readonly ImmutableArray<object> _hostLifetimes;
    private bool _disposed;

    internal LumenGameSceneInstance(
        SceneId id,
        LumenScenePlayer player,
        ImmutableArray<LoadedLumenLayer> layers,
        ImmutableArray<object> hostLifetimes)
    {
        Id = id;
        Player = player;
        Layers = layers;
        Textures = [.. layers.SelectMany(static layer => layer.Content.Textures)];
        _hostLifetimes = hostLifetimes;
    }

    public SceneId Id { get; }

    public LumenScenePlayer Player { get; }

    public ImmutableArray<LoadedLumenLayer> Layers { get; }

    public ImmutableArray<LumenTextureContent> Textures { get; private set; }

    /// <summary>Keeps texture dimensions and indices while dropping uploaded CPU pixels.</summary>
    public void ReleaseUploadedTexturePixels()
    {
        foreach (var layer in Layers)
            layer.Content.ReleaseDecodedTexturePixels();
        Textures = [.. Textures.Select(static texture => texture with { Rgba8 = [] })];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lifetime in _hostLifetimes.Reverse())
        {
            if (lifetime is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (lifetime is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

/// <summary>Loads immutable content and constructs isolated players for every scene layer.</summary>
public sealed class LumenGameSceneLoader : IGameSceneLoader
{
    private readonly ILumenMovieContentSource _contentSource;
    private readonly ILumenLayerHostFactory _hostFactory;
    private readonly float _stageWidth;
    private readonly float _stageHeight;

    public LumenGameSceneLoader(
        ILumenMovieContentSource contentSource,
        ILumenLayerHostFactory hostFactory,
        float stageWidth = 1280,
        float stageHeight = 720)
    {
        _contentSource = contentSource ?? throw new ArgumentNullException(nameof(contentSource));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));
        _stageWidth = stageWidth;
        _stageHeight = stageHeight;
    }

    public async ValueTask<IGameSceneInstance> LoadAsync(
        SceneDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var loaded = ImmutableArray.CreateBuilder<LoadedLumenLayer>(definition.Layers.Length);
        var players = ImmutableArray.CreateBuilder<LumenSceneLayer>(definition.Layers.Length);
        var lifetimes = ImmutableArray.CreateBuilder<object>(definition.Layers.Length);
        uint textureOffset = 0;
        using var scope = (_contentSource as IScopedLumenMovieContentSource)?.CreateSceneScope();
        var contentSource = (ILumenMovieContentSource?)scope ?? _contentSource;
        try
        {
            foreach (var layer in definition.Layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await contentSource
                    .LoadAsync(layer.ArchiveId, layer.MovieId, cancellationToken)
                    .ConfigureAwait(false);
                if (content.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    throw new InvalidDataException($"Lumen movie '{layer.MovieId}' contains semantic errors.");

                var host = _hostFactory.Create(layer)
                    ?? throw new InvalidOperationException($"Host factory returned null for '{layer.HostId}'.");
                if (host.Binding is IDisposable or IAsyncDisposable)
                    lifetimes.Add(host.Binding);
                var player = content.CreatePlayer(_stageWidth, _stageHeight, new InGameBinding(host.Binding));
                host.Initialize?.Invoke(player);
                var textureCount = checked((uint)content.Textures.Length);
                loaded.Add(new LoadedLumenLayer(layer, content, textureOffset, textureCount));
                players.Add(new LumenSceneLayer(player, layer.Transform, textureOffset, textureCount));
                textureOffset = checked(textureOffset + textureCount);
            }

            return new LumenGameSceneInstance(
                definition.Id,
                new LumenScenePlayer(_stageWidth, _stageHeight, players.MoveToImmutable()),
                loaded.MoveToImmutable(),
                lifetimes.ToImmutable());
        }
        catch
        {
            for (var index = lifetimes.Count - 1; index >= 0; index--)
            {
                var lifetime = lifetimes[index];
                if (lifetime is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (lifetime is IDisposable disposable)
                    disposable.Dispose();
            }
            throw;
        }
    }
}

/// <summary>
/// The game gives every movie a global Lumen object; movies without one run their developer mode
/// (sample data, self-start). Unregistered methods on the default object return undefined.
/// </summary>
internal sealed class InGameBinding(ILumenHostBinding? inner) : ILumenHostBinding
{
    public void Install(LumenHostContext context)
    {
        inner?.Install(context);
        if (!context.IsRegistered("Lumen"))
            context.RegisterObject("Lumen", static _ => { });
    }
}
