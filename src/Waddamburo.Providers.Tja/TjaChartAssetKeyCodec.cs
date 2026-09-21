using System.Globalization;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

internal sealed record TjaChartLocator(
    string RelativePath,
    string SourceHash,
    TaikoCourse Course,
    string Player,
    int Occurrence);

internal static class TjaChartAssetKeyCodec
{
    private const string Prefix = "chart-v1:";

    public static CatalogAssetKey Encode(
        CatalogProviderId provider,
        string relativePath,
        string sourceHash,
        TaikoCourse course,
        string player,
        int occurrence)
    {
        var path = TjaAssetKeyCodec.EncodeValue(relativePath);
        var encodedPlayer = TjaAssetKeyCodec.EncodeValue(player);
        return new CatalogAssetKey(
            provider,
            string.Join(':',
                Prefix.TrimEnd(':'),
                path,
                sourceHash,
                ((int)course).ToString(CultureInfo.InvariantCulture),
                encodedPlayer,
                occurrence.ToString(CultureInfo.InvariantCulture)));
    }

    public static bool IsChart(CatalogAssetKey asset) =>
        asset.StableId.StartsWith(Prefix, StringComparison.Ordinal);

    public static TjaChartLocator Decode(CatalogAssetKey asset)
    {
        if (!IsChart(asset))
            throw new ArgumentException("The asset key is not a custom TJA chart.", nameof(asset));
        var fields = asset.StableId.Split(':');
        if (fields.Length != 6
            || fields[0] != "chart-v1"
            || fields[2].Length != 64
            || fields[2].Any(static value => !char.IsAsciiHexDigit(value))
            || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var courseValue)
            || !Enum.IsDefined((TaikoCourse)courseValue)
            || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var occurrence)
            || occurrence <= 0)
        {
            throw new ArgumentException("The custom TJA chart asset key is malformed.", nameof(asset));
        }

        return new TjaChartLocator(
            TjaAssetKeyCodec.DecodeValue(fields[1]),
            fields[2].ToLowerInvariant(),
            (TaikoCourse)courseValue,
            TjaAssetKeyCodec.DecodeValue(fields[4]),
            occurrence);
    }
}
