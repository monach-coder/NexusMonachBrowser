using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class CrashReportSanitizationTests
{
    [Fact]
    public void SensitiveValues_AreRemovedFromReportText()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop";
        const string source =
            "GET https://example.com/private?q=secret user@example.com " +
            "Authorization: Bearer abcdefghijklmnop Cookie=session-cookie-value " +
            "token=plain-token-value " + jwt;

        var sanitized = CrashReportService.SanitizeForReport(source);

        Assert.DoesNotContain("https://example.com", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user@example.com", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdefghijklmnop", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("session-cookie-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, sanitized, StringComparison.Ordinal);
        Assert.Contains("[url-redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("[email-redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("[secret-redacted]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizedReportText_IsBounded()
    {
        var sanitized = CrashReportService.SanitizeForReport(new string('x', 20_000));

        Assert.Equal(16_000, sanitized.Length);
    }
}
