using System.Text.Json.Serialization;
using NexusMonach.Services;

namespace NexusMonach.Models;

public enum PrivacyLevel
{
    Basic,
    Balanced,
    Strict
}

public enum SearchEngineKind
{
    DuckDuckGo,
    Brave,
    Startpage,
    Google,
    Yandex,
    Bing,
    Mojeek
}

public enum ProxyKind
{
    Http,
    Socks5
}

public enum SecureDnsMode
{
    System,
    Automatic,
    Strict
}

public enum SecureDnsProvider
{
    Cloudflare,
    Quad9
}

public enum CrashReportMode
{
    AskBeforeSending,
    AutomaticAnonymous,
    LocalOnly
}

public enum CrashReportDestination
{
    HttpsCollector,
    GitHubIssues
}

public enum BrowserTheme
{
    MonachAqua,
    Ocean,
    Forest,
    Amethyst
}

public enum BrowserThemeMode
{
    Dark,
    Light
}

public enum VoiceAssistantMode
{
    Off,
    ImportantOnly,
    Assistant
}

public enum NeuralVoiceProfile
{
    Natasha,   // Ксения — женский (по умолчанию)
    Irina,     // Ирина — женский (спокойный)
    Aurora,    // Аврора — женский (выразительный)
    Eugene     // Евгений — мужской
}

public enum VideoTranslationMode
{
    Fast,
    Balanced,
    Quality
}

/// <summary>Транспорт для обхода DPI-блокировки Tor.</summary>
public enum TorTransportMode
{
    /// <summary>Напрямую, без обфускации — работает только там, где Tor не заблокирован.</summary>
    Direct,
    /// <summary>obfs4 через lyrebird — трафик выглядит как случайный шум. По умолчанию.</summary>
    Obfs4,
    /// <summary>Snowflake — трафик выглядит как WebRTC-видеозвонок. Тяжело заблокировать.</summary>
    Snowflake,
    /// <summary>meek-azure — трафик выглядит как HTTPS к Microsoft CDN. Почти не заблокировать, но медленно.</summary>
    MeekAzure
}

public sealed class BrowserSettings
{
    public SearchEngineKind SearchEngine { get; set; } = SearchEngineKind.DuckDuckGo;
    public PrivacyLevel PrivacyLevel { get; set; } = PrivacyLevel.Balanced;
    public bool SendDoNotTrack { get; set; } = true;
    public bool SendGlobalPrivacyControl { get; set; } = true;
    public bool StripTrackingParameters { get; set; } = true;
    public bool BlockNotifications { get; set; } = true;
    // Старое JSON-имя сохраняется, чтобы обновление не сбрасывало выбор пользователя.
    [JsonPropertyName("SaveHistory")]
    public bool BuildKnowledgeGraph { get; set; } = true;
    // Оставлено для чтения прежних settings.json. Обычный сеанс больше не
    // сохраняется; восстановление применяется только к DPAPI-снимку Guardian.
    public bool RestoreSession { get; set; }
    public bool ClearBrowsingDataOnExit { get; set; }
    public bool EnableExtensions { get; set; } = true;
    public bool EnableDevTools { get; set; }
    public bool EnablePasswordAutosave { get; set; }
    public bool EnableGeneralAutofill { get; set; }
    public bool MemorySaver { get; set; } = true;
    /// <summary>Панель «Приватность и защита» внизу окна. По умолчанию скрыта;
    /// включается галочкой в настройках сети или кнопкой в меню.</summary>
    public bool ShowPrivacyMonitor { get; set; }
    public bool PreventWebRtcIpLeak { get; set; } = true;
    public bool HttpsFirstEnabled { get; set; } = true;
    public SecureDnsMode SecureDnsMode { get; set; } = SecureDnsMode.Strict;
    public SecureDnsProvider SecureDnsProvider { get; set; } = SecureDnsProvider.Cloudflare;
    public bool EnableCustomProxy { get; set; }
    public ProxyKind ProxyKind { get; set; } = ProxyKind.Socks5;
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 9050;
    public string ProxyBypassList { get; set; } = string.Empty;
    /// <summary>Собственный сервер (VLESS): направлять трафик через транспорт пользователя.</summary>
    public bool VlessEnabled { get; set; }
    /// <summary>Ссылка профиля vless:// — выдаёт администратор сервера.</summary>
    public string VlessProfileUri { get; set; } = string.Empty;
    /// <summary>
    /// Тор — часть цепочки вкладок: браузер → Тор → (Xray или VPN) → интернет.
    /// Прямого выхода у Тора нет — он всегда оборачивается транспортом.
    /// Выключение исключает Тор из маршрута вкладок ради скорости: трафик
    /// идёт напрямую через Xray или системный VPN.
    /// </summary>
    public bool TorInChain { get; set; } = true;
    /// <summary>
    /// Управляющий выходом в сеть: следит за живостью прямого пути и, когда
    /// тот умирает, сам поднимает настроенный сервер (VLESS) и голосом ведёт
    /// пользователя по лестнице маршрутов. Ручные тумблеры всегда важнее.
    /// </summary>
    public bool AutoNetworkGovernor { get; set; } = true;
    public string HomePage { get; set; } = "app://newtab";
    public CrashReportMode CrashReportMode { get; set; } = CrashReportMode.LocalOnly;
    public CrashReportDestination CrashReportDestination { get; set; } = CrashReportDestination.HttpsCollector;
    public string CrashReportEndpoint { get; set; } = string.Empty;
    /// <summary>Репозиторий приёма крашей в формате «владелец/имя».</summary>
    public string GitHubRepository { get; set; } = "monach-coder/NexusMonachBrowser-crash-reports";
    /// <summary>
    /// URL подписанного манифеста сетевой поставки AI-моделей. Пусто —
    /// модели не подтягиваются (полностью офлайн-режим).
    /// </summary>
    public string AiPackManifestUrl { get; set; } = string.Empty;
    /// <summary>
    /// Порт-щит. По умолчанию — тихие уведомления: автозакрытие требует UAC
    /// и на первом запуске пугает; включается осознанно в настройках.
    /// </summary>
    public PortShieldMode PortShieldMode { get; set; } = PortShieldMode.NotifyOnly;
    /// <summary>Релейный мост Tor: эта копия браузера помогает цензурным пользователям.
    /// По умолчанию выключен — включение осознанное, в настройках или мастере первого запуска.</summary>
    public bool TorRelayEnabled { get; set; }
    /// <summary>Пользователь видел предупреждение о мосте и подтвердил осознанно.</summary>
    public bool TorRelayAcknowledged { get; set; }
    /// <summary>Сетевой Дозор: ловушки, обман сканеров и стражи ARP/DNS при старте.</summary>
    public bool NetworkWatchdogEnabled { get; set; } = true;
    public string TorRelayNickname { get; set; } = string.Empty;
    public int TorRelayOrPort { get; set; } = Services.Tor.TorRelayService.DefaultOrPort;
    public int TorRelayObfs4Port { get; set; } = Services.Tor.TorRelayService.DefaultObfs4Port;
    public bool InitialProtectionSetupShown { get; set; }
    public BrowserTheme Theme { get; set; } = BrowserTheme.MonachAqua;
    public BrowserThemeMode ThemeMode { get; set; } = BrowserThemeMode.Dark;
    public bool ThemeSelectionCompleted { get; set; }
    public VoiceAssistantMode VoiceAssistantMode { get; set; } = VoiceAssistantMode.ImportantOnly;
    public bool VoiceSpeakAtStartup { get; set; } = true;
    public bool VoiceHandsFreeEnabled { get; set; } = false;
    public int VoiceRate { get; set; } = 0;
    public NeuralVoiceProfile NeuralVoiceProfile { get; set; } = NeuralVoiceProfile.Natasha;
    public VideoTranslationMode VideoTranslationMode { get; set; } = VideoTranslationMode.Balanced;
    public bool TrailModeEnabled { get; set; }
    public TorTransportMode TorTransport { get; set; } = TorTransportMode.Obfs4;
    public bool TorBridgeEnabled { get; set; }
    /// <summary>Приватные мосты пользователя (obfs4 IP:PORT KEY cert=...).</summary>
    public string TorCustomBridges { get; set; } = string.Empty;
    /// <summary>
    /// Пул приватных мостов (по одному в строке): каждая сессия берёт случайный,
    /// если строка ручных мостов пуста. Публичные списки не используются —
    /// выложенное в открытый доступ выгорает первым.
    /// </summary>
    public string TorBridgePool { get; set; } = string.Empty;

    public BrowserSettings Clone() => new()
    {
        SearchEngine = SearchEngine,
        PrivacyLevel = PrivacyLevel,
        SendDoNotTrack = SendDoNotTrack,
        SendGlobalPrivacyControl = SendGlobalPrivacyControl,
        StripTrackingParameters = StripTrackingParameters,
        BlockNotifications = BlockNotifications,
        BuildKnowledgeGraph = BuildKnowledgeGraph,
        RestoreSession = RestoreSession,
        ClearBrowsingDataOnExit = ClearBrowsingDataOnExit,
        EnableExtensions = EnableExtensions,
        EnableDevTools = EnableDevTools,
        EnablePasswordAutosave = EnablePasswordAutosave,
        EnableGeneralAutofill = EnableGeneralAutofill,
        MemorySaver = MemorySaver,
        ShowPrivacyMonitor = ShowPrivacyMonitor,
        PreventWebRtcIpLeak = PreventWebRtcIpLeak,
        HttpsFirstEnabled = HttpsFirstEnabled,
        SecureDnsMode = SecureDnsMode,
        SecureDnsProvider = SecureDnsProvider,
        EnableCustomProxy = EnableCustomProxy,
        ProxyKind = ProxyKind,
        ProxyHost = ProxyHost,
        ProxyPort = ProxyPort,
        ProxyBypassList = ProxyBypassList,
        VlessEnabled = VlessEnabled,
        VlessProfileUri = VlessProfileUri,
        TorInChain = TorInChain,
        HomePage = HomePage,
        CrashReportMode = CrashReportMode,
        CrashReportDestination = CrashReportDestination,
        CrashReportEndpoint = CrashReportEndpoint,
        GitHubRepository = GitHubRepository,
        AiPackManifestUrl = AiPackManifestUrl,
        PortShieldMode = PortShieldMode,
        TorRelayEnabled = TorRelayEnabled,
        TorRelayAcknowledged = TorRelayAcknowledged,
        NetworkWatchdogEnabled = NetworkWatchdogEnabled,
        TorRelayNickname = TorRelayNickname,
        TorRelayOrPort = TorRelayOrPort,
        TorRelayObfs4Port = TorRelayObfs4Port,
        InitialProtectionSetupShown = InitialProtectionSetupShown,
        Theme = Theme,
        ThemeMode = ThemeMode,
        ThemeSelectionCompleted = ThemeSelectionCompleted,
        VoiceAssistantMode = VoiceAssistantMode,
        VoiceSpeakAtStartup = VoiceSpeakAtStartup,
        VoiceHandsFreeEnabled = VoiceHandsFreeEnabled,
        VoiceRate = VoiceRate,
        NeuralVoiceProfile = NeuralVoiceProfile,
        VideoTranslationMode = VideoTranslationMode
    };
}
