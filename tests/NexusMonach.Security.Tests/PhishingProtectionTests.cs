using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class PhishingProtectionTests
{
    [Theory]
    [InlineData("https://google.com")]
    [InlineData("https://accounts.google.com/signin")]
    [InlineData("https://accounts.google.se/accounts/SetSID?continue=https%3A%2F%2Fwww.youtube.com")]
    [InlineData("https://github.com/login")]
    public void OfficialHttpsHosts_AreNotFlagged(string url)
    {
        var result = PhishingProtectionService.Analyze(url);

        Assert.Equal(PhishingRiskLevel.None, result.Level);
    }

    [Fact]
    public void MixedCyrillicAndLatinHomograph_IsHighRisk()
    {
        var result = PhishingProtectionService.Analyze("https://аррle.com");

        Assert.Equal(PhishingRiskLevel.High, result.Level);
    }

    [Fact]
    public void PunycodeHomograph_IsNeverTrusted()
    {
        var result = PhishingProtectionService.Analyze("https://xn--pple-43d.com");

        Assert.NotEqual(PhishingRiskLevel.None, result.Level);
    }

    [Theory]
    [InlineData("https://goog1e.com")]
    [InlineData("https://paypa1.com")]
    public void OneCharacterBrandSubstitution_IsFlagged(string url)
    {
        var result = PhishingProtectionService.Analyze(url);

        Assert.NotEqual(PhishingRiskLevel.None, result.Level);
    }

    [Fact]
    public void BrandCombinedWithLoginPrompt_IsHighRisk()
    {
        var result = PhishingProtectionService.Analyze("https://secure-google-login.example");

        Assert.Equal(PhishingRiskLevel.High, result.Level);
    }

    [Fact]
    public void LookalikeRegionalGoogleLogin_IsStillBlocked()
    {
        var result = PhishingProtectionService.Analyze("https://accounts.google.se.evil.example/login");

        Assert.Equal(PhishingRiskLevel.High, result.Level);
    }
}
