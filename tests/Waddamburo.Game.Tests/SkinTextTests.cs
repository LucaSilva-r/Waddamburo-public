using Waddamburo.Formats.Text;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Game.Tests;

public sealed class SkinTextTests
{
    private const string Json = """
        { "format": 1,
          "fonts": { "pop": "fonts/pop.ttf" },
          "styles": {
            "title": { "font": "pop", "size": 64, "fit": "squeeze",
              "layers": [ { "grow": 9, "offset": [2, 3], "color": "#e01818" }, { "color": ["#ffffff", "#c8c8d0"] } ] } } }
        """;

    [Fact]
    public void FontsAreTheSkinsFiles()
    {
        var text = SkinText.Parse(Json, "/skin");
        Assert.Equal(Path.GetFullPath("/skin/fonts/pop.ttf"), text.Fonts["pop"]);
        Assert.Equal(Path.GetFullPath("/skin/fonts/pop.ttf"), text.Resolve("""{"style":"title"}""", 24, 'l', 0).FontFile);
    }

    // The field's movie settings, under its style, under its own settings.
    [Fact]
    public void AFieldsStyleIsItsMovieThenItsStyleThenItsOwn()
    {
        var text = SkinText.Parse(Json, "/skin");
        var (plain, font) = text.Resolve(null, 20, 'c', 0xff8800);
        Assert.Equal((20f, TextAlign.Center, 0xff8800ffu, (string?)null), (plain.Size, plain.Align, plain.Layers.Single().Top, font));
        var (styled, _) = text.Resolve("""{"style":"title","size":40,"color":"#00ff00","text":"x"}""", 20, 'c', 0xff8800);
        Assert.Equal((40f, TextAlign.Center, TextFit.Squeeze, 2), (styled.Size, styled.Align, styled.Fit, styled.Layers.Length));
        Assert.Equal((0xe01818ffu, 0x00ff00ffu, 0x00ff00ffu), (styled.Layers[0].Top, styled.Layers[1].Top, styled.Layers[1].Bottom));
        Assert.Equal(9 + 3, styled.Reach);
        // Its own layers bring their own colours; an unknown style is just its own settings.
        var (own, _) = text.Resolve("""{"style":"nope","layers":[{"color":"#123456"}]}""", 20, 'l', 0);
        Assert.Equal(0x123456ffu, own.Layers.Single().Top);
    }

    [Theory]
    [InlineData("""{ "format": 1, "styles": { "a": { "font": "missing" } } }""", "no font \"missing\"")]
    [InlineData("""{ "format": 1, "styles": { "a": { "size": "big" } } }""", "\"size\" is a number")]
    [InlineData("""{ "format": 1, "fonts": { "game": "x.ttf" } }""", "not called \"game\"")]
    [InlineData("""{ "format": 2 }""", "\"format\": 1")]
    public void MistakesAreNamed(string json, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => SkinText.Parse(json, "/skin")).Message);
}
