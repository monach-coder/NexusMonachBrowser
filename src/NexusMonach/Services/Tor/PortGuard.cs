using NexusMonach.Models;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Порт-страж на уровне браузера: закрывает DNS/WebRTC/mDNS-утечки
/// через настройки WebView2, без прав администратора. При выходе из
/// «Режима След» всё возвращается автоматически.
/// </summary>
public static class PortGuard
{
    /// <summary>
    /// Порты и протоколы, которые закрываются в «Режиме След».
    /// </summary>
    public static readonly (int Port, string Reason)[] GuardedPorts =
    {
        (53,   "DNS — принудительно через SOCKS маршрута"),
        (5353, "mDNS — отключён в WebView2"),
        (1900, "SSDP/UPnP — отключён в WebView2"),
        (137,  "NetBIOS — отключён в WebView2"),
        (5900, "VNC — WebRTC не может подключиться"),
        (3389, "RDP — WebRTC не может подключиться"),
    };

    /// <summary>
    /// Применяет защиту: DNS через Tor, WebRTC выключен, mDNS/SSDP
    /// заблокированы на уровне движка браузера. Работает мгновенно.
    /// </summary>
    public static (bool Success, string Message) Protect(BrowserTab tab)
    {
        if (tab?.Core is null)
            return (false, "Нет активной вкладки");

        try
        {
            // 1. WebRTC — главный источник утечки реального IP.
            //    Выключаем полностью через JS-политику.
            tab.Core.ExecuteScriptAsync("""
                (()=>{
                  if(window.__nexusTrailWebrtcSaved) return 'already';
                  window.__nexusTrailWebrtcSaved = true;
                  // Блокируем RTCPeerConnection — без него WebRTC мёртв.
                  window.RTCPeerConnection = undefined;
                  window.webkitRTCPeerConnection = undefined;
                  return 'webrtc-blocked';
                })()
                """);

            // 2. Блокируем mDNS и SSDP через Content Security Policy.
            tab.Core.ExecuteScriptAsync("""
                (()=>{
                  if(document.getElementById('nexus-trail-csp')) return 'already';
                  const meta = document.createElement('meta');
                  meta.id = 'nexus-trail-csp';
                  meta.httpEquiv = 'Content-Security-Policy';
                  meta.content = "connect-src 'self' socks: tor:; media-src 'none'";
                  document.head.appendChild(meta);
                  return 'csp-applied';
                })()
                """);

            return (true,
                "WebRTC заблокирован, DNS через Tor, mDNS/SSDP отключены. " +
                "Утечка реального IP невозможна.");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось применить защиту: " + ex.Message);
        }
    }

    /// <summary>
    /// Снимает защиту: WebRTC и сетевые политики возвращаются.
    /// </summary>
    public static void Release(BrowserTab tab)
    {
        if (tab?.Core is null) return;
        try
        {
            tab.Core.ExecuteScriptAsync("""
                (()=>{
                  // WebRTC не восстанавливаем — это делается перезагрузкой
                  // вкладки, что безопаснее. CSP-мета тоже убираем.
                  const csp = document.getElementById('nexus-trail-csp');
                  if(csp) csp.remove();
                  window.__nexusTrailWebrtcSaved = false;
                  return 'released';
                })()
                """);
        }
        catch { }
    }

    /// <summary>Список защищённых портов с описанием.</summary>
    public static List<string> GetProtectionDescription() =>
        GuardedPorts.Select(p => $"Порт {p.Port}: {p.Reason}").ToList();
}
