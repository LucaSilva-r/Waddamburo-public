using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Formats.Audio;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>
/// Custom songs turned down to stock's loudness (osu! songs are mastered up to 6 dB louder): each one's integrated
/// loudness is measured once, decoding the whole file, and kept in the cache folder. Stock songs play at their banks' authored gain.
/// Only ever turns down: songs peak near full scale, so turning a quiet one up would clip.
/// </summary>
internal static class SongLoudness
{
    // Green's songs as the game plays them: the median of 40 stock SONG_*.nub is -10.9 LUFS raw, -13.0 with each bank's
    // authored gain (spread -16.5..-8.2); osu! songs measured -5.4..-12.3 (Idola no Circus -0.9, peaks +6.4 dBFS).
    // Matched by measure: a clipped low-bitrate mp3 can still sound a little smaller at the same level (user, by ear).
    private const double Target = -13;

    /// <summary>The cache file (set at startup); null keeps measurements for this run only.</summary>
    public static string? CacheFile { get; set; }

    private static readonly Lazy<ConcurrentDictionary<string, double>> Measured = new(load);
    private static readonly object SaveGate = new();

    /// <summary>The volume to play <paramref name="asset"/> at; measures it first when it never was (blocking).</summary>
    public static float Volume(CatalogAssetRouter assets, CatalogAssetKey asset, SdlAudioFormat format,
        CancellationToken cancellationToken = default)
    {
        if (asset.Provider.Value == "stock")
            return stockGain(assets, asset, cancellationToken);
        // ponytail: keyed by the provider's id, so a TJA whose audio file is swapped keeps its old level; clear the cache then.
        var key = $"{asset.Provider.Value}/{asset.StableId}";
        if (!Measured.Value.TryGetValue(key, out var lufs))
        {
            try
            {
                lufs = measure(assets, asset, format, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Song loudness unavailable, playing as is: {exception.Message}");
                return 1f;
            }
            Measured.Value[key] = lufs;
            save();
        }
        return (float)Math.Clamp(Math.Pow(10, (Target - lufs) / 20), 0.05, 1);
    }

    // A stock song plays at its bank's authored gain, as the game plays it (SONG_*.nub's at3 header; 1 if unreadable).
    private static float stockGain(CatalogAssetRouter assets, CatalogAssetKey asset, CancellationToken cancellationToken)
    {
        try
        {
            using var input = assets.OpenReadAsync(asset, cancellationToken).AsTask().GetAwaiter().GetResult();
            var header = new byte[4096];
            var length = 0;
            while (length < header.Length && input.Read(header, length, header.Length - length) is var read and > 0)
                length += read;
            return Math.Min(1f, NubCueDelay.ReadGain(header.AsSpan(0, length), 0));
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Stock song gain unreadable, playing as is: {exception.Message}");
            return 1f;
        }
    }

    private static double measure(CatalogAssetRouter assets, CatalogAssetKey asset, SdlAudioFormat format,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var input = assets.OpenReadAsync(asset, cancellationToken).AsTask().GetAwaiter().GetResult();
        using var decoder = new NativeAudioDecoder(input, checked((uint)format.SampleRate), checked((uint)format.Channels));
        var meter = new LoudnessMeter(format.SampleRate, format.Channels);
        var block = new float[16384 * format.Channels];
        int read;
        while ((read = decoder.Read(block)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            meter.Add(block.AsSpan(0, read));
        }
        var lufs = meter.Integrated;
        Console.WriteLine($"Song loudness {lufs:0.0} LUFS ({watch.Elapsed.TotalMilliseconds:0} ms): {asset.StableId}");
        return lufs;
    }

    // One "lufs<TAB>key" line per song.
    private static ConcurrentDictionary<string, double> load()
    {
        var measured = new ConcurrentDictionary<string, double>();
        try
        {
            if (CacheFile is { } file && File.Exists(file))
                foreach (var line in File.ReadLines(file))
                    if (line.Split('\t', 2) is [var lufs, var key]
                        && double.TryParse(lufs, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        measured[key] = value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Song loudness cache unreadable, measuring again: {exception.Message}");
        }
        return measured;
    }

    private static void save()
    {
        if (CacheFile is not { } file)
            return;
        lock (SaveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllLines(file + ".tmp", Measured.Value.Select(static pair =>
                    $"{pair.Value.ToString("R", CultureInfo.InvariantCulture)}\t{pair.Key}"));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Song loudness cache not saved: {exception.Message}");
            }
        }
    }
}
