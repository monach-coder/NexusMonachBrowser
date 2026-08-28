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
    string StatusText)
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        vless = VlessEnabled && VlessRunning,
        vlessConfigured = VlessEnabled,
        tor = TorInChain,
        torWrapped = TorWrapped,
        proxy = ProxyEnabled,
        status = StatusText
    });
}

/// <summary>
/// Единая точка правды для переключателей маршрута: стартовая страница,
/// настройки и автостарт работают через эти методы. Тор не имеет прямого
/// выхода в сеть — при поднятом транспорте (Xray) он заворачивается в
/// сервер, иначе в системный VPN; без туннеля Тор ждёт, а браузер
/// продолжает работу с максимальной защитой, но с реальным IP.
/// Смена маршрута вкладок применяется к новым вкладкам после перезапуска:
/// прокси зашит в аргументы окружения WebView2.
/// </summary>
public static class NetworkChainService
{
    /// <summary>Текущее состояние цепочки одним снимком.</summary>
    public static NetworkChainSnapshot Snapshot()
    {
        var settings = SettingsService.Current;
        var vlessRunning = Services.Vless.VlessRuntime.IsRunning;
        var torRunning = Services.Tor.TorService.IsRunning;
        var wrapped = torRunning &&
                      (vlessRunning || Services.Tor.VpnDetector.DetectCached().VpnActive);
        return new NetworkChainSnapshot(
            settings.VlessEnabled, settings.TorInChain, settings.EnableCustomProxy,
            vlessRunning, torRunning, wrapped, Describe(settings, vlessRunning, wrapped));
    }

    /// <summary>
    /// Тумблер «Сервер» (VLESS): поднимает или останавливает транспорт,
    /// перезапускает Тора с новой обёрткой. Без ссылки профиля не включается.
    /// </summary>
    public static async Task<NetworkChainSnapshot> ToggleVlessAsync()
    {
        var settings = SettingsService.Current;
        if (!settings.VlessEnabled)
            return await EnsureVlessAsync();

        settings.VlessEnabled = false;
        await SettingsService.SaveAsync(settings);
        Services.Vless.VlessRuntime.Stop();
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
            Announce("Сервер подключён. Тор оборачивается в него."
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
            var snapshot = Snapshot();
            Announce(snapshot.TorWrapped
                ? "Тор в цепочке и обёрнут туннелем." + RouteChangeNote(settings)
                : "Тор ждёт туннель: сервер или VPN. Браузер работает с максимальной защитой, но IP реальный.");
            return Snapshot();
        }

        settings.TorInChain = false;
        await SettingsService.SaveAsync(settings);
        Announce("Тор исключён из цепочки вкладок — скорость выше." + RouteChangeNote(settings));
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
        Announce(settings.EnableCustomProxy
            ? "Ручной прокси включён." + RouteChangeNote(settings)
            : "Ручной прокси отключён.");
        return Snapshot();
    }

    /// <summary>
    /// Перезапускает Тора с текущей обёрткой: вызывается при изменениях
    /// транспорта, чтобы torrc подхватил или сбросил Socks5Proxy.
    /// </summary>
    public static Task RewrapTorAsync(BrowserSettings settings) =>
        settings.TorInChain || settings.TorRelayEnabled || settings.TrailModeEnabled
            ? Tor.TorBridgeManager.RestartWithBridgesAsync(settings)
            : Task.CompletedTask;

    private static string Describe(BrowserSettings settings, bool vlessRunning, bool wrapped)
    {
        if (settings.TorInChain && wrapped)
            return vlessRunning ? "Тор обёрнут сервером" : "Тор обёрнут VPN";
        if (settings.TorInChain)
            return "Тор ждёт туннель (сервер или VPN); IP реальный";
        return vlessRunning ? "Напрямую через сервер" : "Прямое соединение";
    }

    private static string RouteChangeNote(BrowserSettings settings) =>
        settings.TorInChain || settings.VlessEnabled || settings.EnableCustomProxy
            ? " Новые вкладки поедут по новому маршруту после перезапуска браузера."
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
