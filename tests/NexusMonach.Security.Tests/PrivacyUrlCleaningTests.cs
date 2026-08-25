using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class PrivacyUrlCleaningTests
{
    [Theory]
    [InlineData("https://example.com/path?utm_source=mail&id=42&fbclid=secret#part",
        "https://example.com/path?id=42#part")]
    [InlineData("https://example.com/?ttclid=one&page=3&wbraid=two",
        "https://example.com/?page=3")]
    [InlineData("https://example.com/search?q=nexus&%75tm_campaign=test",
        "https://example.com/search?q=nexus")]
    public void KnownTrackingParametersAreRemovedButFunctionalValuesRemain(string input, string expected)
    {
        Assert.Equal(expected, UrlService.CleanTrackingParameters(input, force: true));
    }

    [Fact]
    public void NonTrackingQueryIsNotRewritten()
    {
        const string url = "https://example.com/catalog?category=books&page=2";
        Assert.Equal(url, UrlService.CleanTrackingParameters(url, force: true));
    }

    [Fact]
    public void MalformedEscapingDoesNotBreakNavigationCleaning()
    {
        const string url = "https://example.com/catalog?bad=%&page=2";
        Assert.Equal(url, UrlService.CleanTrackingParameters(url, force: true));
    }

    [Theory]
    [InlineData("https://user:password@example.com/private/path?token=secret#section",
        "https://example.com/private/path")]
    [InlineData("http://example.com:8080/catalog?id=42", "http://example.com:8080/catalog")]
    public void DiagnosticAddressOmitsCredentialsQueryAndFragment(string input, string expected)
    {
        Assert.Equal(expected, UrlService.SanitizeForDisplay(input));
    }

    [Fact]
    public void DiagnosticAddressRejectsNonWebSchemes()
    {
        Assert.Equal(string.Empty, UrlService.SanitizeForDisplay("file:///C:/secret.txt"));
    }
}
