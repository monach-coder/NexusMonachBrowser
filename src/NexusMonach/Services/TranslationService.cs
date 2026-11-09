using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Persistent offline OPUS translator. Page text, selected text and Whisper
/// transcripts all use this service; the general-purpose chat model is not part
/// of the translation path.
/// </summary>
public static class TranslationService
{
    private sealed record TranslationReply(string Id, List<TranslationSegment>? Items, string? Error);

    // Два резидентных воркера: страница из сотни DOM-фрагментов переводится
    // двумя Node/ONNX процессами параллельно (почти ×2 к скорости), а память
    // остаётся ограниченной — каждый воркер держит те же три компактных
    // OPUS-маршрута (~350 МБ моделей на диске, q8).
    private const int MaxWorkers = 2;
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim Slots = new(MaxWorkers, MaxWorkers);
    private static readonly ConcurrentQueue<Process> Idle = new();
    private static readonly List<Process> Workers = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void WarmUpInBackground()
    {
        if (!AiModelCatalog.TranslationReady) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await WarmUpForLiveVideoAsync(includeAllSourceRoutes: false, budget.Token);
            }
            catch { /* Readiness is reported when the user explicitly translates. */ }
        });
    }

    public static async Task WarmUpForLiveVideoAsync(bool includeAllSourceRoutes = true,
        CancellationToken cancellationToken = default)
    {
        if (!AiModelCatalog.TranslationReady)
            throw new InvalidOperationException(AiModelCatalog.MissingTranslationRuntimeMessage);

        EnsureWorkerPool();
        // Греем английский маршрут на КАЖДОМ воркере параллельно: первый
        // настоящий запрос страницы или дубляжа не должен платить разовую
        // загрузку ONNX ни на одной из двух линий.
        await Task.WhenAll(Enumerable.Range(0, MaxWorkers).Select(index =>
            TranslateToRussianAsync(
                index == 0 ? "Translation service is ready." : "The translator is ready.",
                true, cancellationToken)));
        if (!includeAllSourceRoutes) return;

        // Live dubbing must not pay the one-time ONNX load cost after the first
        // scene has already passed. Preload every source route before capture.
        await TranslateToRussianAsync("Guten Tag.", false, cancellationToken, "de");
        await TranslateToRussianAsync("안녕하세요.", false, cancellationToken, "ko");
    }

    public static async Task<string> TranslateToRussianAsync(string text, bool sourceIsEnglish = false,
        CancellationToken cancellationToken = default, string? sourceLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var item = new TranslationSegment
            { Id = "single", Text = text.Trim(), Language = sourceLanguage?.Trim() ?? string.Empty };
        var translated = await TranslateSegmentsAsync([item], sourceIsEnglish, cancellationToken);
        return translated.FirstOrDefault()?.Text ??
               throw new InvalidOperationException("Автономный переводчик не вернул текст.");
    }

    public static async Task<IReadOnlyList<TranslationSegment>> TranslateSegmentsAsync(
        IReadOnlyList<TranslationSegment> segments, bool sourceIsEnglish = false,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0) return [];
        if (!AiModelCatalog.TranslationReady)
            throw new InvalidOperationException(AiModelCatalog.MissingTranslationRuntimeMessage);

        await Slots.WaitAsync(cancellationToken);
        Process? worker = null;
        try
        {
            worker = RentWorker();
            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new
            {
                id = requestId,
                items = segments.Take(16).Select(item => new
                {
                    id = item.Id,
                    text = item.Text[..Math.Min(item.Text.Length, 1_500)],
                    source = SelectSourceRoute(item.Text, item.Language, sourceIsEnglish)
                })
            });
            // Переводчик — живой процесс: однажды он замолчал навсегда, и
            // вечное ожидание строки из его stdout заморозило весь дубляж
            // (доказано дампом зависшей сессии). Жёсткий бюджет на запрос,
            // по истечении — процесс убивается и поднимется заново.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await worker.StandardInput.WriteLineAsync(request.AsMemory(), budget.Token);
                await worker.StandardInput.FlushAsync(budget.Token);

                // ONNX Runtime may print a one-time informational line. Only a JSON
                // response carrying our correlation id is accepted.
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var line = await worker.StandardOutput.ReadLineAsync(budget.Token);
                    if (line is null) break;
                    TranslationReply? reply;
                    try { reply = JsonSerializer.Deserialize<TranslationReply>(line, JsonOptions); }
                    catch (JsonException) { continue; }
                    if (reply is null || !reply.Id.Equals(requestId, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrWhiteSpace(reply.Error))
                        throw new InvalidOperationException("OPUS: " + reply.Error);
                    return reply.Items?.Where(item => !string.IsNullOrWhiteSpace(item.Id) &&
                                                       !string.IsNullOrWhiteSpace(item.Text)).ToArray() ?? [];
                }
                throw new InvalidOperationException("Автономный переводчик завершился без ответа.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillWorker(worker);
                worker = null;
                throw new InvalidOperationException(
                    "Автономный переводчик не ответил за 45 с — процесс перезапущен.");
            }
        }
        catch
        {
            KillWorker(worker);
            worker = null;
            throw;
        }
        finally
        {
            if (worker is not null) ReturnWorker(worker);
            Slots.Release();
        }
    }

    private static void EnsureWorkerPool()
    {
        lock (Sync)
        {
            PruneWorkers();
            while (Workers.Count < MaxWorkers)
            {
                var started = StartWorker();
                Workers.Add(started);
                Idle.Enqueue(started);
            }
        }
    }

    private static Process RentWorker()
    {
        while (Idle.TryDequeue(out var idle))
        {
            if (!idle.HasExited) return idle;
            RemoveWorker(idle);
        }
        lock (Sync)
        {
            PruneWorkers();
            var started = StartWorker();
            Workers.Add(started);
            return started;
        }
    }

    private static void ReturnWorker(Process worker)
    {
        if (!worker.HasExited)
        {
            Idle.Enqueue(worker);
            return;
        }
        RemoveWorker(worker);
    }

    private static void RemoveWorker(Process worker)
    {
        lock (Sync) Workers.Remove(worker);
        try { worker.Dispose(); } catch { }
    }

    private static void PruneWorkers()
    {
        for (var index = Workers.Count - 1; index >= 0; index--)
        {
            if (!Workers[index].HasExited) continue;
            try { Workers[index].Dispose(); } catch { }
            Workers.RemoveAt(index);
        }
    }

    private static Process StartWorker()
    {
        var start = new ProcessStartInfo(AiModelCatalog.NodeExecutable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AiModelCatalog.Root
        };
        start.ArgumentList.Add(AiModelCatalog.TranslationAdapter);
        start.ArgumentList.Add(AiModelCatalog.MultilingualToEnglishRoot);
        start.ArgumentList.Add(AiModelCatalog.EnglishToRussianRoot);
        start.ArgumentList.Add(AiModelCatalog.KoreanToEnglishRoot);
        var process = Process.Start(start) ??
                      throw new InvalidOperationException("Не удалось запустить Nexus OPUS Translation.");
        _ = process.StandardError.ReadToEndAsync();
        ProcessNursery.Adopt(process);
        return process;
    }

    private static void KillWorker(Process? worker)
    {
        if (worker is null) return;
        lock (Sync) Workers.Remove(worker);
        try { if (!worker.HasExited) worker.Kill(true); } catch { }
        try { worker.Dispose(); } catch { }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            foreach (var worker in Workers)
            {
                try { if (!worker.HasExited) worker.Kill(true); } catch { }
                try { worker.Dispose(); } catch { }
            }
            Workers.Clear();
        }
        while (Idle.TryDequeue(out _)) { }
    }

    private static bool IsLikelyEnglish(string text, string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (text.Any(ch => char.IsLetter(ch) && ch > 127)) return false;
        var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z]+")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Value).ToArray();
        if (words.Length == 0) return false;
        var common = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "or", "to", "of", "in", "on", "for", "with", "from",
            "is", "are", "was", "were", "be", "this", "that", "you", "your", "we", "our",
            "read", "more", "search", "sign", "home", "next", "back", "close", "open"
        };
        return words.Any(common.Contains);
    }

    internal static string SelectSourceRoute(string text, string language, bool sourceIsEnglish)
    {
        if (sourceIsEnglish || IsLikelyEnglish(text, language)) return "en";
        if (language.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ||
            text.Any(ch => ch is >= '가' and <= '힯')) return "ko";
        return "auto";
    }
}
