using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace NexusMonach.Services.OnlinePack;

/// <summary>
/// Фоновая подтяжка AI-моделей по сети. Браузер полноценно работает и без
/// нейронок; при наличии подписанного манифеста поставки сервис сверяет
/// локальные файлы с манифестом и докачивает только отсутствующее —
/// группами, с возобновлением и голосовым итогом: «Модели перевода
/// загружены. Перевод видео доступен».
/// </summary>
public static class AiPackFetchService
{
    private static int _running;
    private static Action? _onPacksReady;
    private static bool _fetchPending;

    /// <summary>
    /// Регистрирует прогрев конвейеров на момент готовности пакетов.
    /// Возвращает true, если поставка ещё не приехала (прогрев отложен),
    /// false — когда греть можно сразу.
    /// </summary>
    public static bool WarmUpAfterFetch(Action warmUp)
    {
        _onPacksReady = warmUp;
        _fetchPending = !string.IsNullOrWhiteSpace(
            SettingsService.Current.AiPackManifestUrl);
        return _fetchPending;
    }

    private static void RaisePacksReady()
    {
        var warmUp = _onPacksReady;
        _onPacksReady = null;
        _fetchPending = false;
        warmUp?.Invoke();
    }

    /// <summary>
    /// Запускает фоновую синхронизацию моделей. URL манифеста берётся из
    /// настроек; отсутствие настройки, манифеста или подписи — тихий отказ:
    /// это необязательная функция, а не сбой браузера.
    /// </summary>
    public static void StartBackgroundFetch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = SettingsService.Current;
        if (string.IsNullOrWhiteSpace(settings.AiPackManifestUrl)) return;
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        _ = Task.Run(() => FetchLoopAsync(settings.AiPackManifestUrl));
    }

    private static async Task FetchLoopAsync(string manifestUrl)
    {
        try
        {
            var root = AppContext.BaseDirectory;
            // Публичный ключ Guardian лежит рядом с установкой: им же подписаны
            // манифесты поставки.
            var publicKeyPath = Path.Combine(root, "integrity-public-key.pem");
            if (!File.Exists(publicKeyPath)) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var manifestBytes = await http.GetByteArrayAsync(manifestUrl);
            var signatureUrl = manifestUrl.TrimEnd('/') + ".sig";
            var signature = await http.GetStringAsync(signatureUrl);
            if (!ReleaseManifestVerifier.Verify(manifestBytes, signature.Trim(),
                    await File.ReadAllTextAsync(publicKeyPath)))
            {
                CrashReportService.AddBreadcrumb("ai-pack", "manifest-signature-invalid");
                return;
            }

            var manifest = ReleaseManifest.Parse(manifestBytes);
            var baseUrl = new Uri(new Uri(manifestUrl), ".");
            foreach (var payload in manifest.Files.Where(f =>
                         f.Group.Equals("ai", StringComparison.OrdinalIgnoreCase)))
            {
                // AI приезжает одним архивом: скачиваем в temp, проверяем хеш,
                // распаковываем в корень установки (архив содержит дерево AI/).
                var stagedZip = Path.Combine(Path.GetTempPath(), payload.RelativePath);
                var outcome = await ResumableDownloader.DownloadAsync(
                    baseUrl, payload.RelativePath, stagedZip, payload,
                    cancellationToken: CancellationToken.None);
                if (outcome is not (DownloadOutcome.Completed or DownloadOutcome.AlreadyFresh))
                {
                    CrashReportService.AddBreadcrumb("ai-pack",
                        "download-" + outcome.ToString().ToLowerInvariant());
                    continue;
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(stagedZip, root, overwriteFiles: true);
                try { File.Delete(stagedZip); } catch { }
                CrashReportService.AddBreadcrumb("ai-pack", "pack-extracted");
            }

            // Все пакеты на месте — конвейеры можно греть.
            RaisePacksReady();
            Ui.Post(() => VoiceAssistantService.Announce(
                "Нейросети загружены. Перевод, голос и распознавание доступны.",
                VoiceAnnouncementPriority.Important));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("ai-pack", "background-fetch", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Человекочитаемое имя группы для голосового сообщения.</summary>
    internal static string VoiceFriendlyName(string purpose) => purpose switch
    {
        var p when p.Contains("whisper", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("распознав", StringComparison.OrdinalIgnoreCase) => "перевод видео",
        var p when p.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("голос", StringComparison.OrdinalIgnoreCase) => "голос помощника",
        var p when p.Contains("translation", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("перевод", StringComparison.OrdinalIgnoreCase) => "перевод страниц",
        var p when p.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("семант", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("e5", StringComparison.OrdinalIgnoreCase) => "семантический поиск",
        var p when p.Contains("нейрос", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("все", StringComparison.OrdinalIgnoreCase) => "перевод, голос и распознавание",
        _ => "компонент"
    };
}
