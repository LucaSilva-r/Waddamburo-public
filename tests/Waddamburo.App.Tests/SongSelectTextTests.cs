using Waddamburo.MenuLayouts;

namespace Waddamburo.App.Tests;

public sealed class SongSelectTextTests
{
    [Theory]
    [InlineData("Group: Title", "グループ：曲名")]
    [InlineData("Group: Collections", "グループ：コレクション")]
    [InlineData("Sort: Date Added", "並び順：追加日")]
    [InlineData("Sort: Mapper", "並び順：譜面作者")]
    [InlineData("Group: My own label", "Group: My own label")]
    public void LibraryControlsAreTranslatedWithoutRenamingUnknownLabels(string english, string japanese)
    {
        Assert.Equal(english, SongSelectText.Name(english, english: true));
        Assert.Equal(japanese, SongSelectText.Name(english, english: false));
    }

    [Theory]
    [InlineData("Original Songs", "オリジナル曲")]
    [InlineData("Vocaloid", "ボーカロイド")]
    [InlineData("Return", "もどる")]
    [InlineData("How to Play", "あそびかた")]
    public void GeneratedNamesFollowMenuLanguage(string english, string japanese)
    {
        Assert.Equal(english, SongSelectText.Name(english, true));
        Assert.Equal(japanese, SongSelectText.Name(english, false));
    }

    [Theory]
    [InlineData("Play the\noriginal\nsongs!!")]
    [InlineData("Play your\nosu!taiko\nmaps!!")]
    [InlineData("Choose between the latest hits and the most popular songs!!")]
    public void BothDescriptionLayoutsUseJapaneseWhenRequested(string text)
    {
        Assert.Equal(text, SongSelectText.Description(text, true));
        var japanese = SongSelectText.Description(text, false);
        Assert.NotEqual(text, japanese);
        Assert.Contains(japanese, character => character is >= '\u3040' and <= '\u30ff');
    }

    [Fact]
    public void UserFolderNamesAndDescriptionsArePreserved()
    {
        Assert.Equal("My pack", SongSelectText.Name("My pack", false));
        Assert.Equal("My own\ndescription", SongSelectText.Description("My own\ndescription", false));
    }
}
