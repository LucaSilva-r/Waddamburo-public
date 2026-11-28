using Waddamburo.Game.Scenes;

namespace Waddamburo.Game.Tests;

public sealed class MoviePackageSourceTests
{
    private const string Movie = """
        {
          "format": 1, "root": "main",
          "textures": { "dot": "textures/dot.png" },
          "shapes": { "dot": { "texture": "dot", "rect": [0, 0, 8, 8] } },
          "sprites": { "main": { "frames": 1, "layers": [ { "character": "dot", "keys": [ { "frame": 0 } ] } ] } }
        }
        """;

    [Fact]
    public void AnSvgIsDrawnForEachSizeItsShapesShowItAt()
    {
        const string movie = """
            {
              "format": 1, "root": "main",
              "textures": { "dot": "dot.svg", "art": "art.png" },
              "shapes": { "small": { "texture": "dot", "rect": [-7, -7, 14, 14] }, "big": { "texture": "dot", "rect": [-19, -19, 38, 38] },
                          "also": { "texture": "dot", "rect": [0, 0, 14, 14] }, "picture": { "texture": "art", "rect": [0, 0, 100, 50] } },
              "sprites": { "main": { "frames": 1, "layers": [ { "character": "small", "keys": [ { "frame": 0 } ] },
                { "character": "big", "keys": [ { "frame": 0 } ] }, { "character": "picture", "keys": [ { "frame": 0 } ] } ] } }
            }
            """;
        var sized = new List<(int, int)>();
        var content = LumenMovieContent.LoadPackage(Path.Combine(Path.GetTempPath(), "sized"), static _ => (4, 4, new byte[64]), movieJson: movie,
            readFile: static _ => [1],
            decodeSized: (_, width, height) => { sized.Add((width, height)); return (width, height, new byte[width * height * 4]); },
            pixelsPerUnit: 1.5f);
        // One drawing per size (14 units twice: one), at 1.5 pixels a unit; the PNG as it is.
        Assert.Equal([(21, 21), (57, 57)], sized.Order());
        Assert.Equal(3, content.Textures.Length);
        Assert.Contains(content.Textures, static texture => texture.Width == 4);
    }

    [Fact]
    public async Task APackageStandsInForAnArchiveMovieAndAMissingMovieIsNamed()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-package-test").FullName;
        try
        {
            var package = Path.Combine(root, "movies", "indicator", "time_counter");
            Directory.CreateDirectory(Path.Combine(package, "textures"));
            File.WriteAllText(Path.Combine(package, "movie.json"), Movie);
            File.WriteAllBytes(Path.Combine(package, "textures", "dot.png"), [1, 2, 3]);
            // No archive exists under the asset root at all: the package alone carries the movie.
            var source = new DirectoryLumenMovieContentSource(root)
            {
                PackageRoot = Path.Combine(root, "movies"),
                DecodeImage = static png => png.Length == 3 ? (1, 1, new byte[] { 255, 255, 255, 255 }) : null,
            };

            var content = await source.LoadAsync("indicator/packeddata.ddp", "time_counter/time_counter.lm", CancellationToken.None);
            Assert.Equal("time_counter/time_counter.lm", content.Name);
            Assert.Equal(1, Assert.Single(content.Textures).Width);
            Assert.Empty(content.Diagnostics);
            Assert.Single(content.CreatePlayer().CreateRenderSnapshot().Quads);

            var missing = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
                await source.LoadAsync("indicator/packeddata.ddp", "player_name/player_name.lm", CancellationToken.None));
            Assert.Contains("Movie 'player_name/player_name.lm' is missing", missing.Message);

            // The engine's own movies: only where the game data has none, unless they are preferred.
            var bundled = Path.Combine(root, "bundled", "indicator", "player_name");
            Directory.CreateDirectory(Path.Combine(bundled, "textures"));
            File.WriteAllText(Path.Combine(bundled, "movie.json"), Movie);
            File.WriteAllBytes(Path.Combine(bundled, "textures", "dot.png"), [1, 2, 3]);
            Assert.False(source.Has("indicator/packeddata.ddp", "player_name/player_name.lm"));
            source.BundledRoot = Path.Combine(root, "bundled");
            Assert.True(source.Has("indicator/packeddata.ddp", "player_name/player_name.lm"));
            Assert.Equal("player_name/player_name.lm",
                (await source.LoadAsync("indicator/packeddata.ddp", "player_name/player_name.lm", CancellationToken.None)).Name);
            Directory.CreateDirectory(Path.Combine(root, "indicator"));
            File.WriteAllBytes(Path.Combine(root, "indicator", "packeddata.ddp"), [0]); // The game data has the archive (a broken one here).
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await source.LoadAsync("indicator/packeddata.ddp", "player_name/player_name.lm", CancellationToken.None));
            source.PreferBundled = static () => true;
            Assert.Single((await source.LoadAsync("indicator/packeddata.ddp", "player_name/player_name.lm", CancellationToken.None)).Textures);

            File.WriteAllText(Path.Combine(package, "movie.json"), Movie.Replace("textures/dot.png", "../../dot.png"));
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await source.LoadAsync("indicator/packeddata.ddp", "time_counter/time_counter.lm", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
