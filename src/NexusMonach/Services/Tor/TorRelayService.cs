using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NexusMonach.Services.Tor;

/// <summary>Состояние релейного моста.</summary>
public enum TorRelayState
{
    /// <summary>Мост не включён в настройках.</summary>
    Disabled,
    /// <summary>Tor не запущен — мост стартует вместе с ним.</summary>
    TorNotRunning,
    /// <summary>ORPort/транспорт слушаются локально; полезен при пробросе порта.</summary>
    Listening,
    /// <summary>Порты моста не видны — вероятен NAT без проброса.</summary>
    NotReachable
}

/// <summary>
/// Релейный мост: каждый работающий браузер с Tor помогает пользователям
/// цензурных сетей, работая obfs4-мостом. Безопасность прежде всего:
/// ExitPolicy всегда `reject *:*` — браузер НИКОГДА не становится
/// exit-узлом и не пропускает чужой выходной трафик. Мост публикуется
/// в BridgeDB (BridgeRelay 1), поэтому адрес попадает только к тем,
/// кто просит мосты, а не в публичный консенсус.
/// </summary>
public static class TorRelayService
{
    public const int DefaultOrPort = 9101;
    public const int DefaultObfs4Port = 9102;

    /// <summary>Санитизированный Nickname для torrc: [A-Za-z0-9_], до 19 символов.</summary>
    public static string SanitizeNickname(string nickname)
    {
        var builder = new StringBuilder();
        foreach (var c in (nickname ?? string.Empty).Trim())
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                builder.Append(c);
            if (builder.Length >= 19) break;
        }
        return builder.Length == 0 ? "NexusMonach" : builder.ToString();
    }

    /// <summary>Стабильный ник одной установки: NexusMonach-XXXX (4 hex).</summary>
    public static string DefaultNickname() =>
        "NexusMonach-" + Convert.ToHexString(
            RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    /// <summary>
    /// Строки релейного моста для torrc. Bridge-режим, никогда exit.
    /// Возвращает пустой список, если мост выключен в настройках.
    /// </summary>
    public static List<string> BuildRelayLines(
        bool enabled, string nickname, int orPort, int obfs4Port)
    {
        if (!enabled) return [];
        var lines = new List<string>
        {
            $"Nickname {SanitizeNickname(nickname)}",
            $"ORPort {orPort}",
            "BridgeRelay 1",
            // Абсолютный запрет выходного трафика: мост, а не exit.
            "ExitPolicy reject *:*",
            "ExtORPort auto"
        };
        var obfs4 = FindObfs4ServerExecutable();
        if (obfs4 is not null)
        {
            lines.Add($"ServerTransportPlugin obfs4 exec \"{obfs4}\"");
            lines.Add($"ServerTransportListenAddr obfs4 0.0.0.0:{obfs4Port}");
        }
        return lines;
    }

    /// <summary>Текущее состояние моста по локальным слушателям.</summary>
    public static TorRelayState GetState(bool enabled, int orPort, int obfs4Port)
    {
        if (!enabled) return TorRelayState.Disabled;
        if (!TorService.IsRunning) return TorRelayState.TorNotRunning;
        var or = IsListening(orPort);
        var transport = FindObfs4ServerExecutable() is null || IsListening(obfs4Port);
        return or && transport ? TorRelayState.Listening : TorRelayState.NotReachable;
    }

    /// <summary>Человекочитаемый статус для настроек и Дозора.</summary>
    public static string Describe(TorRelayState state, int orPort, int obfs4Port) => state switch
    {
        TorRelayState.Disabled => "Мост выключен",
        TorRelayState.TorNotRunning => "Мост стартует вместе с Tor",
        TorRelayState.Listening =>
            FindObfs4ServerExecutable() is null
                ? $"Мост работает: ORPort {orPort} (проброс порта на роутере делает его доступным цензурным пользователям)"
                : $"Мост работает: obfs4 {obfs4Port} + ORPort {orPort} — доступен цензурным пользователям при пробросе портов",
        TorRelayState.NotReachable =>
            $"Мост запущен, но порты {orPort}/{obfs4Port} не слушаются — проверьте настройки",
        _ => "Неизвестно"
    };

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(1000);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Серверный исполняемый файл obfs4. Lyrebird работает и как клиент,
    /// и как сервер обфускации — один бинарник на обе роли.
    /// </summary>
    internal static string? FindObfs4ServerExecutable()
    {
        string[] roots = [@"C:\Tor", Path.Combine(AppContext.BaseDirectory, "tor")];
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var name in new[] { "lyrebird.exe", "obfs4proxy.exe" })
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
                var transport = Path.Combine(root, "pluggable_transports", name);
                if (File.Exists(transport)) return transport;
            }
        }
        return null;
    }
}
