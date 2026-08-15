using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class SledopytDiagnosticsTests
{
    [Fact]
    public void ReportExplainsStartRulesAndGroupsStagesByAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        SledopytDiagnosticEntry[] entries =
        [
            Entry(now, "shopping", "requested", "success", "accepted", "run-one", "button-or-enter", "new-tab"),
            Entry(now.AddMilliseconds(5), "shopping", "blocked", "failed", "missing-query", "run-one", "button-or-enter", "new-tab"),
            Entry(now.AddSeconds(1), "site-research", "requested", "success", "accepted", "run-two", "omnibox", "search-provider"),
            Entry(now.AddSeconds(2), "site-research", "completed", "success", "ok", "run-two", "omnibox", "site")
        ];

        var report = SledopytDiagnosticsService.FormatForDisplay(entries);

        Assert.Contains("КОГДА ОН СТАРТУЕТ", report);
        Assert.Contains("Попыток: 2", report);
        Assert.Contains("заблокировано до старта: 1", report);
        Assert.Contains("кнопка «Начать поиск» / Enter", report);
        Assert.Contains("не введён запрос", report);
        Assert.DoesNotContain("secret search phrase", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyJournalStagesAreReconstructedAsOneAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        SledopytDiagnosticEntry[] entries =
        [
            Entry(now, "site-research", "started", "success", "ok", "legacy", "unknown", "unknown"),
            Entry(now.AddSeconds(1), "site-research", "page-read", "success", "ok", "legacy", "unknown", "unknown"),
            Entry(now.AddSeconds(2), "site-research", "completed", "success", "ok", "legacy", "unknown", "unknown")
        ];

        var report = SledopytDiagnosticsService.FormatForDisplay(entries);

        Assert.Contains("Попыток: 1", report);
        Assert.Contains("успешно: 1", report);
    }

    private static SledopytDiagnosticEntry Entry(DateTimeOffset time, string operation, string stage,
        string outcome, string code, string runId, string trigger, string surface) => new()
    {
        TimestampUtc = time,
        Operation = operation,
        Stage = stage,
        Outcome = outcome,
        Code = code,
        RunId = runId,
        Trigger = trigger,
        Surface = surface
    };
}
