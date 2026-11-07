namespace Waddamburo.Catalog;

/// <summary>Optional local authoring destination. Only the owning provider interprets its opaque chart asset key.</summary>
public interface IChartModFileProvider
{
    string? ModchartPath(CatalogAssetKey chartAsset);
}
