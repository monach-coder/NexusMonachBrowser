using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var productVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
});
builder.Services.AddHttpClient("matrix", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<ReportDeduplicator>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("guardian-global", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Nexus Guardian Relay" }));
app.MapPost("/api/v1/crash-reports", async (
    HttpContext context,
    CrashReport report,
    IConfiguration configuration,
    IHttpClientFactory clients,
    ReportDeduplicator deduplicator,
    CancellationToken cancellationToken) =>
{
    var ingestKey = configuration["Guardian:IngestKey"];
    if (!string.IsNullOrWhiteSpace(ingestKey))
    {
        var supplied = context.Request.Headers["X-Nexus-Guardian-Key"].ToString();
        if (!FixedTimeEquals(ingestKey, supplied)) return Results.Unauthorized();
    }

    var validation = report.Validate();
    if (validation is not null) return Results.BadRequest(new { error = validation });

    var fingerprint = report.Fingerprint();
    if (!deduplicator.ShouldForward(fingerprint, out var repeats))
        return Results.Accepted(value: new { accepted = true, duplicate = true, repeats });

    var matrix = MatrixOptions.From(configuration);
    if (!matrix.IsConfigured)
        return Results.Problem("Matrix is not configured on Nexus Guardian Relay.", statusCode: 503);

    var client = clients.CreateClient("matrix");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", matrix.AccessToken);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusGuardianRelay/{productVersion}");

    if (!await MatrixRoomIsPlaintextAsync(client, matrix, cancellationToken))
        return Results.Problem("Matrix room is encrypted or inaccessible; plaintext forwarding was blocked.", statusCode: 503);

    var txnId = "guardian-" + Guid.NewGuid().ToString("N");
    var endpoint = MatrixEndpoint(matrix, $"send/m.room.message/{Uri.EscapeDataString(txnId)}");
    using var response = await client.PutAsJsonAsync(endpoint, new
    {
        msgtype = "m.text",
        body = report.ToMatrixMessage(repeats)
    }, cancellationToken);
    if (!response.IsSuccessStatusCode)
        return Results.Problem($"Matrix returned HTTP {(int)response.StatusCode}.", statusCode: 502);

    return Results.Accepted(value: new { accepted = true, reportId = report.Id });
});

app.Run();

static bool FixedTimeEquals(string expected, string actual)
{
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(actual ?? string.Empty));
    return CryptographicOperations.FixedTimeEquals(left, right);
}

static Uri MatrixEndpoint(MatrixOptions options, string suffix) => new(
    $"{options.Homeserver.TrimEnd('/')}/_matrix/client/v3/rooms/{Uri.EscapeDataString(options.RoomId)}/{suffix}");

static async Task<bool> MatrixRoomIsPlaintextAsync(
    HttpClient client, MatrixOptions options, CancellationToken cancellationToken)
{
    using var response = await client.GetAsync(MatrixEndpoint(options, "state/m.room.encryption"), cancellationToken);
    if (response.IsSuccessStatusCode) return false;
    return response.StatusCode == HttpStatusCode.NotFound;
}

internal sealed record MatrixOptions(string Homeserver, string RoomId, string AccessToken)
{
    public bool IsConfigured =>
        Uri.TryCreate(Homeserver, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        RoomId.StartsWith('!') && RoomId.Contains(':') && !string.IsNullOrWhiteSpace(AccessToken);

    public static MatrixOptions From(IConfiguration configuration) => new(
        configuration["Guardian:Matrix:Homeserver"] ?? string.Empty,
        configuration["Guardian:Matrix:RoomId"] ?? string.Empty,
        configuration["Guardian:Matrix:AccessToken"] ?? string.Empty);
}

internal sealed class ReportDeduplicator
{
    private readonly ConcurrentDictionary<string, DeduplicationEntry> _entries = new();

    public bool ShouldForward(string fingerprint, out int repeats)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.AddOrUpdate(fingerprint,
            _ => new DeduplicationEntry(now, 1),
            (_, previous) => now - previous.FirstSeen > TimeSpan.FromMinutes(30)
                ? new DeduplicationEntry(now, 1)
                : previous with { Count = previous.Count + 1 });
        repeats = entry.Count;
        if (_entries.Count > 2000)
        {
            foreach (var stale in _entries.Where(x => now - x.Value.FirstSeen > TimeSpan.FromHours(2)).Take(500))
                _entries.TryRemove(stale.Key, out _);
        }
        return entry.Count == 1 || entry.Count is 5 or 20 or 100;
    }

    private sealed record DeduplicationEntry(DateTimeOffset FirstSeen, int Count);
}

internal sealed class CrashReport
{
    public int SchemaVersion { get; set; }
    public string? Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public bool Fatal { get; set; }
    public string? BrowserVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? WebView2Version { get; set; }
    public string? ProcessArchitecture { get; set; }
    public string? Component { get; set; }
    public string? Stage { get; set; }
    public string? ExceptionType { get; set; }
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
    public string? IntegrityStatus { get; set; }
    public bool SafeMode { get; set; }

    public string? Validate()
    {
        if (SchemaVersion != 1) return "Unsupported schemaVersion.";
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80) return "Invalid report id.";
        if (TimestampUtc == default || TimestampUtc < DateTimeOffset.UtcNow.AddDays(-30) ||
            TimestampUtc > DateTimeOffset.UtcNow.AddHours(1)) return "Invalid timestamp.";
        if (Length(BrowserVersion) > 80 || Length(Component) > 64 || Length(Stage) > 64 ||
            Length(ExceptionType) > 300 || Length(Message) > 16_000 || Length(StackTrace) > 16_000 ||
            Length(IntegrityStatus) > 300) return "Field length limit exceeded.";
        return null;
    }

    public string Fingerprint()
    {
        var source = string.Join('|', BrowserVersion, Component, Stage, ExceptionType,
            S(StackTrace).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public string ToMatrixMessage(int repeats)
    {
        var title = Fatal ? "🔴 Nexus Guardian · аварийное завершение" : "🟠 Nexus Guardian · программная ошибка";
        var repeatLine = repeats > 1 ? $"\nПовторений этого сбоя: {repeats}" : string.Empty;
        var rawStack = S(StackTrace);
        var rawMessage = S(Message);
        var stack = rawStack.Length <= 7000 ? rawStack : rawStack[..7000] + "…";
        var message = rawMessage.Length <= 3000 ? rawMessage : rawMessage[..3000] + "…";
        return $"{title}\nID: {Id}\nВремя UTC: {TimestampUtc:O}\nВерсия: {BrowserVersion}\n" +
               $"Компонент: {Component} / {Stage}\nИсключение: {ExceptionType}\n" +
               $"Целостность: {IntegrityStatus}\nБезопасный режим: {(SafeMode ? "да" : "нет")}" + repeatLine +
               $"\n\nСообщение:\n{message}\n\nСтек (очищен):\n{stack}";
    }

    private static int Length(string? value) => value?.Length ?? 0;
    private static string S(string? value) => value ?? string.Empty;
}
