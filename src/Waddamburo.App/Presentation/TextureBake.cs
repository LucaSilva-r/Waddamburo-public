using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Waddamburo.App.Scenes;
using Waddamburo.Formats.Ddp;
using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Upscales every texture the game uses into the cache, in the background: from the Settings menu
/// (Bake All Textures) or --upscale-textures. Only movies the game uses: every one it has loaded
/// (used.txt) plus the fixed scenes and every gameplay part a random mix may pick; Green also
/// carries older versions' assets, so the archives are never swept wholesale. A scan first finds the
/// textures missing from the cache (so the progress and time left are real), then only their
/// archives are decoded again and upscaled. It can be stopped at any time; finished textures stay.
/// ponytail: songs' themed skins come in through used.txt once played; list them from the catalog
/// if a first bake should cover them.
/// </summary>
internal sealed class TextureBake
{
    public enum BakePhase { Scanning, Upscaling, Done, Stopped, Failed }

    private static readonly int[] Sides = [0, 1];
    private static readonly bool[] PlayerCounts = [false, true];
    private volatile bool _stopRequested;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _upscaling = new();
    private long _pixelsDone;
    private int _texturesDone, _archivesScanned;

    private TextureBake(int threads) => Threads = threads;

    public int Threads { get; }

    public BakePhase Phase { get; private set; }

    public int ArchivesTotal { get; private set; }

    public int ArchivesScanned => Volatile.Read(ref _archivesScanned);

    public int TexturesTotal { get; private set; }

    public int TexturesDone => Volatile.Read(ref _texturesDone);

    /// <summary>Textures already in the cache when the scan ran.</summary>
    public int TexturesCached { get; private set; }

    public long PixelsTotal { get; private set; }

    public long PixelsDone => Interlocked.Read(ref _pixelsDone);

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Source megapixels upscaled per second so far (0 before any).</summary>
    public double MegapixelsPerSecond => _upscaling.Elapsed.TotalSeconds < 1 ? 0 : PixelsDone / 1e6 / _upscaling.Elapsed.TotalSeconds;

    /// <summary>Estimated time to finish, once the speed is known.</summary>
    public TimeSpan? Remaining => MegapixelsPerSecond > 0 && PixelsTotal > 0
        ? TimeSpan.FromSeconds((PixelsTotal - PixelsDone) / 1e6 / MegapixelsPerSecond) : null;

    public string? Error { get; private set; }

    public bool Running => Phase is BakePhase.Scanning or BakePhase.Upscaling;

    /// <summary>Starts a bake on <paramref name="threads"/> threads (all cores but two by default).</summary>
    public static TextureBake Start(UpscaleTool tool, string lumenRoot, int? threads = null)
    {
        var bake = new TextureBake(threads ?? Math.Max(1, Environment.ProcessorCount - 2));
        _ = Task.Run(() => bake.run(tool, lumenRoot));
        return bake;
    }

    /// <summary>Stops after the textures being upscaled (they are kept).</summary>
    public void Stop() => _stopRequested = true;

    private void run(UpscaleTool tool, string lumenRoot)
    {
        _clock.Start();
        try
        {
            var archives = usedMovies(tool).Concat(knownMovies(lumenRoot))
                .Distinct()
                .Where(movie => File.Exists(Path.Combine(lumenRoot, movie.Archive)) && !PixelArt.IsArchive(movie.Archive))
                .GroupBy(static movie => movie.Archive)
                .ToArray();
            ArchivesTotal = archives.Length;
            // The scan: which archives hold textures missing from the cache, and how big they are.
            var missing = new List<(IGrouping<string, (string Archive, string Movie)> Archive, HashSet<string> Keys)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var archive in archives)
            {
                if (_stopRequested)
                    throw new OperationCanceledException();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var source in textures(lumenRoot, archive))
                {
                    var key = UpscaleTool.Key(source);
                    if (!seen.Add(key))
                        continue;
                    if (tool.IsCached(key))
                        TexturesCached++;
                    else
                    {
                        keys.Add(key);
                        TexturesTotal++;
                        PixelsTotal += source.Width * source.Height;
                    }
                }
                if (keys.Count > 0)
                    missing.Add((archive, keys));
                Interlocked.Increment(ref _archivesScanned);
            }
            Phase = BakePhase.Upscaling;
            _upscaling.Start();
            // Nested parallel loops: without spare pool threads the pool adds them only slowly.
            ThreadPool.GetMinThreads(out var workers, out var ports);
            ThreadPool.SetMinThreads(Math.Max(workers, Threads * 2), ports);
            foreach (var (archive, keys) in missing)
            {
                // Textures side by side, largest first and handed out one at a time, and each texture's
                // tiles across the threads too (an archive may hold a single big texture).
                var sources = textures(lumenRoot, archive).Where(source => keys.Remove(UpscaleTool.Key(source)))
                    .OrderByDescending(static source => source.Width * source.Height).ToArray();
                Parallel.ForEach(Partitioner.Create(sources, loadBalance: true),
                    new ParallelOptions { MaxDegreeOfParallelism = Threads }, (source, loop) =>
                    {
                        if (_stopRequested)
                        {
                            loop.Stop();
                            return;
                        }
                        tool.Upscale(source, Threads);
                        Interlocked.Increment(ref _texturesDone);
                        Interlocked.Add(ref _pixelsDone, source.Width * source.Height);
                    });
                if (_stopRequested)
                    throw new OperationCanceledException();
            }
            Phase = BakePhase.Done;
        }
        catch (OperationCanceledException)
        {
            Phase = BakePhase.Stopped;
        }
        catch (Exception exception)
        {
            // A background task: reported on the page and in the log.
            Error = exception.Message;
            Phase = BakePhase.Failed;
            Console.Error.WriteLine($"Texture bake failed: {exception}");
        }
        finally
        {
            _clock.Stop();
            _upscaling.Stop();
        }
    }

    // An archive's movies' textures worth upscaling (tiny ones gain nothing).
    private static IEnumerable<UpscaleSource> textures(string lumenRoot, IGrouping<string, (string Archive, string Movie)> archive)
    {
        var ddp = DdpArchive.Open(File.ReadAllBytes(Path.Combine(lumenRoot, archive.Key)));
        foreach (var (_, movie) in archive)
        {
            LumenMovieContent content;
            try
            {
                content = LumenMovieContent.Load(ddp.OpenMovie(movie));
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidDataException)
            {
                continue; // a used.txt entry from another data set
            }
            foreach (var texture in content.Textures)
                if (texture.Width * texture.Height >= 256)
                    yield return new UpscaleSource((uint)texture.Width, (uint)texture.Height,
                        ImmutableCollectionsMarshal.AsArray(texture.Rgba8)!);
        }
    }

    private static IEnumerable<(string Archive, string Movie)> usedMovies(UpscaleTool tool) =>
        File.Exists(tool.UsedListPath)
            ? File.ReadAllLines(tool.UsedListPath).Select(static line => line.Split('|'))
                .Where(static parts => parts.Length == 2).Select(static parts => (parts[0], parts[1]))
            : [];

    private static IEnumerable<(string Archive, string Movie)> knownMovies(string lumenRoot)
    {
        var layout = GameplaySceneComposition.LoadLayout(lumenRoot);
        var gameplay = new SceneId("gameplay");
        SceneDefinition[] scenes =
        [
            .. FlowScenes.Initial([FlowScenes.Results, FlowScenes.WaiwaiResults]),
            FlowScenes.SongSelectScene([0]),
            FlowScenes.SongSelectScene([0, 1], waiwai: true),
            FlowScenes.Rainbow, FlowScenes.Shutter, FlowScenes.Fade,
            SystemIndicators.Definition(new SceneId("system-indicators")),
            ResumeCountdown.Definition(new SceneId("resume-countdown")),
            GameplaySceneComposition.CreateWaiwai(gameplay, layout),
            .. from side in Sides
               from twoPlayers in PlayerCounts
               select GameplaySceneComposition.Create(gameplay, new Random(0), layout, side: side, twoPlayers: twoPlayers),
        ];
        return scenes.SelectMany(static scene => scene.Layers)
            .Select(static layer => (layer.ArchiveId, layer.MovieId))
            .Concat(GameplaySceneComposition.OriginalMovies())
            // The home menu's panel and badge art (never loaded as a scene).
            .Append(("entry_info/packeddata.ddp", "entry_info/entry_info.lm"));
    }
}
