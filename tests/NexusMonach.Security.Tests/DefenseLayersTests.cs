using System.Text.Json;
using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Пять новых слоёв доверия: страж расширений, стандартизация отпечатка,
/// целостность профиля, канарейка цепочки, сторож движка. Чистые части
/// покрываются юнит-тестами; сетевые — только парсингом и сравнением.
/// </summary>
public sealed class DefenseLayersTests
{
    private static JsonElement Manifest(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ── Страж расширений ─────────────────────────────────────────

    [Fact]
    public void ExtensionRisk_MinimalManifest_IsSafe()
    {
        var report = ExtensionRiskAnalyzer.Analyze(
            Manifest("{\"manifest_version\":3,\"name\":\"notes\",\"version\":\"1\",\"permissions\":[\"storage\"]}"));
        Assert.Equal(ExtensionRiskVerdict.Safe, report.Verdict);
        Assert.Equal(0, report.Score);
    }

    [Fact]
    public void ExtensionRisk_AllUrlsPlusWebRequestBlocking_IsDangerous()
    {
        var report = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"spy\",\"version\":\"1\",\"permissions\":[\"webRequest\",\"webRequestBlocking\",\"<all_urls>\"]}"));
        Assert.Equal(ExtensionRiskVerdict.Dangerous, report.Verdict);
        Assert.Contains(report.Reasons, r => r.Contains("всем сайтам", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, r => r.Contains("запросов", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionRisk_NativeMessagingAlone_IsCaution()
    {
        var report = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"bridge\",\"version\":\"1\",\"permissions\":[\"nativeMessaging\",\"tabs\"]}"));
        // 4 + 1 = 5 — предостережение, не блок: мост опасен только в связке.
        Assert.Equal(ExtensionRiskVerdict.Caution, report.Verdict);
    }

    [Fact]
    public void ExtensionRisk_ContentScriptEverywhere_AddsWeight()
    {
        var report = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":2,\"name\":\"inject\",\"version\":\"1\",\"content_scripts\":[{\"matches\":[\"<all_urls>\"],\"js\":[\"s.js\"]}],\"permissions\":[\"tabs\"]}"));
        Assert.True(report.Score >= 3);
        Assert.NotEqual(ExtensionRiskVerdict.Safe, report.Verdict);
    }

    [Fact]
    public void ExtensionRisk_OptionalPermissions_CostLess()
    {
        var required = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"a\",\"version\":\"1\",\"permissions\":[\"cookies\"]}"));
        var optional = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"b\",\"version\":\"1\",\"optional_permissions\":[\"cookies\"]}"));
        Assert.True(optional.Score < required.Score);
    }

    [Fact]
    public void ExtensionRisk_Debugger_IsDangerous()
    {
        var report = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"dev\",\"version\":\"1\",\"permissions\":[\"debugger\",\"tabs\"]}"));
        Assert.Equal(ExtensionRiskVerdict.Dangerous, report.Verdict);
    }

    [Fact]
    public void ExtensionRisk_HostPermissionsAllUrls_Counted()
    {
        var report = ExtensionRiskAnalyzer.Analyze(Manifest(
            "{\"manifest_version\":3,\"name\":\"net\",\"version\":\"1\",\"permissions\":[\"proxy\"],\"host_permissions\":[\"*://*/*\"]}"));
        // proxy (3) + все сайты (4) = 7 — опасная связка подмены маршрута.
        Assert.Equal(7, report.Score);
        Assert.Equal(ExtensionRiskVerdict.Dangerous, report.Verdict);
    }

    // ── Стандартизация отпечатка ─────────────────────────────────

    [Theory]
    [InlineData("130.0.2849.68", "130.0.2849.68")]
    [InlineData("120.0.2210.61", "120.0.2210.61")]
    public void UserAgent_IsPlainChrome_FromRuntimeVersion(string runtime, string version)
    {
        var ua = FingerprintService.NormalizeUserAgent(runtime);
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + version + " Safari/537.36",
            ua);
    }

    [Fact]
    public void UserAgent_NeverContainsEdgeOrWebViewTokens()
    {
        var ua = FingerprintService.NormalizeUserAgent("130.0.2849.68");
        Assert.DoesNotContain("Edg/", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("NexusMonach", ua, StringComparison.Ordinal);
    }

    // ── Сторож движка ────────────────────────────────────────────

    [Theory]
    [InlineData("130.0.2849.68", 130, 0, 2849, 68)]
    public void RuntimeVersion_Parsed(string raw, int major, int minor, int build, int revision)
    {
        var version = WebView2RuntimeWatchdog.ParseVersion(raw);
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3")]
    public void RuntimeVersion_RejectsMalformed(string raw)
    {
        Assert.Null(WebView2RuntimeWatchdog.ParseVersion(raw));
    }

    // ── Канарейка цепочки ────────────────────────────────────────

    [Fact]
    public void CanaryProxy_EitherInactiveOrLoopback()
    {
        var (host, port) = EgressCanaryService.ActiveChainProxy();
        Assert.True(port == 0 || host == "127.0.0.1");
    }

    // ── Целостность профиля ──────────────────────────────────────

    [Fact]
    public void ProfileSnapshot_StableForSameContent()
    {
        var first = ProfileIntegrityService.CaptureSnapshot();
        var second = ProfileIntegrityService.CaptureSnapshot();
        Assert.Equal(first.Count, second.Count);
        foreach (var key in first.Keys)
            Assert.Equal(first[key], second[key]);
    }
}
