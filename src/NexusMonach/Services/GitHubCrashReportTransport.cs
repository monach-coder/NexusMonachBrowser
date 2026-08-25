using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using NexusMonach.Services.Diagnostics;

namespace NexusMonach.Services;

/// <summary>
/// Доставка краш-рапортов в GitHub Issues — сервер не нужен. Рапорты
/// дедуплицируются по сигнатуре (тип исключения + верхние кадры стека):
/// повторный краш становится комментарием «+1» к существующему issue,
/// а не новым тикетом. Причинный граф вкладывается Mermaid-диаграммой —
/// GitHub рендерит её прямо в issue.
/// </summary>
public static class GitHubCrashReportTransport
{
    private const string ApiRoot = "https://api.github.com";
    private static readonly string ProductVersion =
        typeof(GitHubCrashReportTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Формат репозитория «владелец/имя», без пробелов и слэшей внутри.</summary>
    public static bool IsValidRepository(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               parts[0].Length > 0 && parts[1].Length > 0 &&
               !repository.Contains(' ') &&
               parts[0].All(c => char.IsLetterOrDigit(c) || c is '-' or '_') &&
               parts[1].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    /// <summary>
    /// Сигнатура краша: SHA-256 от типа исключения и первых кадров стека.
    /// Стабильна для одного места сбоя, различна для разных.
    /// </summary>
    internal static string BuildSignature(string exceptionType, string stackTrace)
    {
        var frames = (stackTrace ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("at ", StringComparison.Ordinal))
            .Take(5);
        var payload = exceptionType + "|" + string.Join("|", frames);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    public static async Task<bool> SendReportAsync(
        HttpClient client,
        string repository,
        string accessToken,
        string reportFile,
        CancellationToken cancellationToken)
    {
        ConfigureClient(client, accessToken);
        await using var stream = File.OpenRead(reportFile);
        using var report = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var (title, body) = BuildIssue(report.RootElement);
        var signature = ExtractSignature(title);

        // Дедупликация: ищем открытое issue с той же сигнатурой в заголовке.
        var existing = await FindIssueNumberAsync(client, repository, signature, cancellationToken);
        string payloadTitle, payloadBody, endpoint;
        if (existing is { } number)
        {
            endpoint = $"{ApiRoot}/repos/{repository}/issues/{number}/comments";
            payloadTitle = title;
            payloadBody = BuildDuplicateComment(report.RootElement, signature);
        }
        else
        {
            endpoint = $"{ApiRoot}/repos/{repository}/issues";
            payloadTitle = title;
            payloadBody = body;
        }
        using var response = await client.PostAsJsonAsync(endpoint,
            new { title = payloadTitle, body = payloadBody }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Короткий комментарий к уже открытому issue: факты повтора без повтора всего рапорта.</summary>
    private static string BuildDuplicateComment(JsonElement report, string signature)
    {
        static string Text(JsonElement root, string name, int limit = 200)
        {
            if (!root.TryGetProperty(name, out var value)) return "—";
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            text ??= "—";
            return text.Length <= limit ? text : text[..limit];
        }
        return $"+1 повтор [{signature}] · версия {Text(report, "BrowserVersion")} · " +
               $"{Text(report, "TimestampUtc")} · {Text(report, "Component")}/{Text(report, "Stage")}";
    }

    /// <summary>Проверяет токен и доступ к репозиторию, ничего не отправляя.</summary>
    public static async Task<(bool Success, string Message)> TestAsync(
        string repository,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRepository(repository))
            return (false, "Репозиторий указывается в формате «владелец/имя», например monach-coder/NexusMonachBrowser.");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            ConfigureClient(client, accessToken);
            using var response = await client.GetAsync($"{ApiRoot}/repos/{repository}", cancellationToken);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK => (true, "Токен принят, доступ к репозиторию подтверждён."),
                System.Net.HttpStatusCode.Unauthorized => (false, "GitHub отклонил токен (401). Проверьте fine-grained PAT и его срок действия."),
                System.Net.HttpStatusCode.Forbidden => (false, "GitHub вернул 403: у токена нет прав на этот репозиторий."),
                System.Net.HttpStatusCode.NotFound => (false, "Репозиторий не найден (404). Проверьте имя и доступ токена."),
                _ => (false, $"GitHub вернул HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).")
            };
        }
        catch (Exception ex)
        {
            return (false, "Не удалось связаться с GitHub: " + ex.Message);
        }
    }

    private static void ConfigureClient(HttpClient client, string accessToken)
    {
        // Клетка повторной конфигурации: один клиент живёт на всю очередь
        // отправок, заголовки выставляются ровно один раз.
        if (client.DefaultRequestHeaders.Authorization is not null) return;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusGuardian/{ProductVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static async Task<int?> FindIssueNumberAsync(
        HttpClient client, string repository, string signature, CancellationToken cancellationToken)
    {
        try
        {
            // Сигнатуру берём в кавычки (скобки — операторы поиска), а запрос
            // обязан содержать is:issue: иначе GitHub отвечает 422.
            var query = Uri.EscapeDataString(
                $"repo:{repository} is:issue in:title \"[{signature}]\"");
            using var response = await client.GetAsync($"{ApiRoot}/search/issues?q={query}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.GetArrayLength() == 0)
                return null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("number", out var number) &&
                    item.TryGetProperty("state", out var state) &&
                    state.GetString() == "open")
                    return number.GetInt32();
            }
            return null;
        }
        catch
        {
            // Поиск недоступен (лимит?) — создаём новый issue, дубликат переживём.
            return null;
        }
    }

    /// <summary>Строит заголовок и тело issue из рапорта. Чистая функция для тестов.</summary>
    internal static (string Title, string Body) BuildIssue(JsonElement report)
    {
        static string Text(JsonElement root, string name, int limit = 2000)
        {
            if (!root.TryGetProperty(name, out var value)) return "—";
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            text ??= "—";
            return text.Length <= limit ? text : text[..limit] + "…";
        }

        var exceptionType = Text(report, "ExceptionType", 160);
        var component = Text(report, "Component", 80);
        var stage = Text(report, "Stage", 80);
        var signature = BuildSignature(exceptionType, Text(report, "StackTrace", 16_000));
        var fatal = report.TryGetProperty("Fatal", out var fatalNode) && fatalNode.ValueKind == JsonValueKind.True;
        var safeMode = report.TryGetProperty("SafeMode", out var safeNode) && safeNode.ValueKind == JsonValueKind.True;
        var shortException = exceptionType.Split('.').LastOrDefault() ?? exceptionType;

        var title = $"[Crash] {component}/{stage}: {shortException} [{signature}]";

        var lines = new List<string>
        {
            fatal ? "🔴 **Аварийное завершение** Nexus Guardian" : "🟠 **Программная ошибка** Nexus Guardian",
            string.Empty,
            $"| Поле | Значение |",
            $"|---|---|",
            $"| Версия | {Text(report, "BrowserVersion", 80)} |",
            $"| Компонент | {component} / {stage} |",
            $"| Исключение | `{exceptionType}` |",
            $"| Время UTC | {Text(report, "TimestampUtc", 80)} |",
            $"| Целостность | {Text(report, "IntegrityStatus", 160)} |",
            $"| Безопасный режим | {(safeMode ? "да" : "нет")} |",
            $"| ID рапорта | {Text(report, "Id", 80)} |",
            string.Empty
        };

        // Причинный граф: GitHub рендерит Mermaid прямо в issue.
        if (report.TryGetProperty("CausalGraph", out var graphNode) &&
            graphNode.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var graph = graphNode.Deserialize<CausalGraph>();
                if (graph is not null && graph.Nodes.Count > 0)
                {
                    lines.Add("**Причинный граф отказа** (корневая причина подсвечена):");
                    lines.Add(string.Empty);
                    lines.Add("```mermaid");
                    lines.Add(CausalGraphExporter.ToMermaid(graph));
                    lines.Add("```");
                    lines.Add(string.Empty);
                    lines.Add($"**Итог:** {graph.Summary}");
                    lines.Add(string.Empty);
                }
            }
            catch { /* Повреждённый граф не должен мешать доставке рапорта. */ }
        }

        lines.Add("**Сообщение**");
        lines.Add("```");
        lines.Add(Text(report, "Message", 3000));
        lines.Add("```");
        lines.Add(string.Empty);
        lines.Add("**Стек (очищен санитизатором Guardian)**");
        lines.Add("```");
        lines.Add(Text(report, "StackTrace", 12_000));
        lines.Add("```");

        var body = string.Join('\n', lines);
        return (title, body.Length <= 60_000 ? body : body[..60_000] + "\n…[сокращено Guardian]");
    }

    private static string ExtractSignature(string title)
    {
        var open = title.LastIndexOf('[');
        var close = title.LastIndexOf(']');
        return open >= 0 && close > open ? title[(open + 1)..close] : string.Empty;
    }
}
