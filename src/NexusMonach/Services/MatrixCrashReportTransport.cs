using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexusMonach.Services;

public static class MatrixCrashReportTransport
{
    public static async Task<bool> SendReportAsync(
        HttpClient client,
        string homeserver,
        string roomId,
        string accessToken,
        string reportFile,
        CancellationToken cancellationToken)
    {
        ConfigureClient(client, accessToken);
        if (!await IsPlaintextRoomAsync(client, homeserver, roomId, cancellationToken)) return false;

        await using var stream = File.OpenRead(reportFile);
        using var report = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var body = BuildMessage(report.RootElement);
        var transactionId = "guardian-" + Guid.NewGuid().ToString("N");
        var endpoint = BuildRoomEndpoint(homeserver, roomId,
            $"send/m.room.message/{Uri.EscapeDataString(transactionId)}");
        using var response = await client.PutAsJsonAsync(endpoint, new
        {
            msgtype = "m.text",
            body
        }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public static async Task<(bool Success, string Message)> TestAsync(
        string homeserver,
        string roomId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            ConfigureClient(client, accessToken);
            if (!await IsPlaintextRoomAsync(client, homeserver, roomId, cancellationToken))
                return (false, "Комната использует сквозное шифрование. Прямая отправка Guardian в E2EE-комнату пока заблокирована.");

            var transactionId = "guardian-test-" + Guid.NewGuid().ToString("N");
            var endpoint = BuildRoomEndpoint(homeserver, roomId,
                $"send/m.room.message/{Uri.EscapeDataString(transactionId)}");
            using var response = await client.PutAsJsonAsync(endpoint, new
            {
                msgtype = "m.text",
                body = "Nexus Guardian: прямое подключение к Matrix проверено. Тестовый crash-report не отправлялся."
            }, cancellationToken);
            if (response.IsSuccessStatusCode) return (true, "Тестовое сообщение отправлено в Matrix.");
            return (false, $"Matrix вернул HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось подключиться к Matrix: " + ex.Message);
        }
    }

    private static void ConfigureClient(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexusGuardian/2.7.2");
    }

    private static async Task<bool> IsPlaintextRoomAsync(
        HttpClient client, string homeserver, string roomId, CancellationToken cancellationToken)
    {
        var endpoint = BuildRoomEndpoint(homeserver, roomId, "state/m.room.encryption");
        using var response = await client.GetAsync(endpoint, cancellationToken);
        if (response.IsSuccessStatusCode) return false;
        return response.StatusCode == HttpStatusCode.NotFound;
    }

    private static Uri BuildRoomEndpoint(string homeserver, string roomId, string suffix)
    {
        var root = homeserver.TrimEnd('/');
        return new Uri($"{root}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/{suffix}", UriKind.Absolute);
    }

    private static string BuildMessage(JsonElement report)
    {
        static string Text(JsonElement root, string name, int limit = 2000)
        {
            if (!root.TryGetProperty(name, out var value)) return "—";
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            text ??= "—";
            return text.Length <= limit ? text : text[..limit] + "…";
        }

        var fatal = report.TryGetProperty("Fatal", out var fatalNode) && fatalNode.ValueKind == JsonValueKind.True;
        var safeMode = report.TryGetProperty("SafeMode", out var safeNode) && safeNode.ValueKind == JsonValueKind.True;
        var lines = new List<string>
        {
            fatal ? "🔴 Nexus Guardian · аварийное завершение" : "🟠 Nexus Guardian · программная ошибка",
            $"ID: {Text(report, "Id", 80)}",
            $"Время UTC: {Text(report, "TimestampUtc", 80)}",
            $"Версия: {Text(report, "BrowserVersion", 80)}",
            $"Компонент: {Text(report, "Component", 80)} / {Text(report, "Stage", 80)}",
            $"Исключение: {Text(report, "ExceptionType", 160)}",
            $"Целостность: {Text(report, "IntegrityStatus", 160)}",
            $"Безопасный режим: {(safeMode ? "да" : "нет")}",
            "",
            "Сообщение:",
            Text(report, "Message", 3000),
            "",
            "Стек (очищен):",
            Text(report, "StackTrace", 7000)
        };
        var result = string.Join('\n', lines);
        return result.Length <= 14_000 ? result : result[..14_000] + "\n…[сокращено Guardian]";
    }
}
