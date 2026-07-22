using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Builds process-scoped WebView2 network settings. Resolver templates are
/// fixed in code so a modified settings file cannot turn DNS into an arbitrary
/// HTTPS exfiltration endpoint. These resolvers do not apply category filters.
/// </summary>
public static class SecureNetworkConfigurationService
{
    private const string CloudflareTemplate = "https://cloudflare-dns.com/dns-query";
    private const string Quad9Template = "https://dns.quad9.net/dns-query";

    public static string BuildBrowserArguments(BrowserSettings settings)
    {
        var arguments = new List<string>();
        var proxyArguments = ProxyConfigurationService.BuildBrowserArguments(settings);
        if (!string.IsNullOrWhiteSpace(proxyArguments)) arguments.Add(proxyArguments);

        if (settings.SecureDnsMode != SecureDnsMode.System)
        {
            var mode = settings.SecureDnsMode == SecureDnsMode.Strict ? "secure" : "automatic";
            arguments.Add($"--dns-over-https-mode={mode}");
            arguments.Add($"--dns-over-https-templates=\"{GetTemplate(settings.SecureDnsProvider)}\"");
        }

        return string.Join(' ', arguments);
    }

    public static string Describe(BrowserSettings settings) => settings.SecureDnsMode switch
    {
        SecureDnsMode.System => "DNS системы Windows · шифрование зависит от ОС/VPN",
        SecureDnsMode.Automatic =>
            $"DoH автоматически · {GetProviderName(settings.SecureDnsProvider)} · возможен системный fallback",
        _ => $"DoH строгий · {GetProviderName(settings.SecureDnsProvider)} · без DNS fallback"
    };

    public static string GetProviderName(SecureDnsProvider provider) => provider switch
    {
        SecureDnsProvider.Quad9 => "Quad9 Secure",
        _ => "Cloudflare 1.1.1.1"
    };

    internal static string GetTemplate(SecureDnsProvider provider) => provider switch
    {
        SecureDnsProvider.Quad9 => Quad9Template,
        _ => CloudflareTemplate
    };
}
