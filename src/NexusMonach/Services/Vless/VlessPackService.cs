using System.IO.Compression;
using System.Net.Http;
using NexusMonach.Services.OnlinePack;

namespace NexusMonach.Services.Vless;

/// <summary>
/// Доставка транспортного модуля (группа «xray» в подписанном манифесте
/// поставки) по требованию: пак не входит ни в ядро, ни в фоновую подтяжку
/// AI — он скачивается только когда пользователь подключает собственный
/// сервер. Хеш проверяется тем же ECDSA-манифестом, что и модели.
/// </summary>
public static class VlessPackService
{
    /// <summary>
    /// Гарантирует наличие xray.exe в установке. Возвращает результат с
    /// человекочитаемым сообщением для статуса в настройках.
    /// </summary>
    public static async Task<(bool Success, string Message)> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (VlessRuntime.FindXray() is not null)
            return (true, "Транспортный модуль уже установлен.");

        var manifestUrl = SettingsService.Current.AiPackManifestUrl;
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return (false, "Манифест сетевой поставки не настроен — установите браузер сетевым установщиком или обновите его.");

        var publicKeyPath = Path.Combine(AppContext.BaseDirectory, "integrity-public-key.pem");
        if (!File.Exists(publicKeyPath))
            return (false, "Не найден ключ проверки поставки (integrity-public-key.pem).");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            progress?.Report("Проверяю манифест поставки…");
            var manifestBytes = await http.GetByteArrayAsync(manifestUrl, ct);
            var signature = await http.GetStringAsync(manifestUrl.TrimEnd('/') + ".sig", ct);
            if (!ReleaseManifestVerifier.Verify(manifestBytes, signature.Trim(),
                    await File.ReadAllTextAsync(publicKeyPath, ct)))
            return (false, "Подпись манифеста поставки не сошлась — загрузка отменена.");

            var manifest = ReleaseManifest.Parse(manifestBytes);
            var pack = manifest.Files.FirstOrDefault(f =>
                f.Group.Equals("xray", StringComparison.OrdinalIgnoreCase));
            if (pack is null)
                return (false, "В манифесте поставки нет транспортного модуля (устаревший релиз?).");

            progress?.Report($"Скачиваю транспорт, {pack.Length / 1024.0 / 1024.0:F0} МБ…");
            var stagedZip = Path.Combine(Path.GetTempPath(), pack.RelativePath);
            var baseUrl = new Uri(new Uri(manifestUrl), ".");
            var outcome = await ResumableDownloader.DownloadAsync(
                baseUrl, pack.RelativePath, stagedZip, pack, cancellationToken: ct);
            if (outcome is not (DownloadOutcome.Completed or DownloadOutcome.AlreadyFresh))
                return (false, "Загрузка прервана (" + outcome + "). Проверьте сеть и повторите.");

            progress?.Report("Распаковываю…");
            ZipFile.ExtractToDirectory(stagedZip, AppContext.BaseDirectory, overwriteFiles: true);
            try { File.Delete(stagedZip); } catch { }

            return VlessRuntime.FindXray() is not null
                ? (true, "Транспортный модуль установлен.")
                : (false, "Архив скачан, но xray.exe в нём не найден.");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("vless", "pack-install", ex);
            return (false, "Не удалось скачать транспорт: " + ex.Message);
        }
    }
}
