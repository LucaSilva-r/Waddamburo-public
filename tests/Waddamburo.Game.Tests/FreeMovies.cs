using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

/// <summary>The engine's own movies (the repository's movies folder), loaded as packages.</summary>
internal static class FreeMovies
{
    public static LumenPlayer Load(string movie)
    {
        var directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(directory, "movies", "indicator", "time_counter")))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("movies/indicator/time_counter");
        // The textures only need a size here.
        var content = LumenMovieContent.LoadPackage(Path.Combine(directory, "movies", movie),
            static _ => (1, 1, new byte[4]));
        Assert.Empty(content.Diagnostics);
        return content.CreatePlayer();
    }
}
