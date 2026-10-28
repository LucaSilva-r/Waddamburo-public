using System.Globalization;
using Waddamburo.App.Cli;

namespace Waddamburo.App.Tests;

public sealed class StartupLanguageTests
{
    [Theory]
    [InlineData("ja-JP", "ja", "ゲームデータ")]
    [InlineData("en-US", "en", "Game data is missing")]
    [InlineData("it-IT", "en", "Game data is missing")]
    public void MissingDataNoticeFollowsSystemUiCulture(string locale, string language, string expected)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        var previousLanguage = Strings.Language;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
            Strings.Use("auto");
            var root = Path.Combine(Path.GetTempPath(), "waddamburo-locale-" + Guid.NewGuid().ToString("N"));
            var error = Assert.Throws<MissingGameDataException>(() => GameDataLayout.Resolve(root));
            Assert.Equal(language, Strings.Language);
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.Contains("USRDIR", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
            Strings.Use(previousLanguage);
        }
    }
}
