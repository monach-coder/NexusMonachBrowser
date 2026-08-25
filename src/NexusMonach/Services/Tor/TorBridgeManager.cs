using NexusMonach.Models;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Менеджер мостов Tor: парсит адреса мостов из настроек браузера,
/// генерирует torrc с транспортом, запускает Tor и следит за здоровьем.
/// Пользователь добавляет мосты в настройках — браузер делает всё остальное.
/// </summary>
public static class TorBridgeManager
{
    /// <summary>
    /// Парсит строки мостов из поля настроек. Поддерживает форматы:
    /// obfs4 IP:PORT FINGERPRINT cert=... iat-mode=...
    /// snowflake IP:PORT FINGERPRINT url=... front=...
    /// meek_lite IP:PORT FINGERPRINT url=... front=...
    /// IP:PORT FINGERPRINT (прямой мост без транспорта)
    /// </summary>
    public static List<ParsedBridge> ParseBridges(string raw)
    {
        var bridges = new List<ParsedBridge>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                              StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("#")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var transport = parts[0].ToLowerInvariant() switch
            {
                "obfs4" => "obfs4",
                "snowflake" => "snowflake",
                "meek_lite" or "meek" => "meek_lite",
                "webtunnel" => "webtunnel",
                _ => "" // прямой мост без транспорта
            };

            var address = transport == "" ? parts[0] : parts[1];
            var fingerprint = transport == "" ? parts[1] : parts.ElementAtOrDefault(2) ?? "";
            var extras = string.Join(" ",
                parts.Skip(transport == "" ? 2 : 3));

            if (!address.Contains(':')) continue;

            bridges.Add(new ParsedBridge(transport, address, fingerprint, extras, line));
        }
        return bridges;
    }

    /// <summary>
    /// Генерирует полный torrc на основе пользовательских мостов, релейного
    /// моста и выбранного транспорта. Файл кладётся во временную папку —
    /// основной torrc не трогаем.
    /// </summary>
    public static string GenerateTorrc(
        List<ParsedBridge> bridges, int socksPort, bool relayEnabled = false,
        string relayNickname = "", int relayOrPort = TorRelayService.DefaultOrPort,
        int relayObfs4Port = TorRelayService.DefaultObfs4Port)
    {
        var lines = new List<string>
        {
            $"SocksPort 127.0.0.1:{socksPort} IsolateClientAddr",
            $"DataDirectory {Path.Combine(Path.GetTempPath(), "nexus-tor-data")}",
            "Log notice stdout",
            "CookieAuthentication 1",
            "DormantCanceledByStartup 1",
            "AvoidDiskWrites 1",
            "ConnectionPadding auto",
            "ReducedConnectionPadding 1",
            "NewCircuitPeriod 30"
        };

        var torDir = FindTorDirectory();
        if (torDir is not null)
        {
            var geoip = Path.Combine(torDir, "geoip");
            var geoip6 = Path.Combine(torDir, "geoip6");
            if (File.Exists(geoip)) lines.Add($"GeoIPFile {geoip}");
            if (File.Exists(geoip6)) lines.Add($"GeoIPv6File {geoip6}");
        }

        // Если есть мосты — подключаем транспорт и мосты.
        if (bridges.Count > 0)
        {
            lines.Add("UseBridges 1");

            // Транспортный плагин подключается по типу моста.
            var transports = bridges
                .Select(b => b.Transport)
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();

            var transportsDir = FindTransportsDirectory();
            if (transportsDir is not null)
            {
                foreach (var transport in transports)
                {
                    var exe = transport switch
                    {
                        "obfs4" => SafeJoin(transportsDir, "lyrebird.exe"),
                        "snowflake" => SafeJoin(transportsDir, "snowflake-client.exe"),
                        "webtunnel" => SafeJoin(transportsDir, "webtunnel-client.exe"),
                        _ => null
                    };
                    if (exe is not null && File.Exists(exe))
                    {
                        lines.Add($"ClientTransportPlugin {transport} exec \"{exe}\"");
                    }
                }
            }

            foreach (var bridge in bridges)
            {
                lines.Add($"Bridge {bridge.Original}");
            }
        }

        // Релейный мост: эта копия браузера помогает цензурным пользователям.
        lines.AddRange(TorRelayService.BuildRelayLines(
            relayEnabled, relayNickname, relayOrPort, relayObfs4Port));

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// Запускает Tor с мостами и релеем из настроек. Перезапускает, если Tor
    /// уже работает с другим конфигом. Сгенерированный torrc передаётся
    /// процессу явно — основной torrc пользователя не используется.
    /// </summary>
    public static async Task<TorState> RestartWithBridgesAsync(
        BrowserSettings settings, CancellationToken ct = default)
    {
        TorService.Stop();

        var bridges = ParseBridges(settings.TorCustomBridges);
        var torrcContent = GenerateTorrc(
            bridges, TorService.SocksPort,
            settings.TorRelayEnabled, settings.TorRelayNickname,
            settings.TorRelayOrPort, settings.TorRelayObfs4Port);

        // Записываем torrc во временную папку и стартуем Tor именно с ним.
        var dataDir = Path.Combine(Path.GetTempPath(), "nexus-tor-data");
        Directory.CreateDirectory(dataDir);
        var torrcPath = Path.Combine(dataDir, "torrc");
        await File.WriteAllTextAsync(torrcPath, torrcContent, ct);

        return await TorService.EnsureRunningAsync(torrcPath, ct);
    }

    private static string? FindTorDirectory()
    {
        if (Directory.Exists(@"C:\Tor")) return @"C:\Tor";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tor");
        if (Directory.Exists(bundled)) return bundled;
        return null;
    }

    private static string? FindTransportsDirectory()
    {
        var torDir = FindTorDirectory();
        if (torDir is null) return null;
        var transports = Path.Combine(torDir, "pluggable_transports");
        return Directory.Exists(transports) ? transports : null;
    }

    private static string SafeJoin(string dir, string file) =>
        Path.Combine(dir, file);
}

/// <summary>
/// Разобранный мост: тип транспорта, адрес, отпечаток, доп. параметры.
/// </summary>
public sealed record ParsedBridge(
    string Transport,
    string Address,
    string Fingerprint,
    string Extras,
    string Original);
