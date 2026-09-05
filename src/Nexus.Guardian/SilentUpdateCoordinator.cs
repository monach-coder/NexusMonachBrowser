using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Guardian;

/// <summary>
/// Guardian-owned updater. It downloads to a side directory while the browser
/// keeps running, verifies the release ECDSA manifest, and applies files only
/// after the browser and its launcher have exited.
/// </summary>
internal static class SilentUpdateCoordinator
{
    private const string ReleaseApi =
        "https://api.github.com/repos/monach-coder/NexusMonachBrowser/releases/latest";
    // Лёгкое ядро вместо полного архива: AI-файлы в обновлении не участвуют —
    // остаются на месте (записи Pending в манифесте) и докачиваются сетевой
    // поставкой только при смене версии моделей. Полный офлайн-архив больше
    // лимита GitHub Release и для автообновления не используется.
    private const string ReleaseAssetName = "nexus-core.zip";
    private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 12L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 150_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string PendingPath(string guardianRoot) =>
        Path.Combine(guardianRoot, "Updates", "pending-update.json");

    /// <summary>
    /// Проверка и установка обновления ПРИ ЗАПУСКЕ установленного браузера:
    /// лаунчер до старта окна проверяет latest-релиз, при наличии новой
    /// версии скачивает её (сплэш показывает ход), применяет безопасным
    /// путём (заменённые файлы ставятся после выхода лаунчера) и
    /// перезапускает уже обновлённый браузер. Возвращает true, когда
    /// лаунчер обязан немедленно завершиться — апликатор сделал всё сам.
    /// Бюджет по времени: медленная сеть — стартуем текущую версию,
    /// фоновое обновление догонит как раньше.
    /// </summary>
    /// <summary>
    /// Отчёт хода обновления для сплэша: этап, процент загрузки (-1 —
    /// неопределённый), человекочитаемая деталь (МБ, версия).
    /// </summary>
    public sealed record UpdateProgress(string Stage, int Percent, string Detail);

    public static bool StartupUpdate(
        string applicationRoot, string guardianRoot,
        Action<string>? progress, TimeSpan budget,
        Action<UpdateProgress>? timeline = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(budget);
            var update = Task.Run(() => StartupUpdateAsync(
                applicationRoot, guardianRoot, progress, cts.Token, timeline));
            var elapsed = Stopwatch.StartNew();
            while (!update.Wait(40))
            {
                Application.DoEvents();
                if (elapsed.Elapsed > budget + TimeSpan.FromSeconds(10)) break;
            }
            return update.IsCompletedSuccessfully && update.Result;
        }
        catch
        {
            return false; // Обновление не должно мешать запуску браузера.
        }
    }

    private static async Task<bool> StartupUpdateAsync(
        string applicationRoot, string guardianRoot,
        Action<string>? progress, CancellationToken cancellationToken,
        Action<UpdateProgress>? timeline = null)
    {
        applicationRoot = NormalizeDirectory(applicationRoot);
        if (TryReadPending(guardianRoot, out _)) return false; // применится обычным путём
        progress?.Invoke("Проверяю обновления…");
        timeline?.Invoke(new UpdateProgress("Проверяю обновления…", -1, string.Empty));
        // Молчаливый отказ проверки — худший исход для диагностики (прецедент
        // 05.09 14:03: проверка умерла до скачивания, никто не узнал почему).
        // Причина обязана попасть в прогресс-файл и на сплэш.
        try
        {
            await CheckAndStageAsync(applicationRoot, guardianRoot, cancellationToken, timeline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timeline?.Invoke(new UpdateProgress("Проверка не успела за бюджет старта", -1,
                "догонит фоновая проверка"));
            WriteSplashProgress(guardianRoot, "проверка прервана стартом", -1,
                "бюджет старта исчерпан — догонит фоновая", done: true, found: false, null);
            return false;
        }
        catch (Exception ex)
        {
            timeline?.Invoke(new UpdateProgress("Обновление не проверено", -1, ex.Message));
            WriteSplashProgress(guardianRoot, "ошибка проверки", -1, ex.Message, done: true, found: false, null);
            return false;
        }
        if (!TryReadPending(guardianRoot, out var pending) || pending is null)
        {
            timeline?.Invoke(new UpdateProgress("Версия актуальна", -1, string.Empty));
            return false; // версия актуальна
        }
        progress?.Invoke($"Устанавливаю версию {pending.Version}…");
        timeline?.Invoke(new UpdateProgress($"Устанавливаю версию {pending.Version}…", -1,
            "браузер перезапустится сам"));
        return TryLaunchPendingApply(applicationRoot, guardianRoot, relaunch: true);
    }

    public static void StartBackgroundCheck(string applicationRoot, string guardianRoot)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(guardianRoot, "Updates"));
            var throttle = Path.Combine(guardianRoot, "Updates", "last-check.utc");
            if (File.Exists(throttle) &&
                File.GetLastWriteTimeUtc(throttle) > DateTime.UtcNow.AddHours(-6)) return;
            File.WriteAllText(throttle, DateTimeOffset.UtcNow.ToString("O"));

            var guardian = Path.Combine(applicationRoot, "NexusMonach.exe");
            var info = new ProcessStartInfo(guardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = applicationRoot
            };
            info.ArgumentList.Add("--background-update-check");
            info.ArgumentList.Add(applicationRoot);
            Process.Start(info)?.Dispose();
        }
        catch { /* Updates are retried later; browser startup must stay available. */ }
    }

    /// <summary>
    /// Скрытая проверка обновления для сплэша браузера: Guardian-процесс
    /// без окна пишет ход в Updates/update-progress.json (этап, проценты,
    /// мегабайты), круглый сплэш браузера читает файл и озвучивает вехи.
    /// Троттлинг общий с фоновой проверкой — API не дёргаем чаще раза в 6 часов.
    /// </summary>
    public static void StartSplashCheck(string applicationRoot, string guardianRoot)
    {
        try
        {
            var guardian = Path.Combine(applicationRoot, "NexusMonach.exe");
            var info = new ProcessStartInfo(guardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = applicationRoot
            };
            info.ArgumentList.Add("--splash-update-check");
            info.ArgumentList.Add(applicationRoot);
            Process.Start(info)?.Dispose();
        }
        catch { /* сплэш переживёт отсутствие проверки */ }
    }

    /// <summary>Путь прогресс-файла для сплэша браузера.</summary>
    public static string SplashProgressPath(string guardianRoot) =>
        Path.Combine(guardianRoot, "Updates", "update-progress.json");

    /// <summary>Запуск скрытой проверки обновления с записью прогресса.</summary>
    public static async Task<int> RunSplashCheckAsync(string applicationRoot, string guardianRoot)
    {
        // Жёсткий предел всей проверки: скачать+распаковать+хешировать гигабайты —
        // минуты, но не ЧАСЫ. Прецеденты 03–04.09: скрытая проверка зависала
        // (верификация стейджинга без дедлайна), её процесс жил сутями, а окно
        // Guardian держало мьютекс — браузер становился незапускаем. Таймаут
        // честно пишется в прогресс; следующая проверка начнёт заново.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            Directory.CreateDirectory(Path.Combine(guardianRoot, "Updates"));
            var throttle = Path.Combine(guardianRoot, "Updates", "last-check.utc");
            if (File.Exists(throttle) &&
                File.GetLastWriteTimeUtc(throttle) > DateTime.UtcNow.AddHours(-6))
            {
                WriteSplashProgress(guardianRoot, "актуальна", -1, string.Empty, done: true, found: false, null);
                return 0;
            }
            File.WriteAllText(throttle, DateTimeOffset.UtcNow.ToString("O"));
            WriteSplashProgress(guardianRoot, "проверяю обновления", -1, string.Empty, false, false, null);
            await CheckAndStageAsync(applicationRoot, guardianRoot, watchdog.Token,
                progress => WriteSplashProgress(guardianRoot,
                    progress.Stage, progress.Percent, progress.Detail, false, false, null));
            var found = TryReadPending(guardianRoot, out var pending) && pending is not null;
            WriteSplashProgress(guardianRoot, found ? "скачано" : "актуальна", -1,
                found ? "применю при следующем запуске" : string.Empty,
                done: true, found, pending?.Version);
            return 0;
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            WriteSplashProgress(guardianRoot, "проверка прервана", -1,
                "превышен лимит 20 минут — попробую в следующий раз", true, false, null);
            return 6;
        }
        catch (Exception ex)
        {
            WriteSplashProgress(guardianRoot, "ошибка проверки", -1, ex.Message, true, false, null);
            return 5;
        }
    }

    private static void WriteSplashProgress(string guardianRoot, string stage, int percent,
        string detail, bool done, bool found, string? version)
    {
        try
        {
            File.WriteAllText(SplashProgressPath(guardianRoot),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schema = 1, stage, percent, detail, done, found, version,
                    utc = DateTimeOffset.UtcNow
                }));
        }
        catch { /* прогресс — желательное, не обязательное */ }
    }

    public static async Task<int> CheckAndStageAsync(string applicationRoot, string guardianRoot,
        CancellationToken cancellationToken = default, Action<UpdateProgress>? timeline = null)
    {
        applicationRoot = NormalizeDirectory(applicationRoot);
        if (TryReadPending(guardianRoot, out _)) return 0;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexusGuardian", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        using var releaseResponse = await client.GetAsync(ReleaseApi,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        EnsureHttps(releaseResponse.RequestMessage?.RequestUri);
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream,
            new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var available)) return 0;
        var currentText = FileVersionInfo.GetVersionInfo(
            Path.Combine(applicationRoot, "NexusMonach.Browser.exe")).ProductVersion ?? "0.0.0";
        if (!TryParseVersion(currentText, out var current) || available <= current) return 0;

        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("name", out var name) &&
            ReleaseAssetName.Equals(name.GetString(), StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("Официальный архив обновления отсутствует в release.");
        var downloadUrl = new Uri(asset.GetProperty("browser_download_url").GetString()
                                  ?? throw new InvalidDataException("У release нет адреса архива."));
        EnsureHttps(downloadUrl);
        var declaredSize = asset.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64() : 0;
        if (declaredSize <= 0 || declaredSize > MaxArchiveBytes)
            throw new InvalidDataException("Недопустимый размер архива обновления.");
        var digest = asset.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString() : null;

        var updatesRoot = Path.Combine(guardianRoot, "Updates");
        Directory.CreateDirectory(updatesRoot);
        var safeVersion = available.ToString().Replace('.', '-');
        var archivePath = Path.Combine(updatesRoot, $"nexus-{safeVersion}.zip.part");
        timeline?.Invoke(new UpdateProgress($"Найдена версия {available}", -1,
            "скачиваю обновление"));
        await DownloadAsync(client, downloadUrl, archivePath, declaredSize, cancellationToken,
            (done, total) => timeline?.Invoke(new UpdateProgress(
                $"Скачиваю обновление {available}…",
                total > 0 ? (int)(done * 100 / total) : -1,
                $"{done / 1024.0 / 1024.0:F0} из {total / 1024.0 / 1024.0:F0} МБ")));
        timeline?.Invoke(new UpdateProgress("Проверяю подпись обновления…", -1,
            $"{declaredSize / 1024.0 / 1024.0:F0} МБ загружено"));
        if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:",
                StringComparison.OrdinalIgnoreCase))
        {
            var expected = digest[7..];
            var actual = await ComputeSha256Async(archivePath, cancellationToken);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("SHA-256 загруженного архива не совпадает с release.");
        }

        var staging = Path.Combine(updatesRoot, "staged-" + safeVersion);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ExtractArchiveSafely(archivePath, staging);
        var verification = IntegrityVerifier.Verify(staging, full: true);
        if (verification.State != IntegrityState.Verified)
            throw new CryptographicException("Guardian отклонил обновление: " +
                                             string.Join("; ", verification.Problems.Take(8)));

        WritePendingAtomically(guardianRoot, new PendingGuardianUpdate
        {
            Version = available.ToString(),
            StagingDirectory = staging,
            TargetDirectory = applicationRoot,
            CreatedUtc = DateTimeOffset.UtcNow
        });
        try { File.Delete(archivePath); } catch { }
        return 0;
    }

    /// <summary>
    /// Дешёвая проверка наличия применимого pending для решения о запуске
    /// (без полного хеширования staging — его делает апликатор). Лаунчер
    /// вызывает до DecideLaunch, чтобы решить: стартовать браузер, применить
    /// накопленное или лечить установку обновлением.
    /// </summary>
    public static bool IsPendingReady(string applicationRoot, string guardianRoot)
    {
        try
        {
            applicationRoot = NormalizeDirectory(applicationRoot);
            if (!TryReadPending(guardianRoot, out var pending) || pending is null ||
                !NormalizeDirectory(pending.TargetDirectory).Equals(applicationRoot,
                    StringComparison.OrdinalIgnoreCase)) return false;
            var staging = NormalizeDirectory(pending.StagingDirectory);
            return IsChildOf(Path.Combine(guardianRoot, "Updates"), staging) &&
                   File.Exists(Path.Combine(staging, "NexusMonach.exe")) &&
                   File.Exists(Path.Combine(staging, IntegrityVerifier.ManifestName));
        }
        catch { return false; }
    }

    public static bool TryLaunchPendingApply(string applicationRoot, string guardianRoot, bool relaunch)
    {
        try
        {
            applicationRoot = NormalizeDirectory(applicationRoot);
            if (!TryReadPending(guardianRoot, out var pending) || pending is null ||
                !NormalizeDirectory(pending.TargetDirectory).Equals(applicationRoot,
                    StringComparison.OrdinalIgnoreCase)) return false;
            var staging = NormalizeDirectory(pending.StagingDirectory);
            if (!IsChildOf(Path.Combine(guardianRoot, "Updates"), staging)) return false;
            if (IntegrityVerifier.Verify(staging, full: true).State != IntegrityState.Verified) return false;
            var stagedGuardian = Path.Combine(staging, "NexusMonach.exe");
            if (!File.Exists(stagedGuardian)) return false;

            var info = new ProcessStartInfo(stagedGuardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = staging
            };
            info.ArgumentList.Add("--apply-pending-update");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            if (relaunch) info.ArgumentList.Add("--relaunch");
            Process.Start(info)?.Dispose();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Решение после неудачной попытки применения: первые две попытки —
    /// ретрай тем же путём (занятый файл мог освободиться), третья —
    /// карантин pending и запуск браузера на текущей версии.
    /// </summary>
    internal enum ApplyFailureAction { Retry, Quarantine }

    internal static ApplyFailureAction DecideApplyFailure(int attempts) =>
        attempts >= 3 ? ApplyFailureAction.Quarantine : ApplyFailureAction.Retry;

    public static int ApplyPendingUpdate(string guardianRoot, int parentProcessId, bool relaunch)
    {
        if (!TryReadPending(guardianRoot, out var pending) || pending is null) return 2;
        var staging = NormalizeDirectory(pending.StagingDirectory);
        var target = NormalizeDirectory(pending.TargetDirectory);
        if (!NormalizeDirectory(AppContext.BaseDirectory).Equals(staging,
                StringComparison.OrdinalIgnoreCase) ||
            !IsChildOf(Path.Combine(guardianRoot, "Updates"), staging)) return 3;
        if (IntegrityVerifier.Verify(staging, full: true).State != IntegrityState.Verified) return 4;

        // Счётчик попыток переживает процесс: ретраи ограничены, петля
        // «старт → апликатор → падение» (прецедент 04–05.09) исключена.
        var attempt = pending.Attempts + 1;
        pending.Attempts = attempt;
        pending.LastAttemptUtc = DateTimeOffset.UtcNow;
        WritePendingAtomically(guardianRoot, pending);

        var rollbackRoot = Path.Combine(guardianRoot, "Updates", "rollback");
        try
        {
            if (parentProcessId > 0)
                try { Process.GetProcessById(parentProcessId).WaitForExit(30_000); }
                catch (ArgumentException) { }

            // До первого изменённого файла в target обязана существовать
            // копия для отката: провал посреди применения больше не оставляет
            // наполовину обновлённую установку без пути назад.
            CreateRollback(staging, target, rollbackRoot);
            ApplyVerifiedDirectory(staging, target);
            var result = IntegrityVerifier.Verify(target, full: true);
            if (result.State != IntegrityState.Verified)
            {
                // Неучтённые файлы (сюда же пишет диагностика рантайма и всё, что
                // пользователь положил в каталог руками) — не подмена: выносим их
                // в карантин и перепроверяем. Прецедент 03.09: video-dubbing-*.jsonl
                // и посторонний .md в каталоге установки превратили каждое
                // применение обновления в молчаливый отказ запуска.
                // Прерывают обновление только настоящие повреждения: неучтённый
                // ИСПОЛНЯЕМЫЙ файл, отсутствие/хеш компонентов.
                var quarantined = QuarantineUnaccountedFiles(guardianRoot, target, result.Problems);
                if (quarantined > 0)
                    result = IntegrityVerifier.Verify(target, full: true);
                if (result.State != IntegrityState.Verified)
                    throw new CryptographicException("Проверка установленного обновления не прошла: " +
                                                     string.Join("; ", result.Problems.Take(8)));
            }
            File.Delete(PendingPath(guardianRoot));
            // Каталог staging больше не нужен: обновление применено и проверено.
            try { Directory.Delete(staging, recursive: true); } catch { }
            DeleteRollback(rollbackRoot);
            if (relaunch)
            {
                // Перезапуск с меткой версии: браузер голосом сообщит
                // пользователю, что обновление встало.
                var relaunchInfo = new ProcessStartInfo(Path.Combine(target, "NexusMonach.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = target
                };
                relaunchInfo.Environment["NEXUS_UPDATED_VERSION"] = pending.Version;
                Process.Start(relaunchInfo)?.Dispose();
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Причина обязана остаться на диске: append-only, с номером попытки
            // и версией (раньше WriteAllText стирал историю ошибок применения).
            AppendApplyError(guardianRoot, attempt, pending.Version, ex);
            pending.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            WritePendingAtomically(guardianRoot, pending);

            // Закон самолечения: сначала рабочее состояние текущей версии,
            // затем запуск браузера; обновление повторится (до трёх попыток).
            var restored = TryRestoreRollback(rollbackRoot, target);
            var working = false;
            try { working = restored && IntegrityVerifier.Verify(target, full: true).CanLaunch; }
            catch { }

            if (DecideApplyFailure(attempt) == ApplyFailureAction.Quarantine)
            {
                QuarantinePending(guardianRoot, pending);
                DeleteRollback(rollbackRoot);
            }

            if (working && relaunch)
            {
                // Браузер стартует на откаченной старой версии и честно
                // сообщает о неудачном обновлении; проверка повторится позже.
                var relaunchInfo = new ProcessStartInfo(Path.Combine(target, "NexusMonach.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = target
                };
                relaunchInfo.Environment["NEXUS_UPDATE_FAILED"] = pending.Version;
                Process.Start(relaunchInfo)?.Dispose();
                return 7; // обновление не встало, откат выполнен
            }
            return 5;
        }
    }

    /// <summary>
    /// Копия заменяемых файлов текущей установки в Updates\rollback\: каждый
    /// файл манифеста обновления, существующий в target, плюс контрольные
    /// файлы Guardian. Провал подготовки отката прерывает применение ДО
    /// изменения первого файла.
    /// </summary>
    internal static void CreateRollback(string staging, string target, string rollbackRoot)
    {
        var newManifest = ReadManifest(staging);
        if (Directory.Exists(rollbackRoot))
            Directory.Delete(rollbackRoot, recursive: true);
        Directory.CreateDirectory(rollbackRoot);

        foreach (var entry in newManifest.Files)
        {
            if (entry.Pending) continue;
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(target, relative);
            if (!File.Exists(source)) continue;
            var backup = Path.Combine(rollbackRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup, overwrite: true);
        }

        foreach (var control in new[]
                 {
                     IntegrityVerifier.ManifestName, IntegrityVerifier.SignatureName,
                     IntegrityVerifier.PublicKeyName, "portable.flag"
                 })
        {
            var source = Path.Combine(target, control);
            if (File.Exists(source)) File.Copy(source, Path.Combine(rollbackRoot, control), overwrite: true);
        }

        File.WriteAllText(Path.Combine(rollbackRoot, "rollback-info.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            createdUtc = DateTimeOffset.UtcNow,
            pendingVersion = ReadManifest(staging).Version
        }, JsonOptions));
    }

    /// <summary>Восстанавливает файлы из rollback поверх target. true — если скопирован хотя бы один.</summary>
    internal static bool TryRestoreRollback(string rollbackRoot, string target)
    {
        try
        {
            if (!Directory.Exists(rollbackRoot)) return false;
            var restored = 0;
            foreach (var file in Directory.EnumerateFiles(rollbackRoot, "*",
                         SearchOption.AllDirectories))
            {
                if (file.EndsWith("rollback-info.json", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(rollbackRoot, file);
                var destination = Path.Combine(target, relative);
                if (!IsChildOf(target, Path.GetFullPath(destination))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                restored++;
            }
            return restored > 0;
        }
        catch { return false; }
    }

    private static void DeleteRollback(string rollbackRoot)
    {
        try { if (Directory.Exists(rollbackRoot)) Directory.Delete(rollbackRoot, recursive: true); }
        catch { /* остаток отката не должен ломать запуск */ }
    }

    /// <summary>
    /// Выносит неисправный pending в pending-update.rejected.json: петля
    /// повторных применений прерывается, следующая проверка обновлений
    /// начнёт заново со свежим стейджингом.
    /// </summary>
    internal static void QuarantinePending(string guardianRoot, PendingGuardianUpdate pending)
    {
        try
        {
            var rejected = Path.Combine(Path.GetDirectoryName(PendingPath(guardianRoot))!,
                "pending-update.rejected.json");
            File.WriteAllText(rejected, JsonSerializer.Serialize(pending, JsonOptions),
                new UTF8Encoding(false));
            File.Delete(PendingPath(guardianRoot));
        }
        catch { /* если не вынеслось — проверка обновлений перепишет pending */ }
    }

    internal static void AppendApplyError(string guardianRoot, int attempt, string version, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(guardianRoot, "Updates"));
            File.AppendAllText(Path.Combine(guardianRoot, "Updates", "apply-error.log"),
                $"{DateTimeOffset.UtcNow:O} attempt={attempt} version={version}: {ex}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destination)
    {
        destination = NormalizeDirectory(destination);
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("Слишком много файлов в архиве обновления.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("Распакованный архив обновления слишком велик.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Символические ссылки запрещены.");
            var path = Path.GetFullPath(Path.Combine(destination,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsChildOf(destination, path))
                throw new InvalidDataException("Архив обновления содержит выход за целевой каталог.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    /// <summary>
    /// Выносит «неучтённые» файлы из каталога установки в карантин
    /// (Guardian\Updates\quarantine\&lt;время&gt;\), чтобы посторонний файл
    /// не блокировал применение обновления. Возвращает число вынесенных.
    /// </summary>
    private static int QuarantineUnaccountedFiles(string guardianRoot, string target,
        IReadOnlyList<string> problems)
    {
        const string prefix = "Неучтённый файл: ";
        var moved = 0;
        var quarantineRoot = Path.Combine(guardianRoot, "Updates", "quarantine",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        foreach (var problem in problems)
        {
            if (!problem.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = problem[prefix.Length..];
            try
            {
                var source = Path.GetFullPath(Path.Combine(target,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                // «Неучтённый исполняемый файл» сюда не попадает: другой префикс
                // и другая угроза — такие файлы должны прерывать обновление.
                if (!IsChildOf(target, source) || !File.Exists(source)) continue;
                var destination = Path.Combine(quarantineRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
                moved++;
            }
            catch { /* Не получилось вынести — проверка снова его покажет. */ }
        }
        if (moved > 0)
        {
            try
            {
                File.AppendAllText(Path.Combine(guardianRoot, "Updates", "quarantine.log"),
                    $"{DateTimeOffset.Now:O} в карантин вынесено файлов: {moved}" + Environment.NewLine);
            }
            catch { }
        }
        return moved;
    }

    private static void ApplyVerifiedDirectory(string staging, string target)
    {
        Directory.CreateDirectory(target);
        var newManifest = ReadManifest(staging);
        IntegrityManifest? oldManifest = null;
        try { oldManifest = ReadManifest(target); } catch { }

        var entries = newManifest.Files.OrderBy(entry =>
            entry.Path.Equals("NexusMonach.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
        foreach (var entry in entries)
        {
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(staging, relative);
            // Pending-файлы (AI-пакеты) приезжают сетевой поставкой, а не ядром
            // обновления: их отсутствие в staging — норма, а не ошибка.
            if (entry.Pending && !File.Exists(source)) continue;
            CopyAtomically(source, Path.Combine(target, relative));
        }

        if (oldManifest is not null)
        {
            var current = newManifest.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obsolete in oldManifest.Files.Where(x => !current.Contains(x.Path)))
            {
                var path = Path.GetFullPath(Path.Combine(target,
                    obsolete.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (IsChildOf(target, path) && File.Exists(path)) File.Delete(path);
            }
        }

        foreach (var control in new[]
                 {
                     IntegrityVerifier.ManifestName, IntegrityVerifier.SignatureName,
                     IntegrityVerifier.PublicKeyName, "portable.flag"
                 })
        {
            var source = Path.Combine(staging, control);
            if (File.Exists(source)) CopyAtomically(source, Path.Combine(target, control));
        }
    }

    private static void CopyAtomically(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".nexus-new-" + Guid.NewGuid().ToString("N");
        try
        {
            // Кратковременно занятый файл (антивирус, индексатор поиска —
            // прецедент UnauthorizedAccessException 04.09) лечится коротким
            // ретраем с паузой, а не падением всего применения.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, temporary, overwrite: true);
                    File.Move(temporary, destination, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 3 &&
                                           ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static IntegrityManifest ReadManifest(string root) =>
        JsonSerializer.Deserialize<IntegrityManifest>(File.ReadAllBytes(
            Path.Combine(root, IntegrityVerifier.ManifestName)))
        ?? throw new InvalidDataException("Манифест обновления не читается.");

    private static async Task DownloadAsync(HttpClient client, Uri url, string destination,
        long declaredSize, CancellationToken cancellationToken,
        Action<long, long>? progress = null)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        var responseSize = response.Content.Headers.ContentLength;
        if (responseSize is > MaxArchiveBytes || responseSize is > 0 && responseSize != declaredSize)
            throw new InvalidDataException("Размер ответа обновления не совпадает с release.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        var lastReport = DateTimeOffset.MinValue;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > MaxArchiveBytes || total > declaredSize)
                throw new InvalidDataException("Архив обновления превысил заявленный размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            // Прогресс — не чаще 4 раз в секунду: сплэш живой, интерфейс не захлёбывается.
            if (progress is not null && (DateTimeOffset.UtcNow - lastReport).TotalMilliseconds >= 250)
            {
                lastReport = DateTimeOffset.UtcNow;
                progress(total, declaredSize);
            }
        }
        if (total != declaredSize) throw new EndOfStreamException("Архив обновления загружен не полностью.");
        progress?.Invoke(declaredSize, declaredSize);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool TryReadPending(string guardianRoot, out PendingGuardianUpdate? pending)
    {
        pending = null;
        try
        {
            var path = PendingPath(guardianRoot);
            if (!File.Exists(path)) return false;
            pending = JsonSerializer.Deserialize<PendingGuardianUpdate>(File.ReadAllText(path));
            return pending?.SchemaVersion == 1;
        }
        catch { return false; }
    }

    private static void WritePendingAtomically(string guardianRoot, PendingGuardianUpdate pending)
    {
        var path = PendingPath(guardianRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void EnsureHttps(Uri? uri)
    {
        if (uri is null || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Guardian запретил незашифрованный источник обновления.");
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsChildOf(string parent, string candidate)
    {
        parent = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        candidate = Path.GetFullPath(candidate);
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        value = value.Trim().TrimStart('v', 'V');
        var numeric = new string(value.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return Version.TryParse(numeric, out version!);
    }
}
