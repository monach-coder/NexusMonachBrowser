using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Снимок состояния сетевой цепочки для кнопок стартовой страницы
/// и настроек: сервер (VLESS), Тор в цепочке, ручной прокси.
/// </summary>
public sealed record NetworkChainSnapshot(
    bool VlessEnabled,
    bool TorInChain,
    bool ProxyEnabled,
    bool VlessRunning,
    bool TorRunning,
    bool TorWrapped,
    bool WarpInstalled,
    bool WarpConnected,
    bool AutoGovernor,
    string StatusText)
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        vless = VlessEnabled && VlessRunning,
        vlessConfigured = VlessEnabled,
        tor = TorInChain,
        torWrapped = TorWrapped,
        proxy = ProxyEnabled,
        warpInstalled = WarpInstalled,
        warp = WarpConnected,
        auto = AutoGovernor,
        status = StatusText
    });
}

/// <summary>
/// Единая точка правды для переключателей маршрута: стартовая страница,
/// настройки и автостарт работают через эти методы. Тор не имеет прямого
/// выхода в сеть — при поднятом транспорте (Xray) он заворачивается в
/// сервер, иначе в системный VPN; без туннеля Тор ждёт, а браузер
/// продолжает работу с максимальной защитой, но с реальным IP.
/// Маршрут живёт во встроенном маршрутизаторе цепочки: смена тумблера
/// применяется к новым соединениям сразу, перезапуск не нужен.
/// </summary>
public static class NetworkChainService
{
    /// <summary>Текущее состояние цепочки одним снимком.</summary>
    public static NetworkChainSnapshot Snapshot()
    {
        var settings = SettingsService.Current;
        var vlessRunning = Services.Vless.VlessRuntime.IsRunning;
        var torRunning = Services.Tor.TorService.IsRunning;
        var warpConnected = Services.Warp.WarpService.IsConnected;
        var wrapped = torRunning &&
                      (vlessRunning || warpConnected ||
                       Services.Tor.VpnDetector.DetectCached().VpnActive);
        return new NetworkChainSnapshot(
            settings.VlessEnabled, settings.TorInChain, settings.EnableCustomProxy,
            vlessRunning, torRunning, wrapped,
            Services.Warp.WarpService.IsInstalled, warpConnected,
            settings.AutoNetworkGovernor,
            Describe(settings, vlessRunning, wrapped));
    }

    /// <summary>Тумблер «Авто»: управляющий выходом в сеть вкл/выкл.</summary>
    public static async Task<NetworkChainSnapshot> ToggleAutoAsync()
    {
        var settings = SettingsService.Current;
        settings.AutoNetworkGovernor = !settings.AutoNetworkGovernor;
        await SettingsService.SaveAsync(settings);
        if (settings.AutoNetworkGovernor)
        {
            Services.NetworkGovernor.Start();
            Announce("Управляющий включён: слежу за выходом в сеть, при обрыве подниму сервер сам.");
        }
        else
        {
            Services.NetworkGovernor.Stop();
            Announce("Управляющий выключен. Маршрутом управляете вручную.");
        }
        return Snapshot();
    }

    /// <summary>Тумблер «Сервер» (VLESS): поднимает или останавливает транспорт,
    /// перезапускает Тора с новой обёрткой. Без ссылки профиля не включается.</summary>
    public static async Task<NetworkChainSnapshot> ToggleVlessAsync()
    {
        var settings = SettingsService.Current;
        if (!settings.VlessEnabled)
            return await EnsureVlessAsync();

        settings.VlessEnabled = false;
        await SettingsService.SaveAsync(settings);
        Services.Vless.VlessRuntime.Stop();
        RerouteNow();
        Announce("Сервер отключён." + RouteChangeNote(settings));
        await RewrapTorAsync(settings);
        return Snapshot();
    }

    /// <summary>
    /// Подключает сервер независимо от текущего состояния: для кнопки
    /// «Проверить и подключить» в настройках и повторных подключений.
    /// </summary>
    public static async Task<NetworkChainSnapshot> EnsureVlessAsync()
    {
        var settings = SettingsService.Current;
        if (!Services.Vless.VlessProfile.TryParse(settings.VlessProfileUri,
                out var profile, out var error) || profile is null)
        {
            Announce("Сначала вставьте ссылку сервера в настройках: " + error);
            return Snapshot();
        }
        if (Services.Vless.VlessRuntime.FindXray() is null)
        {
            Announce("Скачиваю транспортный модуль сервера.");
            var (ok, message) = await Services.Vless.VlessPackService.EnsureInstalledAsync();
            if (!ok)
            {
                Announce(message, critical: true);
                return Snapshot();
            }
        }
        var state = await Services.Vless.VlessRuntime.EnsureRunningAsync(profile);
        if (state != Services.Vless.VlessState.Connected)
        {
            Announce("Транспорт сервера не поднялся: " + StateText(state));
            return Snapshot();
        }
        var wasEnabled = settings.VlessEnabled;
        settings.VlessEnabled = true;
        await SettingsService.SaveAsync(settings);
        if (!wasEnabled)
        {
            RerouteNow();
            Announce("Сервер подключён. Анонимный слой оборачивается в него."
                + RouteChangeNote(settings));
            await RewrapTorAsync(settings);
        }
        return Snapshot();
    }

    /// <summary>
    /// Тумблер «Тор в цепочке»: включает или исключает слой Тора из маршрута
    /// вкладок. Сам сервис Тора продолжает крутиться в браузере — обёртка
    /// и релейный мост от этого тумблера не зависят.
    /// </summary>
    public static async Task<NetworkChainSnapshot> ToggleTorAsync()
    {
        var settings = SettingsService.Current;
        if (!settings.TorInChain)
        {
            settings.TorInChain = true;
            await SettingsService.SaveAsync(settings);
            await Tor.TorBridgeManager.RestartWithBridgesAsync(settings);
            RerouteNow();
            var snapshot = Snapshot();
            Announce(snapshot.TorWrapped
                ? "Анонимный слой в цепочке и обёрнут туннелем." + RouteChangeNote(settings)
                : "Анонимный слой ждёт туннель: сервер или системный туннель. Браузер работает с максимальной защитой, но IP реальный.");
            return Snapshot();
        }

        settings.TorInChain = false;
        await SettingsService.SaveAsync(settings);
        RerouteNow();
        Announce("Анонимный слой исключён из цепочки вкладок — скорость выше." + RouteChangeNote(settings));
        return Snapshot();
    }

    /// <summary>Тумблер ручного прокси из настроек.</summary>
    public static async Task<NetworkChainSnapshot> ToggleProxyAsync()
    {
        var settings = SettingsService.Current;
        settings.EnableCustomProxy = !settings.EnableCustomProxy;
        if (settings.EnableCustomProxy &&
            !ProxyConfigurationService.TryValidate(settings.ProxyHost, settings.ProxyPort, out var error))
        {
            settings.EnableCustomProxy = false;
            Announce("Прокси не включён: " + error);
            return Snapshot();
        }
        await SettingsService.SaveAsync(settings);
        RerouteNow();
        Announce(settings.EnableCustomProxy
            ? "Ручной прокси включён." + RouteChangeNote(settings)
            : "Ручной прокси отключён.");
        return Snapshot();
    }

    /// <summary>
    /// Мгновенная переброска: рвёт живые туннели вкладок, чтобы движок не
    /// катался по старому маршруту на своих keep-alive сокетах. Новые
    /// соединения маршрутизатор уже отправит по-новому.
    /// </summary>
    private static void RerouteNow() => Services.Chain.ChainRouterService.DropAllTunnels();

    /// <summary>
    /// Перезапускает Тора с текущей обёрткой: вызывается при изменениях
    /// транспорта, чтобы torrc подхватил или сбросил Socks5Proxy.
    /// </summary>
    public static Task RewrapTorAsync(BrowserSettings settings) =>
        settings.TorInChain || settings.TorRelayEnabled || settings.TrailModeEnabled
            ? Tor.TorBridgeManager.RestartWithBridgesAsync(settings)
            : Task.CompletedTask;

    /// <summary>
    /// Кнопка WARP на стартовой странице: подключением управляет сам
    /// официальный клиент (иконка в трее) — браузер читает состояние адаптера
    /// и заворачивает в туннель анонимный слой. Здесь — пояснение и статус.
    /// </summary>
    public static Task<NetworkChainSnapshot> WarpButtonAsync()
    {
        var snapshot = Snapshot();
        if (!snapshot.WarpInstalled)
            Announce("Клиент Cloudflare WARP не найден. Установите официальный клиент — браузер подхватит туннель сам и завернёт в него анонимный слой.");
        else if (snapshot.WarpConnected)
            Announce("Туннель WARP активен. Анонимный слой может заворачиваться в него; управление подключением — в клиенте WARP.");
        else
            Announce("Клиент WARP установлен, туннель опущен. Подключите его в собственном клиенте — браузер подхватит сам.");
        return Task.FromResult(snapshot);
    }

    private static string Describe(BrowserSettings settings, bool vlessRunning, bool wrapped)
    {
        if (settings.TorInChain && wrapped)
            return vlessRunning ? "Слой обёрнут сервером" : "Слой обёрнут туннелем";
        if (settings.TorInChain)
            return "Слой ждёт туннель (сервер или системный); IP реальный";
        return vlessRunning ? "Напрямую через сервер" : "Прямое соединение";
    }

    private static string RouteChangeNote(BrowserSettings settings) =>
        settings.TorInChain || settings.VlessEnabled || settings.EnableCustomProxy
            ? " Встроенный маршрутизатор применит маршрут к новым соединениям сразу, без перезапуска."
            : string.Empty;

    private static void Announce(string text, bool critical = false) =>
        Ui.Post(() => VoiceAssistantService.Announce(text,
            critical ? VoiceAnnouncementPriority.Critical : VoiceAnnouncementPriority.Important));

    private static string StateText(Services.Vless.VlessState state) => state switch
    {
        Services.Vless.VlessState.NotInstalled => "модуль не установлен",
        Services.Vless.VlessState.Starting => "не успел подняться, попробуйте ещё раз",
        Services.Vless.VlessState.Failed => "ошибка запуска, подробности в журнале",
        _ => state.ToString().ToLowerInvariant()
    };
}
