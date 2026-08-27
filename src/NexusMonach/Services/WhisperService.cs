using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusMonach.Services;

public sealed record WhisperTranscript(string Text, string Language)
{
    /// <summary>Сегменты с таймкодами (только для verbose_json-ответов).</summary>
    public IReadOnlyList<WhisperTimedSegment> Segments { get; init; } = [];
}

/// <summary>Реплика оригинала с точным положением на таймлайне аудио.</summary>
public sealed record WhisperTimedSegment(double Start, double End, string Text,
    double NoSpeechProb = 0, double AvgLogProb = 0);

public enum WhisperLane
{
    Commands,
    Dubbing
}

/// <summary>
/// Полностью автономное распознавание речи. При наличии whisper-server модель
/// загружается один раз на весь сеанс браузера. CLI оставлен только как резерв
/// для старых автономных комплектов.
/// </summary>
public static class WhisperService
{
    private static readonly HttpClient Client = CreateWhisperClient();

    private static HttpClient CreateWhisperClient()
    {
        // Каждая просьба — свежее соединение: после первой отменённой просьбы
        // повторное использование pooled-соединения к whisper-server однажды
        // зависало на десятки секунд при полностью здоровом сервере.
        var client = LocalAiLoopbackTransport.CreateClient();
        client.DefaultRequestHeaders.ConnectionClose = true;
        return client;
    }
    private static readonly LaneState CommandsLane = new();
    private static readonly LaneState DubbingLane = new();

    internal static bool UsesIndependentRecognitionLanes =>
        !ReferenceEquals(CommandsLane, DubbingLane) &&
        !ReferenceEquals(CommandsLane.StartGate, DubbingLane.StartGate) &&
        !ReferenceEquals(CommandsLane.InferenceGate, DubbingLane.InferenceGate);

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
                    await WarmUpAsync(WhisperLane.Commands, budget.Token);
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

    public static Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        WarmUpAsync(WhisperLane.Commands, cancellationToken);

    public static async Task WarmUpAsync(WhisperLane lane,
        CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken: cancellationToken);
        if (AiModelCatalog.WhisperServer is not null)
            await EnsureServerStartedAsync(GetLane(lane), lane, cancellationToken);
    }

    public static async Task<string> TranscribeAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        (await TranscribeDetailedAsync(wav, cancellationToken)).Text;

    public static async Task<WhisperTranscript> TranscribeDetailedAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        await TranscribeDetailedAsync(wav, WhisperLane.Commands, cancellationToken);

    public static async Task<WhisperTranscript> TranscribeDetailedAsync(byte[] wav,
        WhisperLane lane, CancellationToken cancellationToken = default)
    {
        if (wav.Length < 1_000) return new WhisperTranscript(string.Empty, string.Empty);

        // Parakeet TDT: быстрее и точнее whisper — приоритетный путь,
        // если модель доставлена. Фолбэк на whisper при любой ошибке.
        if (lane == WhisperLane.Dubbing && ParakeetService.IsAvailable)
        {
            var parakeet = await TryParakeetAsync(wav, cancellationToken);
            if (parakeet is not null) return parakeet;
        }

        await EnsureInstalledAsync(cancellationToken: cancellationToken);
        var state = GetLane(lane);

        if (AiModelCatalog.WhisperServer is not null)
        {
            try
            {
                await EnsureServerStartedAsync(state, lane, cancellationToken);
                return await RunServerInferenceAsync(state, wav, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Бюджет истёк — сервер под подозрением: следующая просьба
                // поднимет его заново, а не пойдёт по зависшему соединению.
                StopServer(state);
                throw;
            }
            catch (Exception ex)
            {
                // Один аварийный сервер не должен убивать перевод. Старый CLI
                // остаётся безопасным резервом, если он есть в комплекте.
                // Причина падения сервера пишется в Crash Vault: тихий уход
                // в фолбэк однажды стоил часа слепой отладки «нулевых реплик».
                CrashReportService.RecordNonFatal("whisper",
                    "server-inference-" + lane.ToString().ToLowerInvariant(), ex);
                StopServer(state);
                if (AiModelCatalog.WhisperCli is null) throw;
            }
        }

        return new WhisperTranscript(await RunCliAsync(state, wav, translateToEnglish: false, cancellationToken), string.Empty);
    }

    /// <summary>Совместимость со старым API. Для новых субтитров не используется.</summary>
    public static Task<string> TranscribeToEnglishAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        RunCliAsync(CommandsLane, wav, translateToEnglish: true, cancellationToken);

    /// <summary>
    /// Конвертирует WAV (16-bit PCM, 16 кГц) в массив float32 для Parakeet.
    /// </summary>
    internal static float[]? WavToPcmFloats(byte[] wav)
    {
        if (!AudioRateRestore.TryGetLayout(wav, out var layout)) return null;
        var dataStart = (int)layout.DataOffset;
        var dataLength = (int)layout.DataLength;
        if (dataLength < 2) return null;

        var sampleCount = dataLength / 2; // 16-bit = 2 байта на сэмпл
        var floats = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            floats[i] = BitConverter.ToInt16(wav, dataStart + i * 2) / 32768f;
        return floats;
    }

    /// <summary>
    /// Прогоняет WAV через Parakeet TDT. Возвращает WhisperTranscript при
    /// успехе, null — при ошибке (фолбэк на whisper).
    /// </summary>
    private static async Task<WhisperTranscript?> TryParakeetAsync(
        byte[] wav, CancellationToken ct)
    {
        try
        {
            var pcm = WavToPcmFloats(wav);
            if (pcm is null || pcm.Length < 1600) return null;
            var result = await ParakeetService.TranscribeAsync(pcm, ct);
            if (result is null) return null;

            var segments = result.Segments
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .Select(s => new WhisperTimedSegment(s.Start, s.End, s.Text))
                .ToList();
            CrashReportService.AddBreadcrumb("parakeet",
                $"transcribed-{segments.Count}-segments");
            return new WhisperTranscript(result.Text, "auto") { Segments = segments };
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("parakeet", "try-transcribe", ex);
            return null;
        }
    }

    private static async Task<WhisperTranscript> RunServerInferenceAsync(LaneState state, byte[] wav,
        CancellationToken cancellationToken)
    {
        await state.InferenceGate.WaitAsync(cancellationToken);
        try
        {
            var endpoint = state.InferenceEndpoint
                           ?? throw new InvalidOperationException("Локальная сессия Whisper не запущена.");
            LocalAiLoopbackTransport.EnsureAllowedEndpoint(endpoint);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            using var content = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wav);
            audio.Headers.ContentType = new("audio/wav");
            content.Add(audio, "file", "nexus-audio.wav");
            content.Add(new StringContent("0.0"), "temperature");
            content.Add(new StringContent("0.2"), "temperature_inc");
            // verbose_json carries Whisper's detected language. Without it a
            // short English phrase can be routed through the multilingual
            // model and lose meaning before the final Russian translation.
            content.Add(new StringContent("verbose_json"), "response_format");
            content.Add(new StringContent("auto"), "language");
            content.Add(new StringContent("false"), "translate");

            using var response = await Client.PostAsync(endpoint, content, budget.Token);
            var payload = await response.Content.ReadAsStringAsync(budget.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Whisper server: HTTP {(int)response.StatusCode}: " +
                                                    payload[..Math.Min(payload.Length, 500)]);
            return ParseResponse(payload);
        }
        finally { state.InferenceGate.Release(); }
    }

    internal static WhisperTranscript ParseResponse(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0) return new WhisperTranscript(string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var text = FindString(document.RootElement, "text") ?? string.Empty;
            var language = FindString(document.RootElement, "detected_language") ??
                           FindString(document.RootElement, "language") ?? string.Empty;
            var segments = new List<WhisperTimedSegment>();
            if (document.RootElement.TryGetProperty("segments", out var segmentsElement) &&
                segmentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsElement.EnumerateArray())
                {
                    var segmentText = NormalizeTranscript(
                        FindString(segment, "text") ?? string.Empty);
                    if (segmentText.Length == 0) continue;
                    if (!TryFindNumber(segment, "start", out var start) ||
                        !TryFindNumber(segment, "end", out var end)) continue;
                    if (end <= start) continue;
                    // Вероятности — классические маркеры галлюцинаций на тишине.
                    TryFindNumber(segment, "no_speech_prob", out var noSpeechProb);
                    TryFindNumber(segment, "avg_logprob", out var avgLogProb);
                    segments.Add(new WhisperTimedSegment(start, end, segmentText,
                        noSpeechProb, avgLogProb));
                }
            }
            return new WhisperTranscript(NormalizeTranscript(text), language.Trim())
            {
                Segments = segments
            };
        }
        catch (JsonException)
        {
            return new WhisperTranscript(
                NormalizeTranscript(payload.Trim('"', '\r', '\n', ' ')), string.Empty);
        }
    }

    private static bool TryFindNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value);
    }

    internal static string NormalizeTranscript(string? text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text ?? string.Empty, @"\s+", " ").Trim();

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

    private static async Task EnsureServerStartedAsync(LaneState state, WhisperLane lane,
        CancellationToken cancellationToken)
    {
        if (state.Server is { HasExited: false } && state.InferenceEndpoint is not null) return;
        await state.StartGate.WaitAsync(cancellationToken);
        try
        {
            if (state.Server is { HasExited: false } && state.InferenceEndpoint is not null) return;
            StopServer(state);

            var executable = AiModelCatalog.WhisperServer
                ?? throw new InvalidOperationException("whisper-server.exe не найден.");
            var port = ReserveLoopbackPort();
            var routeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var inferencePath = $"/nexus-{routeToken}/inference";
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            // Распознавание сидит в критическом пути закадрового перевода:
            // каждая секунда на сегменте напрямую добавляется к отставанию,
            // поэтому отдаём трем четвертям ядер, оставляя остальным задачам
            // минимум.
            var threads = Math.Clamp(Environment.ProcessorCount * 3 / 4, 3, 8);
            foreach (var argument in new[]
                     {
                         // whisper-server открывает модель через ANSI fopen: кириллица
                         // в пути установки убивает процесс на загрузке модели, поэтому
                         // модель заранее зеркалируется в ASCII-безопасный кэш.
                         "-m", AsciiSafeModelCache.EnsureAsciiSafePath(AiModelCatalog.WhisperModel!),
                         "--host", IPAddress.Loopback.ToString(),
                         "--port", port.ToString(), "--inference-path", inferencePath, "-l", "auto",
                         "-t", threads.ToString(), "-p", "1", "-sns"
                     })
                start.ArgumentList.Add(argument);

            var server = Process.Start(start)
                         ?? throw new InvalidOperationException("Не удалось запустить встроенный whisper-server.");
            state.Server = server;
            state.InferenceEndpoint = new Uri($"http://127.0.0.1:{port}{inferencePath}");
            ProcessNursery.Adopt(server);
            LocalAiLoopbackTransport.EnsureAllowedEndpoint(state.InferenceEndpoint);
            _ = DrainAsync(server.StandardOutput);
            _ = DrainAsync(server.StandardError);
            Status = $"Whisper {lane} загружает модель один раз…";

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
            Status = $"Whisper {lane} готов · модель остаётся в памяти";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Whisper: " + ex.Message;
            StopServer(state);
            throw;
        }
        finally { state.StartGate.Release(); }
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
        try { await reader.ReadToEndAsync(); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("whisper", "DrainAsync", swallowed);
        }
    }

    private static async Task<string> RunCliAsync(LaneState state, byte[] wav, bool translateToEnglish,
        CancellationToken cancellationToken)
    {
        if (AiModelCatalog.WhisperCli is null)
            throw new InvalidOperationException(AiModelCatalog.MissingSpeechRuntimeMessage);
        await state.InferenceGate.WaitAsync(cancellationToken);
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
                // whisper-cli падает при загрузке модели по пути с не-ASCII
                // символами — тот же класс ошибки, что у torch в TTS-воркере.
                "-m", AsciiSafeModelCache.EnsureAsciiSafePath(AiModelCatalog.WhisperModel!),
                "-f", input, "-l", "auto",
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
                try { process.Kill(entireProcessTree: true); } catch (Exception swallowed)
                {
                    Services.SwallowLog.Log("whisper", "RunCliAsync", swallowed);
                }
                throw;
            }
            var log = (await stdout) + "\n" + (await stderr);
            var output = outputBase + ".txt";
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                log = log.Trim();
                throw new InvalidOperationException("Whisper завершился с ошибкой: " +
                                                    log[..Math.Min(log.Length, 2500)]);
            }
            return (await File.ReadAllTextAsync(output, cancellationToken)).Trim();
        }
        finally
        {
            state.InferenceGate.Release();
            try { Directory.Delete(work, recursive: true); } catch (Exception swallowed)
            {
                Services.SwallowLog.Log("whisper", "RunCliAsync", swallowed);
            }
        }
    }

    public static void Shutdown()
    {
        StopServer(CommandsLane);
        StopServer(DubbingLane);
    }

    private static LaneState GetLane(WhisperLane lane) =>
        lane == WhisperLane.Dubbing ? DubbingLane : CommandsLane;

    private static void StopServer(LaneState state)
    {
        try { if (state.Server is { HasExited: false }) state.Server.Kill(entireProcessTree: true); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("whisper", "StopServer", swallowed);
        }
        try { state.Server?.Dispose(); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("whisper", "StopServer", swallowed);
        }
        state.Server = null;
        state.InferenceEndpoint = null;
    }

    private sealed class LaneState
    {
        internal readonly SemaphoreSlim StartGate = new(1, 1);
        internal readonly SemaphoreSlim InferenceGate = new(1, 1);
        internal Process? Server;
        internal Uri? InferenceEndpoint;
    }
}
