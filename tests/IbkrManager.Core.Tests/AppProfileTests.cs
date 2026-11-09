using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class AppProfileTests
{
    [Fact]
    public void Embedded_profile_is_valid()
    {
        var profile = AppProfile.LoadEmbedded();

        Assert.Equal("ntws.exe", profile.ExecutableName);
        Assert.Contains("IBKR Desktop", profile.RegistryDisplayNamePatterns);
        Assert.True(profile.LoginWindowTimeout > TimeSpan.Zero);
        Assert.Contains("C:\\Program Files\\IBKR Desktop", profile.FallbackInstallPaths);
    }

    [Theory]
    [InlineData("IBKR Desktop - Login", WindowKind.Login)]
    // The real window is titled "IBKR Desktop" in *every* state, so the title cannot mean
    // "logged in"; classification relies on accessibility content instead.
    [InlineData("IBKR Desktop", WindowKind.Unknown)]
    [InlineData("IBKR Desktop Security Code", WindowKind.TwoFactor)]
    [InlineData("Something else", WindowKind.Unknown)]
    [InlineData("", WindowKind.Unknown)]
    [InlineData(null, WindowKind.Unknown)]
    public void Classify_uses_the_embedded_patterns(string? title, WindowKind expected) =>
        Assert.Equal(expected, AppProfile.LoadEmbedded().Classify(title));

    [Fact]
    public void Classify_prefers_two_factor_over_login()
    {
        var profile = AppProfile.Default with
        {
            LoginWindowTitlePatterns = ["IBKR"],
            TwoFactorWindowTitlePatterns = ["Security Code"],
            LoggedInWindowTitlePatterns = ["IBKR"],
        };

        Assert.Equal(WindowKind.TwoFactor, profile.Classify("IBKR Security Code"));
    }

    [Fact]
    public void FromJson_applies_defaults_and_validates()
    {
        var profile = AppProfile.FromJson("""{"executableName":"other.exe"}""");

        Assert.Equal("other.exe", profile.ExecutableName);
        Assert.Equal("IBKR Desktop", profile.DisplayName);
    }

    [Fact]
    public void FromJson_rejects_invalid_json() =>
        Assert.Throws<AppProfileException>(() => AppProfile.FromJson("{ not json"));

    [Theory]
    [InlineData("""{"executableName":"ntws"}""")]
    [InlineData("""{"executableName":""}""")]
    [InlineData("""{"typingDelayMilliseconds":-5}""")]
    [InlineData("""{"typingDelayMilliseconds":9000}""")]
    public void FromJson_rejects_invalid_content(string json) =>
        Assert.Throws<AppProfileException>(() => AppProfile.FromJson(json));

    [Fact]
    public void MatchesRegistryName_is_case_insensitive()
    {
        var profile = AppProfile.Default with { RegistryDisplayNamePatterns = ["IBKR Desktop"] };

        Assert.True(profile.MatchesRegistryName("ibkr desktop 1.2.3"));
        Assert.False(profile.MatchesRegistryName("Trader Workstation"));
        Assert.False(profile.MatchesRegistryName(null));
    }
}
