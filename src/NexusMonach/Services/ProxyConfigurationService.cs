using System.Net;
using System.Text.RegularExpressions;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static partial class ProxyConfigurationService
{
    public static string BuildBrowserArguments(BrowserSettings settings)
    {
        var arguments = new List<string>();
        if (settings.PreventWebRtcIpLeak)
            arguments.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");

        if (!settings.EnableCustomProxy)
            return string.Join(' ', arguments);

        if (!TryValidate(settings.ProxyHost, settings.ProxyPort, out var error))
            throw new InvalidOperationException("Неверная настройка прокси: " + error);

        var scheme = settings.ProxyKind == ProxyKind.Socks5 ? "socks5" : "http";
        var host = FormatHost(settings.ProxyHost.Trim());
        var bypass = BuildBypassList(settings.ProxyBypassList);
        arguments.Add($"--proxy-server=\"{scheme}://{host}:{settings.ProxyPort}\"");
        arguments.Add($"--proxy-bypass-list=\"{bypass}\"");
        return string.Join(' ', arguments);
    }

    public static bool TryValidate(string? host, int port, out string error)
    {
        host = host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "укажите IP-адрес или имя сервера";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "порт должен находиться в диапазоне 1–65535";
            return false;
        }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out _) && !SafeHostPattern().IsMatch(host))
        {
            error = "имя сервера содержит недопустимые символы";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string BuildBypassList(string? value)
    {
        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "<local>", "localhost", "127.0.0.1", "[::1]", "nexus.local"
        };
        foreach (var raw in (value ?? string.Empty).Split([';', ',', '\r', '\n', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = NormalizeDomain(raw);
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            rules.Add(candidate);
            if (!candidate.StartsWith("*.", StringComparison.Ordinal) &&
                !IPAddress.TryParse(candidate.Trim('[', ']'), out _))
                rules.Add("*." + candidate);
        }
        return string.Join(';', rules);
    }

    private static string NormalizeDomain(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value,
                UriKind.Absolute, out var uri))
            value = uri.Host;
        value = value.Trim().Trim('.');
        return SafeBypassPattern().IsMatch(value) ? value : string.Empty;
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + host.Trim('[', ']') + "]"
            : host;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex SafeHostPattern();

    [GeneratedRegex(@"^(?:\*\.)?[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$|^[0-9A-Fa-f:.\[\]]+$")]
    private static partial Regex SafeBypassPattern();
}
