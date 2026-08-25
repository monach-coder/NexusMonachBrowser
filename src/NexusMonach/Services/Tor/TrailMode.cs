using NexusMonach.Models;

namespace NexusMonach.Services.Tor;

/// <summary>
/// «Режим След» — принудительная анонимная конфигурация браузера:
/// весь трафик через Tor SOCKS5 на loopback, строгая приватность,
/// отключение расширений, графа знаний и телеметрии. Один тумблер.
/// </summary>
public static class TrailMode
{
    /// <summary>
    /// Применяет конфигурацию «Режима След»: Tor SOCKS5 на loopback,
    /// строгая приватность, WebRTC выключен, DNS через Tor, порт-страж.
    /// </summary>
    public static BrowserSettings Apply(BrowserSettings settings)
    {
        settings.EnableCustomProxy = true;
        settings.ProxyKind = ProxyKind.Socks5;
        settings.ProxyHost = "127.0.0.1";
        settings.ProxyPort = TorService.SocksPort;
        settings.PrivacyLevel = PrivacyLevel.Strict;
        settings.EnablePasswordAutosave = false;
        settings.EnableGeneralAutofill = false;
        settings.SendDoNotTrack = true;
        settings.SendGlobalPrivacyControl = true;
        settings.StripTrackingParameters = true;
        settings.PreventWebRtcIpLeak = true;
        settings.BuildKnowledgeGraph = false;
        settings.EnableExtensions = false;
        settings.RestoreSession = false;
        settings.ClearBrowsingDataOnExit = true;
        return settings;
    }

    /// <summary>
    /// Активирует защиту портов на вкладке: WebRTC блокируется,
    /// DNS идёт через Tor SOCKS, mDNS/SSDP отключены.
    /// </summary>
    public static (bool Success, string Message) ProtectTab(Models.BrowserTab tab)
    {
        var vpn = VpnDetector.Detect();
        var guard = PortGuard.Protect(tab);
        var vpnText = vpn.VpnActive ? $" VPN: {vpn.AdapterName}." : "";
        return (guard.Success, guard.Message + vpnText);
    }

    /// <summary>
    /// Снимает защиту портов при выходе из режима.
    /// </summary>
    public static void ReleaseTab(Models.BrowserTab tab) => PortGuard.Release(tab);

    /// <summary>Человекочитаемый статус Tor для UI.</summary>
    public static (bool Ready, string Status) CheckTorStatus()
    {
        if (TorService.IsRunning)
        {
            var source = TorService.IsManaged ? "браузерный" : "внешний";
            return (true, $"Tor подключён ({source}, порт {TorService.SocksPort})");
        }
        return (false, "Tor не запущен — включите Tor или запустите через браузер");
    }
}
