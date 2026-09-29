using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock.Tests;

public sealed class NijiiroCatalogProviderTests
{
    [Fact]
    public async Task ScanDecryptsTablesWithTheLoadersKeyAndListsInstalledCourses()
    {
        var install = Directory.CreateTempSubdirectory("nijiiro-test").FullName;
        try
        {
            var data = Path.Combine(install, "Data", "x64");
            Directory.CreateDirectory(Path.Combine(data, "datatable"));
            Directory.CreateDirectory(Path.Combine(data, "fumen", "abc"));
            Directory.CreateDirectory(Path.Combine(data, "sound"));
            Directory.CreateDirectory(Path.Combine(install, "Executable", "Release"));
            var key = RandomNumberGenerator.GetBytes(32);
            // The loader holds the key as a hex literal among other bytes.
            File.WriteAllBytes(Path.Combine(install, "Executable", "Release", "bnusio.dll"),
                [.. "MZ\0junk"u8, .. Encoding.ASCII.GetBytes(Convert.ToHexString(key)), 0]);
            File.WriteAllBytes(Path.Combine(data, "datatable", "musicinfo.bin"), encrypt(key, gzip(
                """{"items":[{"id":"abc","genreNo":3,"starEasy":2,"starMania":8},{"id":"nosound","genreNo":0}]}""")));
            File.WriteAllBytes(Path.Combine(data, "datatable", "wordlist.bin"), gzip(
                """{"items":[{"key":"song_abc","japaneseText":"テスト","englishUsText":"Test"}]}"""));
            File.WriteAllBytes(Path.Combine(data, "fumen", "abc", "abc_e.bin"), []);
            File.WriteAllBytes(Path.Combine(data, "fumen", "abc", "abc_m.bin"), []);
            File.WriteAllBytes(Path.Combine(data, "sound", "song_abc.nus3bank"), []);

            var provider = new NijiiroCatalogProvider(install);
            Assert.True(provider.Exists);
            var contribution = await provider.ScanAsync(null, default);

            var song = Assert.Single(contribution.Songs);
            Assert.Equal("テスト", song.Title.Japanese);
            Assert.Equal("Test", song.Title.English);
            Assert.Equal([TaikoCourse.Easy, TaikoCourse.Oni], song.Charts.Select(static chart => chart.Course!.Value));
            Assert.Equal([2, 8], song.Charts.Select(static chart => chart.Level!.Value));
            Assert.Equal("Vocaloid", Assert.Single(contribution.Categories).Name);

            // Usable with the loader's key; without the loader nothing decrypts, and it says so.
            Assert.Null(provider.Problem);
            File.Delete(Path.Combine(install, "Executable", "Release", "bnusio.dll"));
            Assert.Contains("bnusio.dll", new NijiiroCatalogProvider(install).Problem);
            Assert.Equal("No Nijiiro data there.", new NijiiroCatalogProvider(Path.Combine(install, "nothing")).Problem);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    private static byte[] gzip(string text)
    {
        using var output = new MemoryStream();
        using (var stream = new GZipStream(output, CompressionMode.Compress))
            stream.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static byte[] encrypt(byte[] key, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.PKCS7)];
    }
}
