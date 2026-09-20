namespace Waddamburo.Game.Flow;

/// <summary>A stable product-owned identifier for a composed game scene.</summary>
public sealed record SceneId
{
    public SceneId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
