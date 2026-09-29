using System.Diagnostics;
using System.Runtime.InteropServices;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Formats.Ddp;
using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Tools;

/// <summary>
/// --upscale-textures[=threads]: fills the upscale cache ahead of play (all cores but two by default).
/// Only movies the game uses: every one it has loaded (used.txt) plus
/// the fixed scenes and every gameplay part a random mix may pick. Green also carries older
/// versions' assets, so the archives are never swept wholesale.
/// ponytail: songs' themed skins come in through used.txt once played; list them from the catalog
/// if a first batch should cover them.
/// </summary>
internal static class TextureCacheBuilder
{
    private static readonly int[] Sides = [0, 1];
    private static readonly bool[] PlayerCounts = [false, true];

    public static int Run(string cacheFolder, string lumenRoot, int threads)
    {
        using var tool = UpscaleTool.Find(cacheFolder);
        if (tool is null)
        {
            Console.Error.WriteLine("Texture upscaling needs the model: realesr-animevideov3-x3.param and .bin in an "
                + "'upscale' folder next to Waddamburo (or WADDAMBURO_UPSCALE_MODEL naming their folder).");
            return 1;
        }
        var movies = usedMovies(tool).Concat(knownMovies(lumenRoot))
            .Distinct()
            .Where(movie => File.Exists(Path.Combine(lumenRoot, movie.Archive)))
            .GroupBy(static movie => movie.Archive)
            .ToArray();
        Console.WriteLine($"Upscaling the textures of {movies.Sum(static group => group.Count())} movies "
            + $"in {movies.Length} archives into {tool.Cache} on {threads} thread(s).");
        // Nested parallel loops: without spare pool threads the pool adds them only slowly.
        ThreadPool.GetMinThreads(out var workers, out var ports);
        ThreadPool.SetMinThreads(Math.Max(workers, threads * 2), ports);
        var clock = Stopwatch.StartNew();
        int done = 0, skipped = 0;
        double megapixels = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < movies.Length; index++)
        {
            var archive = DdpArchive.Open(File.ReadAllBytes(Path.Combine(lumenRoot, movies[index].Key)));
            var sources = new List<UpscaleSource>();
            foreach (var (_, movie) in movies[index])
            {
                LumenMovieContent content;
                try
                {
                    content = LumenMovieContent.Load(archive.OpenMovie(movie));
                }
                catch (Exception exception) when (exception is KeyNotFoundException or InvalidDataException)
                {
                    continue; // a used.txt entry from another data set
                }
                foreach (var texture in content.Textures)
                {
                    var source = new UpscaleSource((uint)texture.Width, (uint)texture.Height,
                        ImmutableCollectionsMarshal.AsArray(texture.Rgba8)!);
                    var key = UpscaleTool.Key(source);
                    if (source.Width * source.Height < 256 || !seen.Add(key))
                        continue;
                    if (tool.IsCached(key))
                        skipped++;
                    else
                        sources.Add(source);
                }
            }
            // Textures side by side, largest first and handed out one at a time, and each texture's
            // tiles across the threads too (an archive may hold a single big texture).
            var ordered = sources.OrderByDescending(static source => source.Width * source.Height).ToArray();
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(ordered, loadBalance: true),
                new ParallelOptions { MaxDegreeOfParallelism = threads }, source =>
                {
                    tool.Upscale(source, threads);
                    Interlocked.Increment(ref done);
                });
            megapixels += sources.Sum(static source => source.Width * source.Height / 1e6);
            Console.WriteLine($"[{index + 1}/{movies.Length}] {movies[index].Key}: {done} upscaled ({megapixels:F1} MP), "
                + $"{skipped} already cached ({clock.Elapsed:hh\\:mm\\:ss}).");
        }
        Console.WriteLine($"Done in {clock.Elapsed:hh\\:mm\\:ss}: {done} upscaled, {skipped} already cached.");
        return 0;
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
            .Concat(GameplaySceneComposition.OriginalMovies());
    }
}
