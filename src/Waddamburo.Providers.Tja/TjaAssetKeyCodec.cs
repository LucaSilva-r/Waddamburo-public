using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

internal static class TjaAssetKeyCodec
{
    private const string Prefix = "relative:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CatalogAssetKey Encode(CatalogProviderId provider, string relativePath)
    {
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new CatalogAssetKey(provider, Prefix + encoded);
    }

    public static string Decode(CatalogAssetKey asset)
    {
        if (!asset.StableId.StartsWith(Prefix, StringComparison.Ordinal))
            throw new ArgumentException("The asset key is not a custom TJA relative asset.", nameof(asset));
        var encoded = asset.StableId[Prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("The custom TJA asset key is malformed.", nameof(asset), exception);
        }
    }
}
