using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock.Tests;

public sealed class RhythmFestivalCatalogProviderTests
{
    // Synthetic secrets: the provider must find them in the binaries, not know them.
    private const string Password = "synthetic-password-0123456789ab_";
    private const string Salt = "synthetic-salt-abcdefghijklmnop_";
    private const int KeyIterations = 1234;
    private const int IvIterations = 567;

    [Fact]
    public async Task ScanFindsTheCipherInTheBinariesAndListsLooseAndPackedSongs()
    {
        var install = Directory.CreateTempSubdirectory("rhythm-festival-test").FullName;
        try
        {
            var data = Path.Combine(install, "Taiko no Tatsujin Rhythm Festival_Data");
            var assets = Path.Combine(data, "StreamingAssets");
            var downloads = Path.Combine(install, "downloads");
            foreach (var folder in (string[])[Path.Combine(data, "il2cpp_data", "Metadata"), Path.Combine(assets, "ReadAssets"),
                Path.Combine(assets, "sound"), Path.Combine(assets, "fumen"), downloads])
                Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat"),
                metadata(["not base64 at all", b64("decoy-string-that-opens-nothing!"), b64(Password), b64(Salt)]));
            File.WriteAllBytes(Path.Combine(install, "GameAssembly.dll"), assembly(KeyIterations, IvIterations));
            File.WriteAllBytes(Path.Combine(assets, "ReadAssets", "musicinfo.bin"), encrypt("musicinfo", gzip(
                """
                {"items":[
                  {"Id":"loose","GenreNo":2,"StarEasy":3,"SongNameJP":"<font=jp>テスト","SongNameEN":"<font=efigs>Test","SongSubEN":"<font=efigs>"},
                  {"Id":"packed","GenreNo":7,"StarMania":9,"BranchMania":true,"SongNameEN":"<font=efigs>Packed"},
                  {"Id":"absent","GenreNo":0,"StarEasy":1},
                  {"Id":"debug","GenreNo":0,"Debug":true}
                ]}
                """)));
            File.WriteAllBytes(Path.Combine(assets, "sound", "SONG_LOOSE.bin"), encrypt("SONG_LOOSE", [1, 2, 3]));
            File.WriteAllBytes(Path.Combine(assets, "fumen", "loose_e.bin"), encrypt("loose_e", [0]));
            File.WriteAllBytes(Path.Combine(assets, "sound", "SONG_DEBUG.bin"), encrypt("SONG_DEBUG", [0]));
            File.WriteAllBytes(Path.Combine(assets, "fumen", "debug_e.bin"), encrypt("debug_e", [0]));
            // Version 2 replaces version 1; a preview-only package is ignored.
            zip(Path.Combine(downloads, "0002_trail_001.zip"), ("SONG_PACKED.bin", [9]), ("packed_e.bin", [0]));
            zip(Path.Combine(downloads, "0002_trail_002.zip"), ("SONG_PACKED.bin", encrypt("SONG_PACKED", [4, 5])), ("packed_m.bin", [0]));
            zip(Path.Combine(downloads, "0002_trail_stream_001.zip"), ("PSONG_PACKED.bin", [0]));
            // A song only the server's song data knows; for one musicinfo has too, musicinfo's entry wins.
            zip(Path.Combine(downloads, "0003_trail_001.zip"), ("SONG_SERVER.bin", [0]), ("server_h.bin", [0]));
            Directory.CreateDirectory(Path.Combine(install, "ServerDataCacheSteam"));
            File.WriteAllBytes(Path.Combine(install, "ServerDataCacheSteam", "SongdataDetails"), serverEncrypt(
                """
                {"ary_song_datail":[
                  {"song_uid":2,"song_id":"packed","genre_id":0,"star_4":1,"song_name_en":"<font=efigs>Old"},
                  {"song_uid":3,"song_id":"server","genre_id":1,"star_3":6,"is_branch_3":true,"song_name_ja":"<font=jp>サーバー","song_name_en":"<font=efigs>Server","song_name_sub_en":"<font=efigs>Sub"}
                ]}
                """));

            var cache = Path.Combine(install, "cache");
            var provider = new RhythmFestivalCatalogProvider(install, cache, downloads);
            Assert.True(provider.Exists);
            Assert.Null(provider.Problem);
            var contribution = await provider.ScanAsync(null, default);

            Assert.Equal(["loose", "packed", "server"], contribution.Songs.Select(static song => song.Key.StableId));
            var loose = contribution.Songs[0];
            Assert.Equal("テスト", loose.Title.Japanese);
            Assert.Equal("Test", loose.Title.English);
            Assert.Null(loose.EnglishSubtitle);
            Assert.Equal(3, Assert.Single(loose.Charts).Level);
            var packed = contribution.Songs[1];
            var oni = Assert.Single(packed.Charts);
            Assert.Equal(TaikoCourse.Oni, oni.Course);
            Assert.True(oni.HasBranches);
            Assert.Equal(9, oni.Level);
            Assert.Equal("Packed", packed.Title.English);
            var server = contribution.Songs[2];
            Assert.Equal("サーバー", server.Title.Japanese);
            Assert.Equal("Sub", server.EnglishSubtitle);
            var hard = Assert.Single(server.Charts);
            Assert.Equal((TaikoCourse.Hard, 6, true), (hard.Course, hard.Level, hard.HasBranches));
            Assert.Equal(["Anime", "Vocaloid", "Namco Original"], contribution.Categories.Select(static category => category.Name));

            // The music is decrypted into the cache.
            await using (var music = await provider.OpenReadAsync(packed.AudioAsset!))
            {
                Assert.EndsWith(".acb", ((FileStream)music).Name, StringComparison.Ordinal);
                Assert.Equal(new byte[] { 4, 5 }, ReadAll(music));
            }
            Assert.Single(Directory.GetFiles(cache));
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void ProblemSaysWhenTheBinariesHoldNothingThatOpensTheSongTable()
    {
        var install = Directory.CreateTempSubdirectory("rhythm-festival-test").FullName;
        try
        {
            var data = Path.Combine(install, "Taiko no Tatsujin Rhythm Festival_Data");
            Directory.CreateDirectory(Path.Combine(data, "il2cpp_data", "Metadata"));
            Directory.CreateDirectory(Path.Combine(data, "StreamingAssets", "ReadAssets"));
            File.WriteAllBytes(Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat"), metadata([b64(Password), b64(Salt)]));
            File.WriteAllBytes(Path.Combine(install, "GameAssembly.dll"), assembly(KeyIterations + 1, IvIterations));
            File.WriteAllBytes(Path.Combine(data, "StreamingAssets", "ReadAssets", "musicinfo.bin"), encrypt("musicinfo", gzip("{\"items\":[]}")));

            Assert.Equal("No parameters in the installation's binaries open its song table.", new RhythmFestivalCatalogProvider(install).Problem);
            Assert.Equal("No Rhythm Festival installation there.", new RhythmFestivalCatalogProvider(Path.Combine(install, "nothing")).Problem);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void VdfValuesReadsLibraryPathsAndInstallDirectories()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/home/me/games\"\n\t}\n}\n");
            Assert.Equal([@"D:\SteamLibrary", "/home/me/games"], RhythmFestivalCatalogProvider.VdfValues(file, "path"));
            Assert.Empty(RhythmFestivalCatalogProvider.VdfValues(file + ".missing", "path"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string b64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    // il2cpp metadata with only a string literal table: header (sanity, version, table offset/size, data offset/size).
    private static byte[] metadata(string[] literals)
    {
        var table = new MemoryStream();
        var data = new MemoryStream();
        foreach (var literal in literals)
        {
            var bytes = Encoding.ASCII.GetBytes(literal);
            table.Write(BitConverter.GetBytes(bytes.Length));
            table.Write(BitConverter.GetBytes((int)data.Length));
            data.Write(bytes);
        }
        const int header = 0x100;
        var output = new MemoryStream();
        output.Write(BitConverter.GetBytes(0xFAB11BAF));
        output.Write(BitConverter.GetBytes(29));
        output.Write(BitConverter.GetBytes(header));
        output.Write(BitConverter.GetBytes((int)table.Length));
        output.Write(BitConverter.GetBytes(header + (int)table.Length));
        output.Write(BitConverter.GetBytes((int)data.Length));
        output.Write(new byte[header - output.Length]);
        output.Write(table.ToArray());
        output.Write(data.ToArray());
        return output.ToArray();
    }

    // Code bytes holding the two masked stores (mov dword [rcx+0x80], imm32 ... mov dword [rcx+0x84], imm32).
    private static byte[] assembly(int keyIterations, int ivIterations) =>
    [
        .. "MZ\0code"u8,
        0xC7, 0x81, 0x80, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(keyIterations ^ 0xFFFFFF),
        0x48, 0x8B, 0x05, 0x00, 0x00, 0x00, 0x00,
        0xC7, 0x81, 0x84, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(ivIterations ^ 0xFFFFF),
        0xC3,
    ];

    private static byte[] encrypt(string name, byte[] plain)
    {
        var suffix = Encoding.UTF8.GetBytes(name);
        byte[] password = [.. Encoding.UTF8.GetBytes(Password), .. suffix], salt = [.. Encoding.UTF8.GetBytes(Salt), .. suffix];
        using var aes = Aes.Create();
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KeyIterations, HashAlgorithmName.SHA256, 32);
        return aes.EncryptCbc(plain, Rfc2898DeriveBytes.Pbkdf2(password, salt, IvIterations, HashAlgorithmName.SHA256, 16), PaddingMode.PKCS7);
    }

    // The server data cache's fixed cipher (see RhythmFestivalKeys.DecryptServerCache): UTF-16 text.
    private static byte[] serverEncrypt(string text)
    {
        var stream = Rfc2898DeriveBytes.Pbkdf2("xbox"u8, "taikoxboxdepelop"u8, 1000, HashAlgorithmName.SHA1, 48);
        using var aes = Aes.Create();
        aes.Key = stream[..32];
        return aes.EncryptCbc(Encoding.Unicode.GetBytes(text), stream.AsSpan(32), PaddingMode.PKCS7);
    }

    private static byte[] gzip(string text)
    {
        using var output = new MemoryStream();
        using (var stream = new GZipStream(output, CompressionMode.Compress))
            stream.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static void zip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(data);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
