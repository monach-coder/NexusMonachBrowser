using System.IO;
using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// Наблюдатель скрытой проверки обновления для круглого сплэша: читает
/// Updates/update-progress.json (его пишет скрытый Guardian-процесс),
/// закрывает секторы кольца (обновление → загрузка) и озвучивает вехи —
/// «нашёл версию, качаю», «скачано, применю при перезапуске». Молчит,
/// когда версия актуальна: пустой болтовни на каждом старте не будет.
/// </summary>
public static class SplashUpdateWatcher
{
    private sealed record ProgressState(
        string Stage, int Percent, string Detail, bool Done, bool Found, string? Version);

    public static async Task RunAsync(Views.SplashWindow splash)
    {
        var path = Path.Combine(AppPaths.AppRoot, "Guardian", "Updates", "update-progress.json");
        if (!File.Exists(path)) return;
        // Файл от прошлого запуска? Смотрим только свежие (моложе 2 минут).
        if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-2)) return;

        var announcedDownload = false;
        var announcedDone = false;
        var startedUtc = DateTimeOffset.UtcNow;
        var lastSeen = string.Empty;
        while (true)
        {
            // Сплэш закрылся (запуск закончился) или скрытая проверка молчит
            // дольше трёх минут (файл не меняется, процесс погиб) — тихо
            // выходим: опрос каждые 400 мс не должен жить всю сессию.
            // Любое обновление файла сбрасывает счётчик: долгая загрузка
            // на медленной сети легальна и не должна обрываться.
            var splashAlive = splash.Dispatcher.CheckAccess()
                ? splash.IsLoaded
                : (bool)splash.Dispatcher.Invoke(() => splash.IsLoaded);
            if (!splashAlive || DateTimeOffset.UtcNow - startedUtc > TimeSpan.FromMinutes(3))
                return;
            ProgressState? state = null;
            string? raw = null;
            try
            {
                raw = await File.ReadAllTextAsync(path);
                state = JsonSerializer.Deserialize<ProgressState>(raw);
            }
            catch { /* файл пишется прямо сейчас — попробуем ещё раз */ }
            if (raw is not null && !string.Equals(raw, lastSeen, StringComparison.Ordinal))
            {
                lastSeen = raw;
                startedUtc = DateTimeOffset.UtcNow;
            }
            if (state is not null)
            {
                var stageText = state.Stage switch
                {
                    "проверяю обновления" => "Проверяю обновления…",
                    "скачано" => "Обновление скачано · применю при перезапуске",
                    "актуальна" => "Версия актуальна",
                    "ошибка проверки" => "Проверка обновления не удалась",
                    _ => state.Stage
                };
                splash.SetStatus(stageText);
                splash.SetDetail(state.Detail);
                switch (state.Stage)
                {
                    case "проверяю обновления":
                        splash.ActivateSector(1, -1);
                        break;
                    case "актуальна":
                    case "ошибка проверки":
                        splash.CompleteSector(1);
                        break;
                    case "скачано":
                        splash.CompleteSector(1);
                        splash.CompleteSector(2);
                        break;
                    default:
                        if (state.Stage.StartsWith("Найдена", StringComparison.Ordinal))
                        {
                            splash.CompleteSector(1);
                            splash.ActivateSector(2, -1);
                        }
                        else if (state.Stage.StartsWith("Скачиваю", StringComparison.Ordinal))
                        {
                            splash.CompleteSector(1);
                            splash.ActivateSector(2, state.Percent);
                        }
                        else if (state.Stage.StartsWith("Проверяю подпись", StringComparison.Ordinal))
                        {
                            splash.CompleteSector(1);
                            splash.CompleteSector(2);
                        }
                        break;
                }

                if (!announcedDownload && state.Found && !state.Done)
                {
                    announcedDownload = true;
                    VoiceAssistantService.Announce(
                        "Найдена новая версия" +
                        (string.IsNullOrEmpty(state.Version) ? "" : " " + state.Version) +
                        ". Скачиваю, не мешаю работе.",
                        VoiceAnnouncementPriority.Important);
                }
                if (!announcedDone && state.Done && state.Found)
                {
                    announcedDone = true;
                    VoiceAssistantService.Announce(
                        "Обновление скачано. Применю при следующем запуске.",
                        VoiceAnnouncementPriority.Important);
                }
                if (state.Done) return;
            }
            await Task.Delay(400);
        }
    }
}
