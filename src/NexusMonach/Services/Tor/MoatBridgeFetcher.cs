using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Moat-клиент: официальная раздача неизданных мостов Tor Project
/// (тот же механизм, что в Tor Browser). Капчу решает человек в окошке —
/// браузер автоматизирует всё остальное: запрос, отправку решения,
/// получение webtunnel-мостов и их вписывание в пул ротации.
/// Публичные списки не используются: выложенное выгорает первым.
/// </summary>
public static class MoatBridgeFetcher
{
    private const string FetchUrl = "https://bridges.torproject.org/moat/fetch";
    private const string CheckUrl = "https://bridges.torproject.org/moat/check";

    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Вызов капчи:Tor Project отвечает картинкой-заданием.</summary>
    public sealed record Challenge(string Id, string Transport, byte[] ImagePng, string MoatVersion);

    /// <summary>Запрос задания капчи для webtunnel-мостов.</summary>
    public static async Task<Challenge?> FetchChallengeAsync(string transport = "webtunnel")
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                data = new object[]
                {
                    new { version = "0.1.0", type = "client-transports", supported = new[] { transport } }
                }
            });
            using var response = await Client.PostAsync(FetchUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return ParseChallenge(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Отправка решения капчи; возвращает полученные строки мостов.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        Challenge challenge, string solution)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                data = new object[]
                {
                    new
                    {
                        id = challenge.Id,
                        version = "0.1.0",
                        type = "moat-solution",
                        solution,
                        transport = challenge.Transport
                    }
                }
            });
            using var response = await Client.PostAsync(CheckUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return ParseBridges(body);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Разбор ответа fetch: задание капчи. Чистая функция для тестов.</summary>
    internal static Challenge? ParseChallenge(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.GetString() == "moat-challenge" &&
                    item.TryGetProperty("image", out var image) &&
                    item.TryGetProperty("id", out var id))
                {
                    var dataUrl = image.GetString() ?? string.Empty;
                    var base64 = dataUrl.Contains(",", StringComparison.Ordinal)
                        ? dataUrl[(dataUrl.IndexOf(',', StringComparison.Ordinal) + 1)..]
                        : dataUrl;
                    var transport = item.TryGetProperty("transport", out var t)
                        ? t.GetString() ?? "webtunnel" : "webtunnel";
                    var version = item.TryGetProperty("moat_version", out var v)
                        ? v.GetString() ?? "0.1.0" : "0.1.0";
                    return new Challenge(id.GetString() ?? string.Empty, transport,
                        Convert.FromBase64String(base64), version);
                }
            }
        }
        catch
        {
            // Некорректный ответ разда́тчика — выше превратится в честный null.
        }
        return null;
    }

    /// <summary>Разбор ответа check: строки полученных мостов. Чистая функция для тестов.</summary>
    internal static IReadOnlyList<string> ParseBridges(string json)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.GetString() == "moat-bridges" &&
                    item.TryGetProperty("bridges", out var bridges) &&
                    bridges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bridge in bridges.EnumerateArray())
                    {
                        var line = bridge.GetString();
                        if (!string.IsNullOrWhiteSpace(line))
                            result.Add(line.Trim());
                    }
                }
            }
        }
        catch
        {
            // Пустой список — вызывающий честно сообщит «не вышло».
        }
        return result;
    }
}
