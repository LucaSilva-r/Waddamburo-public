using System.Collections.Immutable;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

/// <summary>
/// Versioned, source-independent description of one composed scene. A native-only
/// scene may have no authored movie layers.
/// </summary>
public sealed record SceneDefinition
{
    public const int CurrentVersion = 1;

    public SceneDefinition(
        int version,
        SceneId id,
        IEnumerable<SceneLayerDefinition> layers)
    {
        if (version != CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(version), version, $"Only scene definition version {CurrentVersion} is supported.");
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(layers);
        var materialized = layers.ToImmutableArray();
        if (materialized.Any(static layer => layer is null))
            throw new ArgumentException("Scene layers cannot contain null entries.", nameof(layers));

        Version = version;
        Id = id;
        Layers = materialized;
    }

    public int Version { get; }

    public SceneId Id { get; }

    public ImmutableArray<SceneLayerDefinition> Layers { get; }
}

/// <summary>One ordered Lumen layer and its product-owned loading/host keys.</summary>
public sealed record SceneLayerDefinition
{
    public SceneLayerDefinition(
        string archiveId,
        string movieId,
        LumenMatrix transform,
        string hostId,
        string? anchorId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(movieId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        if (anchorId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        if (!isFinite(transform))
            throw new ArgumentOutOfRangeException(nameof(transform), "Layer transform must be finite.");

        ArchiveId = archiveId;
        MovieId = movieId;
        Transform = transform;
        HostId = hostId;
        AnchorId = anchorId;
    }

    public string ArchiveId { get; }

    public string MovieId { get; }

    public LumenMatrix Transform { get; }

    public string HostId { get; }

    public string? AnchorId { get; }

    private static bool isFinite(LumenMatrix value) =>
        float.IsFinite(value.M11)
        && float.IsFinite(value.M12)
        && float.IsFinite(value.M21)
        && float.IsFinite(value.M22)
        && float.IsFinite(value.X)
        && float.IsFinite(value.Y);
}
