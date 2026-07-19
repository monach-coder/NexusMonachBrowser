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

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static Process? _process;

    public static void WarmUpInBackground()
    {
        if (!AiModelCatalog.TranslationReady) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // The browser is already visible at this point. Preload only the
                // smaller English -> Russian stage used by live video captions;
                // the multilingual stage stays lazy until a page needs it.
                await Task.Delay(TimeSpan.FromSeconds(3));
                using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await TranslateToRussianAsync("Translation service is ready.", true, budget.Token);
            }
            catch { /* Readiness is reported when the user explicitly translates. */ }
        });
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

        await Gate.WaitAsync(cancellationToken);
        try
        {
            EnsureProcess();
            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new
            {
                id = requestId,
                items = segments.Take(16).Select(item => new
                {
                    id = item.Id,
                    text = item.Text[..Math.Min(item.Text.Length, 1_500)],
                    source = SourceRoute(item.Text, item.Language, sourceIsEnglish)
                })
            });
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);

            // ONNX Runtime may print a one-time informational line. Only a JSON
            // response carrying our correlation id is accepted.
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
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
        catch (OperationCanceledException)
        {
            StopProcess();
            throw;
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally { Gate.Release(); }
    }

    private static void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;
        StopProcess();
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
        _process = Process.Start(start) ??
                   throw new InvalidOperationException("Не удалось запустить Nexus OPUS Translation.");
        _ = _process.StandardError.ReadToEndAsync();
    }

    public static void Stop()
        => StopProcess();

    private static void StopProcess()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
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

    private static string SourceRoute(string text, string language, bool sourceIsEnglish)
    {
        if (sourceIsEnglish || IsLikelyEnglish(text, language)) return "en";
        if (language.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ||
            text.Any(ch => ch is >= '\uAC00' and <= '\uD7AF')) return "ko";
        return "auto";
    }
}
