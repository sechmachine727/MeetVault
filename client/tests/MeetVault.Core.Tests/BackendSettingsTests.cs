using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public class BackendSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToTheShippedDefault(string? stored)
        => Assert.Equal(BackendDefaults.Url, BackendSettings.Resolve(stored));

    [Theory]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("  https://example.com  ", "https://example.com")]
    [InlineData("http://box:8080", "http://box:8080")]
    public void Resolve_KeepsACustomValue_Trimmed(string stored, string expected)
        => Assert.Equal(expected, BackendSettings.Resolve(stored));

    [Fact]
    public void IsServerChange_TrueOnlyForADifferentNonEmptyValue()
    {
        Assert.True(BackendSettings.IsServerChange("http://127.0.0.1:8081", BackendDefaults.Url));
        // Same server, different case + trailing slash: cosmetic differences must not fire the reset.
        Assert.False(BackendSettings.IsServerChange("https://A.example", "https://a.example/"));
        Assert.False(BackendSettings.IsServerChange(BackendDefaults.Url, BackendDefaults.Url + "/"));
        Assert.False(BackendSettings.IsServerChange(null, BackendDefaults.Url));
        Assert.False(BackendSettings.IsServerChange("http://old", ""));
    }

    [Fact]
    public void MigrateLegacy_OnlyRewritesTheOldDevUrl_Once()
    {
        Assert.Equal(BackendDefaults.Url, BackendSettings.MigrateLegacy("http://127.0.0.1:8081", null));
        Assert.Null(BackendSettings.MigrateLegacy("http://127.0.0.1:8081", "1"));
        Assert.Null(BackendSettings.MigrateLegacy("https://custom.example", null));
        Assert.Null(BackendSettings.MigrateLegacy(null, null));
    }
}
