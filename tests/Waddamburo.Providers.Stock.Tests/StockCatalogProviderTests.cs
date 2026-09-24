using System.Buffers.Binary;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock.Tests;

public sealed class StockCatalogProviderTests
{
    [Fact]
    public async Task ScanListsInstalledSongsInMetadataOrderByGenre()
    {
        using var data = new TemporaryData();
        data.WriteMusicInfo("config/REV/musicinfo.xml",
            ("beta", "ベータ", "アニメ"), ("alpha", "アルファ", "J-POP"), ("gamma", "ガンマ", "アニメ"), ("absent", "なし", "J-POP"));
        data.WriteMusicInfo("musicinfo.xml", ("alpha", "Old", "J-POP")); // older revision: fewer installed songs
        foreach (var (id, course) in new[] { ("alpha", 'm'), ("alpha", 'x'), ("beta", 'e'), ("gamma", 'n') })
            data.WriteBytes($"fumen/{id}/solo/{id}_{course}.bin", Fumen(new Measure(120, 0, [N(1, 0)])));
        data.WriteBytes("sound/bgm/nub/SONG_ALPHA.nub", [1, 2, 3]);
        data.WriteBytes("sound/bgm/nsh/SONG_ALPHA.nsh", Nsh(previewMs: 48_500));

        var provider = new StockCatalogProvider(data.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        Assert.Equal(["beta", "alpha", "gamma"], contribution.Songs.Select(static song => song.Key.StableId));
        Assert.Collection(contribution.Categories,
            category => { Assert.Equal("Anime", category.Name); Assert.Equal(2, category.Songs.Length); },
            category => { Assert.Equal("J-POP", category.Name); Assert.Single(category.Songs); });
        var alpha = contribution.Songs[1];
        Assert.Equal("アルファ", alpha.Title.Primary);
        Assert.Equal([TaikoCourse.Oni, TaikoCourse.Ura], alpha.Charts.Select(static chart => chart.Course!.Value));
        Assert.Equal(TimeSpan.FromMilliseconds(48_500), alpha.PreviewStart);
        await using var audio = await provider.OpenReadAsync(alpha.AudioAsset!);
        Assert.Equal(3, audio.Length);
        Assert.Null(contribution.Songs[0].AudioAsset);
        Assert.Contains(contribution.Diagnostics, static diagnostic => diagnostic.Code == "STOCK_AUDIO_MISSING");
    }

    [Fact]
    public async Task ChartConvertsMeasuresNotesAndLongNotes()
    {
        using var data = new TemporaryData();
        data.WriteMusicInfo("musicinfo.xml", ("song", "曲", "J-POP"));
        // 120 BPM: a measure lasts 2000 ms and is hit 2000 ms after its offset.
        data.WriteBytes("fumen/song/solo/song_m.bin", Fumen(
            new Measure(120, 0, [N(1, 0), N(4, 500), N(7, 1000), N(6, 1500, 250)]),
            new Measure(120, 2000, [N(0xA, 0, 400, 5)], GoGo: true, Speed: 1.5f)));
        var provider = new StockCatalogProvider(data.Path);
        var chart = Assert.Single((await provider.ScanAsync(null, CancellationToken.None)).Songs[0].Charts);

        var playable = await provider.LoadChartAsync(chart.Key, chart.ChartAsset);

        Assert.Equal(TimeSpan.Zero, playable.AuthoredOffset);
        Assert.Equal([2000d, 2500, 3000], playable.HitObjects.Select(static note => note.StartTime.TotalMilliseconds));
        Assert.Equal([PlayableNoteKind.Don, PlayableNoteKind.Ka, PlayableNoteKind.BigDon], playable.HitObjects.Select(static note => note.Kind));
        Assert.Collection(playable.LongNotes,
            roll => { Assert.Equal(PlayableLongNoteKind.Roll, roll.Kind); Assert.Equal(3750, roll.EndTime.TotalMilliseconds); },
            balloon => { Assert.Equal(PlayableLongNoteKind.Balloon, balloon.Kind); Assert.Equal(5, balloon.RequiredHits); Assert.Equal(4000, balloon.StartTime.TotalMilliseconds); });
        Assert.Equal([false, true], playable.EffectPoints.Select(static point => point.IsGoGo));
        Assert.Equal([1d, 1.5], playable.ScrollPoints.Select(static point => point.Multiplier));
        Assert.Equal(120, Assert.Single(playable.TimingPoints).BeatsPerMinute);
        Assert.Equal(6000, playable.Duration.TotalMilliseconds);
        Assert.Equal(1000, playable.ScoreInit);
        Assert.Equal(25, playable.ScoreDiff);
    }

    [Fact]
    public async Task EarlyFirstMeasureShiftsChartTimeThroughTheAuthoredOffset()
    {
        using var data = new TemporaryData();
        data.WriteMusicInfo("musicinfo.xml", ("song", "曲", "J-POP"));
        data.WriteBytes("fumen/song/solo/song_e.bin", Fumen(new Measure(120, -2500, [N(1, 1000)])));
        var provider = new StockCatalogProvider(data.Path);
        var chart = Assert.Single((await provider.ScanAsync(null, CancellationToken.None)).Songs[0].Charts);

        var playable = await provider.LoadChartAsync(chart.Key, chart.ChartAsset);

        // Measure hit at audio -500 ms, note at audio 500 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(500), playable.AuthoredOffset);
        Assert.Equal(1000, Assert.Single(playable.HitObjects).StartTime.TotalMilliseconds);
    }

    [Theory]
    [InlineData("chart:../x:m")]
    [InlineData("chart:song:q")]
    [InlineData("audio:song/../../etc")]
    public async Task MalformedAssetKeysAreRejected(string stableId)
    {
        using var data = new TemporaryData();
        var provider = new StockCatalogProvider(data.Path);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.OpenReadAsync(new CatalogAssetKey(provider.Id, stableId)));
    }

    [Fact]
    public void PreviewCueNeedsTheStreamEntry()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), StockCatalogProvider.PreviewStart(Nsh(12_000)));
        var broken = Nsh(12_000);
        broken[0x30] = 0; // not an "at3" entry
        Assert.Null(StockCatalogProvider.PreviewStart(broken));
        Assert.Null(StockCatalogProvider.PreviewStart(new byte[8]));
    }

    [Fact]
    public void StarsComeFromCourseEntriesAndTheUraRecord()
    {
        var stars = StockCatalogProvider.ReadStars(Tuning(
            ("song", [("song1p_e", 3), ("song1p_m", 8), ("song2p_m", 9)]),
            ("ex_song", [("ex_song1p_m", 10)])));

        Assert.Equal([3, 0, 0, 8, 10], stars["song"]);
    }

    // tuning.bin: count, 0x90C-byte records (three string offsets, then 0x80-byte course
    // entries of name offset + stars), then the string pool.
    private static byte[] Tuning(params (string Id, (string Name, int Stars)[] Courses)[] records)
    {
        var pool = new MemoryStream();
        uint add(string text)
        {
            var offset = (uint)pool.Length;
            pool.Write(System.Text.Encoding.UTF8.GetBytes(text + "\0"));
            return offset;
        }
        var data = new byte[4 + records.Length * 0x90C];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)records.Length);
        for (var index = 0; index < records.Length; index++)
        {
            var record = data.AsSpan(4 + index * 0x90C);
            BinaryPrimitives.WriteUInt32BigEndian(record, add(records[index].Id));
            for (var course = 0; course < 18; course++)
            {
                var entry = record[(12 + course * 0x80)..];
                BinaryPrimitives.WriteUInt32BigEndian(entry, uint.MaxValue);
                BinaryPrimitives.WriteUInt32BigEndian(entry[4..], uint.MaxValue);
            }
            var courses = records[index].Courses;
            for (var course = 0; course < courses.Length; course++)
            {
                var entry = record[(12 + course * 0x80)..];
                BinaryPrimitives.WriteUInt32BigEndian(entry, add(courses[course].Name));
                BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)courses[course].Stars);
            }
        }
        return [.. data, .. pool.ToArray()];
    }

    private sealed record Measure(float Bpm, float Offset, (int Type, float Position, float Duration, int Hits)[] Notes, bool GoGo = false, float Speed = 1);

    private static (int, float, float, int) N(int type, float position, float duration = 0, int hits = 0) => (type, position, duration, hits);

    // Big-endian fumen: 520-byte header (measure count at 0x200), 40-byte measures, three
    // branches (normal carries the notes), 24-byte notes plus 8 bytes after rolls.
    private static byte[] Fumen(params Measure[] measures)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4];
        void i32(int value) { BinaryPrimitives.WriteInt32BigEndian(buffer, value); stream.Write(buffer); }
        void u16(int value) { BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value); stream.Write(buffer, 0, 2); }
        void f32(float value) => i32(BitConverter.SingleToInt32Bits(value));
        stream.Write(new byte[0x200]);
        i32(measures.Length);
        i32(0);
        foreach (var measure in measures)
        {
            f32(measure.Bpm);
            f32(measure.Offset);
            stream.WriteByte(measure.GoGo ? (byte)1 : (byte)0);
            stream.WriteByte(1);
            stream.Write(new byte[2 + 24 + 4]);
            for (var branch = 0; branch < 3; branch++)
            {
                u16(measure.Notes.Length);
                u16(0);
                f32(measure.Speed);
                foreach (var (type, position, duration, hits) in measure.Notes)
                {
                    i32(type);
                    f32(position);
                    i32(0);
                    f32(0);
                    u16(type is 0xA or 0xC ? hits : 1000);
                    u16(type is 0xA or 0xC ? 0 : 100);
                    f32(duration);
                    if (type is 0x6 or 0x9 or 0x62)
                        stream.Write(new byte[8]);
                }
            }
        }
        return stream.ToArray();
    }

    private static byte[] Nsh(uint previewMs)
    {
        var nsh = new byte[0x100];
        BinaryPrimitives.WriteUInt32BigEndian(nsh, 0x00020100);
        BinaryPrimitives.WriteUInt32BigEndian(nsh.AsSpan(0x18), 0x20); // table
        BinaryPrimitives.WriteUInt32BigEndian(nsh.AsSpan(0x20), 0x30); // first entry
        BinaryPrimitives.WriteUInt32BigEndian(nsh.AsSpan(0x30), 0x61743300); // "at3\0"
        BinaryPrimitives.WriteUInt32BigEndian(nsh.AsSpan(0x30 + 0x90), 20);
        BinaryPrimitives.WriteUInt32BigEndian(nsh.AsSpan(0x30 + 0xB0), previewMs);
        return nsh;
    }

    private sealed class TemporaryData : IDisposable
    {
        public TemporaryData()
        {
            Path = Directory.CreateTempSubdirectory("waddamburo-stock-").FullName;
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "fumen"));
        }

        public string Path { get; }

        public void WriteBytes(string relative, byte[] bytes)
        {
            var path = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void WriteMusicInfo(string relative, params (string Id, string Title, string Genre)[] songs)
        {
            var data = string.Concat(songs.Select(static song =>
                $"<Data><musicid>{song.Id}</musicid><musicname>{song.Title}</musicname><genrename>{song.Genre}</genrename></Data>"));
            WriteBytes(relative, System.Text.Encoding.UTF8.GetBytes(
                $"""<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE boost_serialization><boost_serialization><MusicInfo>{data}</MusicInfo></boost_serialization>"""));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
