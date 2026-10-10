using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class UiLanguageTests
{
    [Theory]
    [InlineData("system", "ja-JP", "ja")]
    [InlineData("system", "ja", "ja")]
    [InlineData("system", "en-US", "en")]
    [InlineData("system", "vi-VN", "en")]   // unsupported locales fall back to English
    [InlineData("system", "", "en")]
    [InlineData(null, "ja-JP", "ja")]       // no setting yet: device decides
    [InlineData("en", "ja-JP", "en")]       // explicit override wins over device
    [InlineData("ja", "en-US", "ja")]
    [InlineData("JA", "en-US", "ja")]       // case-insensitive
    [InlineData("de", "en-US", "en")]       // unknown setting value: device decides
    public void Resolve_MapsSettingAndSystemLocale(string? setting, string systemTag, string expected)
        => Assert.Equal(expected, UiLanguage.Resolve(setting, systemTag));

    [Fact]
    public void ToTag_MapsToConcreteCulture()
    {
        Assert.Equal("ja-JP", UiLanguage.ToTag("ja"));
        Assert.Equal("en-US", UiLanguage.ToTag("en"));
        Assert.Equal("en-US", UiLanguage.ToTag("whatever"));
    }
}
