using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class SceneSwapTests
{
    private const string Movie = """
        { "format": 1, "root": "main", "textures": { "dot": "textures/dot.png" },
          "shapes": { "dot": { "texture": "dot", "rect": [0, 0, 8, 8] } },
          "sprites": { "main": { "frames": 1, "layers": [ { "character": "dot", "keys": [ { "frame": 0 } ] } ] } } }
        """;

    private sealed class Hosts(List<LumenPlayer> attached) : ILumenLayerHostFactory
    {
        public LumenLayerHost Create(SceneLayerDefinition layer) => new(null, attached.Add);
    }

    // A saved package goes in its layer's place with the screen going on: what the engine gave the old movie, its
    // request handler and its host come along; its textures go after the scene's.
    [Fact]
    public async Task ASwappedMovieKeepsItsDataHandlerAndHost()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-swap-test").FullName;
        try
        {
            var package = Path.Combine(root, "entry", "entry");
            Directory.CreateDirectory(Path.Combine(package, "textures"));
            File.WriteAllText(Path.Combine(package, "movie.json"), Movie);
            File.WriteAllBytes(Path.Combine(package, "textures", "dot.png"), [1, 2, 3]);
            var source = new DirectoryLumenMovieContentSource(root)
            {
                PackageRoot = root,
                DecodeImage = static png => png.Length == 3 ? (1, 1, new byte[] { 255, 255, 255, 255 }) : null,
            };
            var attached = new List<LumenPlayer>();
            var definition = new SceneDefinition(SceneDefinition.CurrentVersion, new SceneId("entry"),
                [new SceneLayerDefinition("entry/packeddata.ddp", "entry/entry.lm", LumenMatrix.Identity, "player-entry")]);
            var scene = (LumenGameSceneInstance)await new LumenGameSceneLoader(source, new Hosts(attached)).LoadAsync(definition, CancellationToken.None);
            var old = scene.Player.Layers[0].Player;
            old.Publish("player.name", LumenHostValue.FromString("Don-chan"), 1);
            old.PublishItem("scores.name", 2, LumenHostValue.FromString("Wada"));
            Func<string, LumenHostValue[], string?> handler = static (_, _) => null;
            old.RequestHandler = handler;

            var fresh = scene.Swap(0, await source.LoadAsync("entry/packeddata.ddp", "entry/entry.lm", CancellationToken.None));

            Assert.NotSame(old, fresh);
            Assert.Same(fresh, scene.Player.Layers[0].Player);
            Assert.Equal("Don-chan", fresh.Provided("player.name", 1).AsString());
            Assert.Contains(fresh.ProvidedNow(), static provided => provided.Name == "scores.name" && provided.Index == 2);
            Assert.Same(handler, fresh.RequestHandler);
            Assert.Equal([old, fresh], attached);
            Assert.Equal((1u, 1u), (scene.Player.Layers[0].TextureOffset, scene.Player.Layers[0].TextureCount));
            Assert.Equal(2, scene.Textures.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
