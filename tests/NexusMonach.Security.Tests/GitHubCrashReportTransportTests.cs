using System.Text.Json;
using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class GitHubCrashReportTransportTests
{
    private const string RenderStack =
        "System.Runtime.InteropServices.COMException (0x88980406): UCEERR_RENDERTHREADFAILURE\r\n" +
        "   at System.Windows.Media.Composition.DUCE.Channel.SyncFlush()\r\n" +
        "   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(Boolean enableRenderTarget)\r\n" +
        "   at NexusMonach.Views.MainWindow.ShowSettings()\r\n" +
        "   at Frame.Four()\r\n" +
        "   at Frame.Five()\r\n" +
        "   at Frame.Six()";

    private static JsonElement Report(string stack = RenderStack, string component = "wpf") =>
        JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = 2,
            Id = "abc123",
            TimestampUtc = "2026-08-25T05:00:00Z",
            Fatal = true,
            BrowserVersion = "2.9.0.0",
            Component = component,
            Stage = "dispatcher-unhandled",
            ExceptionType = "System.Runtime.InteropServices.COMException",
            Message = "UCEERR_RENDERTHREADFAILURE",
            StackTrace = stack,
            IntegrityStatus = "verified",
            SafeMode = false,
            CausalGraph = new
            {
                Nodes = new[]
                {
                    new { Id = "b0", Kind = "event", Title = "startup · main-window-ready",
                          TimestampUtc = "2026-08-25T04:59:00Z", Details = (string?)null },
                    new { Id = "crash", Kind = "exception", Title = "wpf: COMException",
                          TimestampUtc = "2026-08-25T05:00:00Z", Details = (string?)null }
                },
                Edges = new[]
                {
                    new { FromId = "b0", ToId = "crash", Relation = "предшествовало", LagMs = 60000 }
                },
                RootCauseNodeId = "b0",
                Summary = "Причина: startup · main-window-ready → отказ: wpf: COMException."
            }
        });

    [Theory]
    [InlineData("monach-coder/NexusMonachBrowser", true)]
    [InlineData("monach/crash-reports", true)]
    [InlineData("https://github.com/owner/repo", false)]
    [InlineData("owner", false)]
    [InlineData("owner/name/extra", false)]
    [InlineData("owner name/repo", false)]
    [InlineData("", false)]
    public void RepositoryName_Validated(string repository, bool expected)
    {
        Assert.Equal(expected, GitHubCrashReportTransport.IsValidRepository(repository));
    }

    [Fact]
    public void Signature_IsStableForSameCrashAndDistinctForAnother()
    {
        var first = GitHubCrashReportTransport.BuildSignature(
            "System.Runtime.InteropServices.COMException", RenderStack);
        var again = GitHubCrashReportTransport.BuildSignature(
            "System.Runtime.InteropServices.COMException", RenderStack + "\r\n   at Extra.Frame.After()");
        var other = GitHubCrashReportTransport.BuildSignature(
            "System.Windows.Markup.XamlParseException", RenderStack);

        Assert.Equal(first, again); // нижние кадры не меняют сигнатуру
        Assert.NotEqual(first, other);
        Assert.Equal(8, first.Length);
    }

    [Fact]
    public void Issue_IncludesSignatureInTitleAndMermaidInBody()
    {
        var (title, body) = GitHubCrashReportTransport.BuildIssue(Report());

        Assert.StartsWith("[Crash] wpf/dispatcher-unhandled: COMException [", title);
        Assert.EndsWith("]", title.Trim());

        Assert.Contains("```mermaid", body);
        Assert.Contains("graph TD", body); // причинный граф встроен и отрендерится GitHub'ом
        Assert.Contains("**Итог:** Причина: startup · main-window-ready", body);
        Assert.Contains("UCEERR_RENDERTHREADFAILURE", body); // стек в код-блоке
        Assert.Contains("| Версия | 2.9.0.0 |", body);        // сводная таблица
    }

    [Fact]
    public void Issue_WithoutCausalGraph_StillBuilds()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            ExceptionType = "System.Exception",
            Component = "tests",
            Stage = "unit",
            Message = "тест",
            StackTrace = "   at Tests.Fail()",
            BrowserVersion = "1.0"
        });
        var (title, body) = GitHubCrashReportTransport.BuildIssue(json);

        Assert.StartsWith("[Crash] tests/unit: Exception [", title);
        Assert.DoesNotContain("mermaid", body);
    }
}
