namespace Waddamburo.Catalog;

/// <summary>A provider's local privacy policy, evaluated without loading or publishing chart content.</summary>
public interface IChartUploadPolicyProvider
{
    bool UploadBlocked(CatalogAssetKey chartAsset);
}

public static class ChartUploadPolicy
{
    public const string MarkerFileName = "donotupload";

    /// <summary>A marker beside the chart or in an ancestor through the library root blocks uploads.</summary>
    public static bool HasMarker(string chartPath, string libraryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var folder = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (folder is null || !(folder.Equals(root, comparison) || folder.StartsWith(prefix, comparison)))
            return true; // Unresolvable/outside the library: never grant upload permission.
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder, MarkerFileName))) return true;
            if (folder.Equals(root, comparison)) break;
            folder = Path.GetDirectoryName(folder);
        }
        return false;
    }
}
