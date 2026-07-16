using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// A single loopback-only llama-server process. Keeping Qwen loaded removes the
/// repeated model startup that previously made page and video translation stall.
/// No request can leave 127.0.0.1.
/// </summary>
internal static class LocalTextModelServer
{
    private static readonly SemaphoreSlim StartupGate = new(1, 1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static Process? _process;
    private static Uri? _endpoint;

    public static bool CanRun => AiModelCatalog.LlamaServer is not null && AiModelCatalog.TextModel is not null;

    public static async Task WarmUpAsync()
    {
        if (!CanRun) return;
        try { await EnsureStartedAsync(CancellationToken.None); }
        catch { /* llama-cli remains the compatibility fallback. */ }
    }

    public static async Task<string> AskAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken, int maximumTokens = 4096, double temperature = 0.15)
    {
        await EnsureStartedAsync(cancellationToken);
        var endpoint = _endpoint ?? throw new InvalidOperationException("Локальный AI-сервер не запущен.");
        var payload = JsonSerializer.Serialize(new
        {
            model = "nexus-local",
            messages = new[]
            {
                new { role = "system", content = systemPrompt.Trim() + "\nОтвечай без рассуждений и служебных тегов. /no_think" },
                new { role = "user", content = userPrompt.Trim() }
            },
            temperature = Math.Clamp(temperature, 0, 1),
            max_tokens = Math.Clamp(maximumTokens, 64, 4096),
            stream = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Локальный AI-сервер отклонил запрос: " +
                                                json[..Math.Min(json.Length, 500)]);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
            throw new InvalidOperationException("Локальный AI-сервер вернул неизвестный формат ответа.");
        return content.GetString()?.Trim() ?? string.Empty;
    }

    private static async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _endpoint is not null) return;
        await StartupGate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } && _endpoint is not null) return;
            Shutdown();
            if (!CanRun) throw new InvalidOperationException("llama-server.exe отсутствует в AI-комплекте.");
            var port = ReserveLoopbackPort();
            var start = new ProcessStartInfo(AiModelCatalog.LlamaServer!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AiModelCatalog.LlamaRoot
            };
            foreach (var argument in new[]
                     {
                         "-m", AiModelCatalog.TextModel!, "--host", "127.0.0.1", "--port", port.ToString(),
                         "-c", "12288", "--parallel", "1", "--threads",
                         Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(), "--no-webui"
                     })
                start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить llama-server.exe.");
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _endpoint = new Uri($"http://127.0.0.1:{port}/");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException("llama-server.exe завершился при загрузке модели.");
                try
                {
                    using var health = await Client.GetAsync(new Uri(_endpoint, "health"), cancellationToken);
                    if (health.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(350, cancellationToken);
            }
            throw new TimeoutException("Локальная модель не загрузилась за 75 секунд.");
        }
        catch
        {
            Shutdown();
            throw;
        }
        finally { StartupGate.Release(); }
    }

    public static void Shutdown()
    {
        var process = Interlocked.Exchange(ref _process, null);
        _endpoint = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
