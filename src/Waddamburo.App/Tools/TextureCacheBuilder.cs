using Waddamburo.App.Presentation;

namespace Waddamburo.App.Tools;

/// <summary>--upscale-textures[=threads]: a <see cref="TextureBake"/> with no game window, progress on the console.</summary>
internal static class TextureCacheBuilder
{
    public static int Run(string cacheFolder, string lumenRoot, int threads)
    {
        using var tool = UpscaleTool.Find(cacheFolder);
        if (tool is null)
        {
            Console.Error.WriteLine("Texture upscaling is unavailable (the waddamburo_texture library is missing).");
            return 1;
        }
        var bake = TextureBake.Start(tool, lumenRoot, threads);
        Console.WriteLine($"Upscaling textures into {tool.Cache} on {bake.Threads} thread(s).");
        while (bake.Running)
        {
            Thread.Sleep(2000);
            Console.WriteLine(bake.Phase == TextureBake.BakePhase.Scanning
                ? $"Scanning: {bake.ArchivesScanned}/{bake.ArchivesTotal} archives."
                : $"Upscaling: {bake.TexturesDone}/{bake.TexturesTotal} textures, {bake.MegapixelsPerSecond:F2} MP/s"
                    + (bake.Remaining is { } left ? $", {left:hh\\:mm\\:ss} left." : "."));
        }
        Console.WriteLine($"{bake.Phase} in {bake.Elapsed:hh\\:mm\\:ss}: {bake.TexturesDone} upscaled, "
            + $"{bake.TexturesCached} already cached.{(bake.Error is { } error ? $" {error}" : "")}");
        return bake.Phase == TextureBake.BakePhase.Done ? 0 : 1;
    }
}
