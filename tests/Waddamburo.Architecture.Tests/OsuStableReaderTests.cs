using System.Security.Cryptography;
using System.Text;
using Waddamburo.Providers.OsuLazer;

namespace Waddamburo.Architecture.Tests;

/// <summary>A synthetic osu!stable install (osu!.db, collection.db, Songs/) read like a lazer library.</summary>
public sealed class OsuStableReaderTests
{
    private const string Osu = """
        osu file format v14

        [General]
        Mode: 1

        [Difficulty]
        OverallDifficulty:5
        SliderMultiplier:1.4

        [TimingPoints]
        0,500,4,1,0,100,1,0

        [HitObjects]
        256,192,0,1,0,0:0:0:0:
        256,192,500,1,2,0:0:0:0:
        """;

    [Fact]
    public async Task AStableInstallListsItsTaikoMapsAndCollections()
    {
        var root = Path.Combine(Path.GetTempPath(), "wdb-stable-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Songs", "123 Artist - Song"));
            File.WriteAllText(Path.Combine(root, "Songs", "123 Artist - Song", "oni.osu"), Osu);
            File.WriteAllBytes(Path.Combine(root, "Songs", "123 Artist - Song", "audio.mp3"), [1, 2, 3]);
            writeDb(Path.Combine(root, "osu!.db"));
            writeCollections(Path.Combine(root, "collection.db"));

            Assert.Equal(root, OsuLazerCatalogProvider.FindStableFolder(root));
            Assert.Null(OsuLazerCatalogProvider.FindDataFolder(root)); // not a lazer folder
            var provider = new OsuLazerCatalogProvider(root);
            Assert.Equal("osu-stable", provider.Id.Value);
            Assert.Equal(Waddamburo.Catalog.SongSourceKind.OsuStable, provider.Source);
            var contribution = await provider.ScanAsync(null, CancellationToken.None);

            var song = Assert.Single(contribution.Songs); // the osu!standard map is left out
            Assert.Equal(Waddamburo.Catalog.SongSourceKind.OsuStable, song.Key.Source); // its own library
            Assert.Equal("ソング", song.Title.Primary);
            Assert.Equal("Song", song.Title.English);
            Assert.Equal(TimeSpan.FromMilliseconds(1234), song.PreviewStart);
            Assert.Equal(120, song.Bpm);
            var chart = Assert.Single(song.Charts);
            Assert.Equal(4, chart.Level); // taiko stars 4.2 with no mods
            // The chart id is the .osu file's SHA-256, as lazer names it.
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Osu))), chart.Key.StableId);
            Assert.Equal(2, (await provider.LoadChartAsync(chart.Key, chart.ChartAsset)).HitObjects.Length);
            Assert.False(provider.UploadBlocked(chart.ChartAsset));
            var marker = Path.Combine(root, "Songs", "123 Artist - Song", "donotupload");
            File.WriteAllText(marker, "");
            Assert.True(provider.UploadBlocked(chart.ChartAsset));
            File.Delete(marker);
            Assert.False(provider.UploadBlocked(chart.ChartAsset));
            File.WriteAllText(Path.Combine(root, "donotupload"), "");
            Assert.True(provider.UploadBlocked(chart.ChartAsset));
            await using (var audio = await provider.OpenReadAsync(song.AudioAsset!))
                Assert.Equal(3, audio.Length);
            Assert.Contains(contribution.Categories, category => category.Name == "Favourites" && category.Songs.Single() == song.Key);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // osu!.db at version 20250107 (Int-Float star pairs): a taiko map and an osu!standard one.
    private static void writeDb(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(20250108);
        writer.Write(1); // folders
        writer.Write(true);
        writer.Write(0L);
        str(writer, "player");
        writer.Write(2);
        beatmap(writer, mode: 1, "taiko-md5", "oni.osu");
        beatmap(writer, mode: 0, "std-md5", "std.osu");
        writer.Write(0); // permissions
    }

    private static void beatmap(BinaryWriter writer, byte mode, string md5, string file)
    {
        foreach (var text in new[] { "Artist", "アーティスト", "Song", "ソング", "Mapper", "Oni", "audio.mp3", md5, file })
            str(writer, text);
        writer.Write((byte)4);
        writer.Write(new byte[6]);
        writer.Write(new DateTime(2024, 1, 1).Ticks);
        writer.Write(new byte[16]); // AR CS HP OD
        writer.Write(1.4d);
        for (var ruleset = 0; ruleset < 4; ruleset++)
        {
            writer.Write(2);
            foreach (var (mods, stars) in new[] { (0, ruleset == 1 ? 4.2f : 9f), (64, 7f) })
            {
                writer.Write((byte)0x08);
                writer.Write(mods);
                writer.Write((byte)0x0c);
                writer.Write(stars);
            }
        }
        writer.Write(60); // drain
        writer.Write(61000); // total
        writer.Write(1234); // preview
        writer.Write(1);
        writer.Write(500d); // ms per beat
        writer.Write(0d);
        writer.Write(true);
        writer.Write(42); // difficulty id
        writer.Write(123); // set id
        writer.Write(0);
        writer.Write(new byte[4]);
        writer.Write((short)0);
        writer.Write(0.7f);
        writer.Write(mode);
        str(writer, "");
        str(writer, "");
        writer.Write((short)0);
        str(writer, "");
        writer.Write(true);
        writer.Write(0L);
        writer.Write(false);
        str(writer, "123 Artist - Song");
        writer.Write(0L);
        writer.Write(new byte[5]);
        writer.Write(0);
        writer.Write((byte)0);
    }

    private static void writeCollections(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(20250108);
        writer.Write(1);
        str(writer, "Favourites");
        writer.Write(1);
        str(writer, "taiko-md5");
    }

    private static void str(BinaryWriter writer, string text)
    {
        if (text.Length == 0)
        {
            writer.Write((byte)0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((byte)0x0b);
        for (var length = (uint)bytes.Length; ; length >>= 7)
        {
            writer.Write((byte)(length < 0x80 ? length : (length & 0x7f) | 0x80));
            if (length < 0x80) break;
        }
        writer.Write(bytes);
    }
}
