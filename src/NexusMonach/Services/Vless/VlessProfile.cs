namespace NexusMonach.Services.Vless;

/// <summary>
/// Профиль подключения VLESS — разобранная ссылка «vless://…», которую
/// выдаёт администратор сервера. Хранит поля, необходимые для генерации
/// конфигурации транспорта (Xray): адрес, пользователя и параметры
/// маскировки Reality/TLS/WebSocket.
/// </summary>
public sealed record VlessProfile
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string UserId { get; init; }
    public string Encryption { get; init; } = "none";
    public string Flow { get; init; } = "";
    public string Network { get; init; } = "tcp";
    public string Security { get; init; } = "none";
    public string Sni { get; init; } = "";
    public string Fingerprint { get; init; } = "chrome";
    public string PublicKey { get; init; } = "";
    public string ShortId { get; init; } = "";
    public string SpiderX { get; init; } = "";
    public string Path { get; init; } = "";
    public string Host { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string Alpn { get; init; } = "";

    public bool UsesReality => Security.Equals("reality", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Разбирает ссылку вида
    /// vless://uuid@host:port?type=tcp&security=reality&pbk=…&sni=…#Имя.
    /// Возвращает профиль или человекочитаемую ошибку.
    /// </summary>
    public static bool TryParse(string? link, out VlessProfile? profile, out string error)
    {
        profile = null;
        var raw = link?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            error = "вставьте ссылку профиля";
            return false;
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
        {
            error = "ссылка должна начинаться с vless://";
            return false;
        }

        var userId = uri.UserInfo;
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParseExact(userId, "D", out _))
        {
            error = "в ссылке нет корректного идентификатора пользователя (UUID)";
            return false;
        }
        var address = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "в ссылке нет адреса сервера";
            return false;
        }
        if (uri.Port is <= 0 or > 65535)
        {
            error = "в ссылке нет корректного порта";
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query.Length > 0 ? uri.Query[1..] : "");
        string Param(string key) => (query[key] ?? string.Empty).Trim();

        var security = Lower(Param("security"), "none");
        if (security is not ("none" or "tls" or "reality"))
        {
            error = "поддерживаются security: none, tls, reality";
            return false;
        }
        var network = Lower(Param("type"), "tcp");
        if (network is not ("tcp" or "ws" or "grpc" or "http"))
        {
            error = "поддерживаются type: tcp, ws, grpc, http";
            return false;
        }

        var sni = Param("sni");
        var publicKey = Param("pbk");
        if (security == "reality" && publicKey.Length == 0)
        {
            error = "Профиль сервера без публичного ключа (pbk) — ссылка неполная";
            return false;
        }
        if (security == "reality" && sni.Length == 0)
        {
            error = "Профиль сервера без имени сайта маскировки (sni) — ссылка неполная";
            return false;
        }

        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        profile = new VlessProfile
        {
            Name = name.Length > 0 ? name : address,
            Address = address,
            Port = uri.Port,
            UserId = userId.ToLowerInvariant(),
            Encryption = Lower(Param("encryption"), "none"),
            Flow = Param("flow"),
            Network = network,
            Security = security,
            Sni = sni,
            Fingerprint = Lower(Param("fp"), "chrome"),
            PublicKey = publicKey,
            ShortId = Param("sid"),
            SpiderX = Param("spx"),
            Path = Param("path"),
            Host = Param("host"),
            ServiceName = Param("serviceName"),
            Alpn = Param("alpn")
        };
        error = string.Empty;
        return true;
    }

    private static string Lower(string value, string fallback) =>
        value.Length > 0 ? value.ToLowerInvariant() : fallback;
}
