using System.Text.Json.Serialization;

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
    MatrixDirect
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
    public bool RestoreSession { get; set; } = true;
    public bool ClearBrowsingDataOnExit { get; set; }
    public bool EnableExtensions { get; set; } = true;
    public bool EnableDevTools { get; set; }
    public bool EnablePasswordAutosave { get; set; }
    public bool EnableGeneralAutofill { get; set; }
    public bool MemorySaver { get; set; } = true;
    public bool ShowPrivacyMonitor { get; set; } = true;
    public bool PreventWebRtcIpLeak { get; set; } = true;
    public bool HttpsFirstEnabled { get; set; } = true;
    public SecureDnsMode SecureDnsMode { get; set; } = SecureDnsMode.Strict;
    public SecureDnsProvider SecureDnsProvider { get; set; } = SecureDnsProvider.Cloudflare;
    public bool EnableCustomProxy { get; set; }
    public ProxyKind ProxyKind { get; set; } = ProxyKind.Socks5;
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 9050;
    public string ProxyBypassList { get; set; } = string.Empty;
    public string HomePage { get; set; } = "app://newtab";
    public CrashReportMode CrashReportMode { get; set; } = CrashReportMode.LocalOnly;
    public CrashReportDestination CrashReportDestination { get; set; } = CrashReportDestination.HttpsCollector;
    public string CrashReportEndpoint { get; set; } = string.Empty;
    public string MatrixHomeserver { get; set; } = string.Empty;
    public string MatrixRoomId { get; set; } = string.Empty;

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
        HomePage = HomePage,
        CrashReportMode = CrashReportMode,
        CrashReportDestination = CrashReportDestination,
        CrashReportEndpoint = CrashReportEndpoint,
        MatrixHomeserver = MatrixHomeserver,
        MatrixRoomId = MatrixRoomId
    };
}
