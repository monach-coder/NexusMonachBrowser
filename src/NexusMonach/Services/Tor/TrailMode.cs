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
    /// Применяет конфигурацию «Режима След»: Tor-прокси на loopback,
    /// строгая приватность, всё лишнее выключено.
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
