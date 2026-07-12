using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SiteRuleService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<SitePrivacyRule> _rules = [];

    public static async Task InitializeAsync() =>
        _rules = await JsonStore.ReadAsync<List<SitePrivacyRule>>(AppPaths.SiteRulesFile) ?? [];

    public static bool BypassesAdditionalBlocking(string? host)
    {
        host = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(host)) return false;
        return _rules.Any(x => x.BypassAdditionalTrackerBlocking && HostMatches(host, x.Host));
    }

    public static async Task SetBypassAsync(string host, bool bypass)
    {
        host = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(host)) return;

        await Gate.WaitAsync();
        try
        {
            var rule = _rules.FirstOrDefault(x => x.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (!bypass)
            {
                if (rule is not null)
                    _rules.Remove(rule);
            }
            else if (rule is null)
            {
                _rules.Add(new SitePrivacyRule
                {
                    Host = host,
                    BypassAdditionalTrackerBlocking = true,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                rule.BypassAdditionalTrackerBlocking = true;
                rule.UpdatedAtUtc = DateTime.UtcNow;
            }

            await JsonStore.WriteAsync(AppPaths.SiteRulesFile, _rules);
        }
        finally { Gate.Release(); }
    }

    private static string NormalizeHost(string? host) =>
        (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();

    private static bool HostMatches(string actual, string rule) =>
        actual.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        actual.EndsWith('.' + rule, StringComparison.OrdinalIgnoreCase);
}
