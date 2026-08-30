using System.Net;
using System.Net.Sockets;

namespace NexusMonach.Services;

/// <summary>Действие управляющего при текущей живости выхода.</summary>
public enum GovernorStep
{
    /// <summary>Ничего не делать: выход есть (прямой или через системный туннель).</summary>
    EgressOk,
    /// <summary>Прямой путь умер, профиль сервера настроен — поднять транспорт.</summary>
    StartServer,
    /// <summary>Прямой путь умер, сервер не настроен, клиент WARP есть — предложить.</summary>
    SuggestWarp,
    /// <summary>Выхода нет совсем — честно сказать голосом.</summary>
    NoEgress
}

/// <summary>
/// Управляющий выходом в сеть: периодически щупает живость прямого пути
/// (TCP до 1.1.1.1 по IP-литералу, без DNS и прокси) и, когда тот умирает,
/// действует по лестнице: настроенный сервер (VLESS) поднимается сам,
/// установленному клиенту WARP предлагается подключиться, в худшем случае
/// честное голосовое «сети нет». Переходы озвучиваются однократно, не спамят.
/// Ручные тумблеры пользователя всегда приоритетнее автомата: управляющий
/// включается тумблером «Авто» на стартовой странице.
/// </summary>
public static class NetworkGovernor
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Timer? _timer;
    private static GovernorStep _lastStep = GovernorStep.EgressOk;
    private static volatile bool _recoveringServer;
    private static DateTimeOffset _lastNoEgressUtc = DateTimeOffset.MinValue;

    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(async _ => await GovernAsync(), null,
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25));
    }

    public static void Stop() => _timer?.Dispose();

    /// <summary>
    /// Чистая решающая функция — проверяется юнит-тестами. Порядок лестницы:
    /// выход есть → покой; сервер настроен и не поднят → поднять; клиент
    /// WARP установлен → предложить; иначе — честное «нет выхода».
    /// </summary>
    internal static GovernorStep Decide(
        bool directOk, bool warpConnected, bool warpInstalled,
        bool vlessConfigured, bool vlessRunning) =>
        directOk || warpConnected ? GovernorStep.EgressOk :
        vlessRunning ? GovernorStep.EgressOk :
        vlessConfigured ? GovernorStep.StartServer :
        warpInstalled ? GovernorStep.SuggestWarp :
        GovernorStep.NoEgress;

    private static async Task GovernAsync()
    {
        if (!SettingsService.Current.AutoNetworkGovernor) return;
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var directOk = await ProbeDirectAsync();
            var step = Decide(directOk,
                Services.Warp.WarpService.IsConnected,
                Services.Warp.WarpService.IsInstalled,
                !string.IsNullOrWhiteSpace(SettingsService.Current.VlessProfileUri),
                Services.Vless.VlessRuntime.IsRunning);

            if (step == _lastStep && step != GovernorStep.NoEgress) return;

            switch (step)
            {
                case GovernorStep.StartServer when !_recoveringServer:
                    _recoveringServer = true;
                    Announce("Прямой путь в сеть не отвечает. Поднимаю настроенный сервер.");
                    try { await NetworkChainService.EnsureVlessAsync(); }
                    finally { _recoveringServer = false; }
                    break;
                case GovernorStep.SuggestWarp:
                    Announce("Прямой путь не отвечает, сервер не настроен. Клиент WARP установлен — подключите его, я подхвачу автоматически.");
                    break;
                case GovernorStep.NoEgress:
                    // Самый частый и бесполезный звук — повторное «сети нет»:
                    // говорим раз в десять минут, не чаще.
                    if (DateTimeOffset.UtcNow - _lastNoEgressUtc < TimeSpan.FromMinutes(10)) return;
                    _lastNoEgressUtc = DateTimeOffset.UtcNow;
                    Announce("Не вижу выхода в сеть: прямой путь не отвечает, сервер не настроен. Проверьте подключение или включите туннель.", critical: true);
                    break;
            }

            if (_lastStep != GovernorStep.EgressOk && step == GovernorStep.EgressOk)
                Announce("Прямой путь в сеть восстановился.");
            _lastStep = step;
        }
        catch
        {
            // Управляющий не имеет права ронять или тревожить браузер.
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Живость прямого выхода: TCP до 1.1.1.1 по IP-литералу, без DNS и прокси.</summary>
    private static async Task<bool> ProbeDirectAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Announce(string text, bool critical = false) =>
        Ui.Post(() => VoiceAssistantService.Announce(text,
            critical ? VoiceAnnouncementPriority.Critical : VoiceAnnouncementPriority.Important));
}
