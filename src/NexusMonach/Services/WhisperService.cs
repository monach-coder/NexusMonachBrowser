using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace NexusMonach.Services;

public sealed record WhisperTranscript(string Text, string Language);

/// <summary>
/// Полностью автономное распознавание речи. При наличии whisper-server модель
/// загружается один раз на весь сеанс браузера. CLI оставлен только как резерв
/// для старых автономных комплектов.
/// </summary>
public static class WhisperService
{
    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static Process? _server;
    private static Uri? _inferenceEndpoint;

    public static string Status { get; private set; } = "Проверка встроенного Whisper";
    public static string? LastError { get; private set; }
    public static bool IsReady => AiModelCatalog.SpeechReady;

    public static void PrepareInBackground()
    {
        Status = IsReady ? "Whisper готов" : "Whisper отсутствует в автономном комплекте";
        LastError = IsReady ? null : AiModelCatalog.MissingSpeechRuntimeMessage;
        if (IsReady && AiModelCatalog.WhisperServer is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await WarmUpAsync(budget.Token);
                }
                catch { /* Пользователь увидит точную ошибку при запуске перевода. */ }
            });
    }

    public static Task EnsureInstalledAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
        {
            LastError = AiModelCatalog.MissingSpeechRuntimeMessage;
            Status = LastError;
            progress?.Report(Status);
            throw new InvalidOperationException(LastError);
        }
        LastError = null;
        Status = "Whisper готов";
        progress?.Report(Status);
        return Task.CompletedTask;
    }

    public static async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken: cancellationToken);
        if (AiModelCatalog.WhisperServer is not null)
            await EnsureServerStartedAsync(cancellationToken);
    }

    public static async Task<string> TranscribeAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        (await TranscribeDetailedAsync(wav, cancellationToken)).Text;

    public static async Task<WhisperTranscript> TranscribeDetailedAsync(byte[] wav,
        CancellationToken cancellationToken = default)
    {
        if (wav.Length < 1_000) return new WhisperTranscript(string.Empty, string.Empty);
        await EnsureInstalledAsync(cancellationToken: cancellationToken);

        if (AiModelCatalog.WhisperServer is not null)
        {
            try
            {
                await EnsureServerStartedAsync(cancellationToken);
                return await RunServerInferenceAsync(wav, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Один аварийный сервер не должен убивать перевод. Старый CLI
                // остаётся безопасным резервом, если он есть в комплекте.
                StopServer();
                if (AiModelCatalog.WhisperCli is null) throw;
            }
        }

        return new WhisperTranscript(await RunCliAsync(wav, translateToEnglish: false, cancellationToken), string.Empty);
    }

    /// <summary>Совместимость со старым API. Для новых субтитров не используется.</summary>
    public static Task<string> TranscribeToEnglishAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        RunCliAsync(wav, translateToEnglish: true, cancellationToken);

    private static async Task<WhisperTranscript> RunServerInferenceAsync(byte[] wav,
        CancellationToken cancellationToken)
    {
        await InferenceGate.WaitAsync(cancellationToken);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(55));
            using var content = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wav);
            audio.Headers.ContentType = new("audio/wav");
            content.Add(audio, "file", "nexus-audio.wav");
            content.Add(new StringContent("0.0"), "temperature");
            content.Add(new StringContent("0.2"), "temperature_inc");
            content.Add(new StringContent("json"), "response_format");
            content.Add(new StringContent("auto"), "language");
            content.Add(new StringContent("false"), "translate");

            using var response = await Client.PostAsync(_inferenceEndpoint!, content, budget.Token);
            var payload = await response.Content.ReadAsStringAsync(budget.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Whisper server: HTTP {(int)response.StatusCode}: " +
                                                    payload[..Math.Min(payload.Length, 500)]);
            return ParseResponse(payload);
        }
        finally { InferenceGate.Release(); }
    }

    private static WhisperTranscript ParseResponse(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0) return new WhisperTranscript(string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var text = FindString(document.RootElement, "text") ?? string.Empty;
            var language = FindString(document.RootElement, "language") ?? string.Empty;
            return new WhisperTranscript(text.Trim(), language.Trim());
        }
        catch (JsonException)
        {
            return new WhisperTranscript(payload.Trim('"', '\r', '\n', ' '), string.Empty);
        }
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        return null;
    }

    private static async Task EnsureServerStartedAsync(CancellationToken cancellationToken)
    {
        if (_server is { HasExited: false } && _inferenceEndpoint is not null) return;
        await StartGate.WaitAsync(cancellationToken);
        try
        {
            if (_server is { HasExited: false } && _inferenceEndpoint is not null) return;
            StopServer();

            var executable = AiModelCatalog.WhisperServer
                ?? throw new InvalidOperationException("whisper-server.exe не найден.");
            var port = ReserveLoopbackPort();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            foreach (var argument in new[]
                     {
                         "-m", AiModelCatalog.WhisperModel!, "--host", IPAddress.Loopback.ToString(),
                         "--port", port.ToString(), "--inference-path", "/inference", "-l", "auto",
                         "-t", threads.ToString(), "-p", "1", "-sns"
                     })
                start.ArgumentList.Add(argument);

            var server = Process.Start(start)
                         ?? throw new InvalidOperationException("Не удалось запустить встроенный whisper-server.");
            _server = server;
            _inferenceEndpoint = new Uri($"http://127.0.0.1:{port}/inference");
            _ = DrainAsync(server.StandardOutput);
            _ = DrainAsync(server.StandardError);
            Status = "Whisper загружает модель один раз…";

            var ready = false;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (server.HasExited)
                    throw new InvalidOperationException($"whisper-server завершился с кодом {server.ExitCode}.");
                if (await CanConnectAsync(port, cancellationToken))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(500, cancellationToken);
            }
            if (!ready) throw new TimeoutException("Whisper не успел загрузить локальную модель.");
            Status = "Whisper готов · модель остаётся в памяти";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Whisper: " + ex.Message;
            StopServer();
            throw;
        }
        finally { StartGate.Release(); }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { await reader.ReadToEndAsync(); } catch { }
    }

    private static async Task<string> RunCliAsync(byte[] wav, bool translateToEnglish,
        CancellationToken cancellationToken)
    {
        if (AiModelCatalog.WhisperCli is null)
            throw new InvalidOperationException(AiModelCatalog.MissingSpeechRuntimeMessage);
        await InferenceGate.WaitAsync(cancellationToken);
        var work = Path.Combine(Path.GetTempPath(), "NexusMonachWhisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "audio.wav");
        var outputBase = Path.Combine(work, "transcript");
        await File.WriteAllBytesAsync(input, wav, cancellationToken);
        try
        {
            var executable = AiModelCatalog.WhisperCli
                             ?? throw new InvalidOperationException(AiModelCatalog.MissingSpeechRuntimeMessage);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            var arguments = new List<string>
            {
                "-m", AiModelCatalog.WhisperModel!, "-f", input, "-l", "auto",
                "-otxt", "-of", outputBase, "-nt"
            };
            if (translateToEnglish) arguments.Add("-tr");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Не удалось запустить встроенный Whisper.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var log = (await stdout) + "\n" + (await stderr);
            var output = outputBase + ".txt";
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                log = log.Trim();
                throw new InvalidOperationException("Whisper завершился с ошибкой: " +
                                                    log[..Math.Min(log.Length, 700)]);
            }
            return (await File.ReadAllTextAsync(output, cancellationToken)).Trim();
        }
        finally
        {
            InferenceGate.Release();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    public static void Shutdown() => StopServer();

    private static void StopServer()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;
        _inferenceEndpoint = null;
    }
}
