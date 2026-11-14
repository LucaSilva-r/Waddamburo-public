using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class RomajiTests
{
    [Theory]
    [InlineData("きたさいたま２０００", "kitasaitama2000")]
    [InlineData("きたさいたま２０００", "kita saitama")]
    [InlineData("ゴーストルール", "gosutoruru")]
    [InlineData("ちっちゃな", "chitchana")]
    [InlineData("しゃんぷー", "shanpu")]
    [InlineData("じゃんけん", "janken")]
    [InlineData("ファミリー", "famiri")]
    [InlineData("つけまつける", "tsukematsukeru")]
    [InlineData("つけまつける", "tukematukeru")]
    public void TypedRomajiFindsKana(string title, string query) =>
        Assert.All(query.Split(' '), word => Assert.Contains(Romaji.Fold(word), Romaji.Fold(Romaji.FromKana(title))));

    [Fact]
    public void KanjiAndLatinStayAsWritten() => Assert.Equal("千本桜 senbonzakura", Romaji.FromKana("千本桜 Senbonzakura"));
}
