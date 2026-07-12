using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Узкий loopback-мост между вкладкой расширения Chromium DevTools и встроенной моделью.
/// Он принимает только очищенный контекст, не имеет доступа к cookies/профилю и не выполняет команды.
/// </summary>
public static class DevToolsAiBridgeService
{
    public const int Port = 28471;
    private const int MaximumBodyBytes = 256 * 1024;
    private static TcpListener? _listener;
    private static CancellationTokenSource? _lifetime;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);

    public static void Start()
    {
        if (_listener is not null) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start(4);
            _lifetime = new CancellationTokenSource();
            _ = AcceptLoopAsync(_lifetime.Token);
        }
        catch
        {
            _listener?.Stop();
            _listener = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
    }

    public static void Stop()
    {
        _lifetime?.Cancel();
        _listener?.Stop();
        _listener = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private static async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(250, cancellationToken); }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 15_000;
            client.SendTimeout = 15_000;
            NetworkStream? stream = null;
            var origin = string.Empty;
            try
            {
                stream = client.GetStream();
                var headerBytes = await ReadHeadersAsync(stream, cancellationToken);
                var header = Encoding.ASCII.GetString(headerBytes);
                var firstLine = header.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
                origin = ReadHeader(header, "Origin");
                if (!origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stream, 403, new { error = "Запрос разрешён только встроенному DevTools." }, origin, cancellationToken);
                    return;
                }
                if (firstLine.StartsWith("OPTIONS ", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 204, new { }, origin, cancellationToken);
                    return;
                }
                if (!firstLine.StartsWith("POST /analyze ", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 404, new { error = "Неизвестный локальный маршрут." }, origin, cancellationToken);
                    return;
                }
                if (!int.TryParse(ReadHeader(header, "Content-Length"), out var length) || length <= 0 || length > MaximumBodyBytes)
                {
                    await WriteJsonAsync(stream, 413, new { error = "Недопустимый размер диагностического контекста." }, origin, cancellationToken);
                    return;
                }
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken);
                var request = JsonSerializer.Deserialize<BridgeRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (request is null || string.IsNullOrWhiteSpace(request.Context))
                    throw new InvalidOperationException("DevTools не передал очищенный контекст.");
                if (!AiModelCatalog.TextReady)
                    throw new InvalidOperationException(AiModelCatalog.MissingTextRuntimeMessage);

                await RequestGate.WaitAsync(cancellationToken);
                try
                {
                    var question = string.IsNullOrWhiteSpace(request.Question)
                        ? "Объясни назначение выбранного объекта, возможные проблемы и следующие безопасные шаги."
                        : request.Question.Trim();
                    var analysis = await LocalIntelligenceService.AnswerDeveloperQuestionAsync(
                        question, request.Context[..Math.Min(request.Context.Length, 50_000)], cancellationToken);
                    await WriteJsonAsync(stream, 200, new
                    {
                        answer = analysis.Summary,
                        steps = analysis.Suggestions,
                        selector = analysis.Highlights.FirstOrDefault()?.Selector ?? string.Empty,
                        reason = analysis.Highlights.FirstOrDefault()?.Reason ?? string.Empty,
                        model = AiModelCatalog.TextModelId
                    }, origin, cancellationToken);
                }
                finally { RequestGate.Release(); }
            }
            catch (Exception ex)
            {
                try
                {
                    if (stream is not null)
                        await WriteJsonAsync(stream, 500, new { error = ex.Message }, origin, cancellationToken);
                }
                catch { }
            }
            finally { if (stream is not null) await stream.DisposeAsync(); }
        }
    }

    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var current = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            if (await stream.ReadAsync(current, cancellationToken) == 0) break;
            buffer.WriteByte(current[0]);
            var data = buffer.GetBuffer();
            var length = (int)buffer.Length;
            if (length >= 4 && data[length - 4] == 13 && data[length - 3] == 10 &&
                data[length - 2] == 13 && data[length - 1] == 10)
                return buffer.ToArray();
        }
        throw new InvalidOperationException("Некорректный локальный HTTP-запрос.");
    }

    private static string ReadHeader(string headers, string name)
    {
        var prefix = name + ":";
        return headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim()
            ?? string.Empty;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object value, string origin,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var reason = status switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large", _ => "Error" };
        var safeOrigin = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ? origin : "null";
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Access-Control-Allow-Origin: {safeOrigin}\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        if (status != 204) await stream.WriteAsync(body, cancellationToken);
    }

    private sealed class BridgeRequest
    {
        public string Context { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
    }
}
