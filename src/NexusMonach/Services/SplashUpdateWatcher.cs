using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace NexusMonach.Services;

/// <summary>
/// Наблюдатель скрытой проверки обновления для круглого сплэша: читает
/// Updates/update-progress.json (его пишет скрытый Guardian-процесс),
/// рисует кольцо-шкалу, этапы и озвучивает вехи — «нашёл версию, качаю»,
/// «скачано, применю при перезапуске». Молчит, когда версия актуальна:
/// пустой болтовни на каждом старте не будет.
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
        while (true)
        {
            ProgressState? state = null;
            try
            {
                state = JsonSerializer.Deserialize<ProgressState>(
                    await File.ReadAllTextAsync(path));
            }
            catch { /* файл пишется прямо сейчас — попробуем ещё раз */ }
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
                splash.SetProgress(state.Percent, state.Detail);
                splash.SetStage(state.Stage switch
                {
                    "проверяю обновления" => 1,
                    var s when s.StartsWith("Найдена", StringComparison.Ordinal) ||
                                      s.StartsWith("Скачиваю", StringComparison.Ordinal) => 2,
                    "скачано" => 3,
                    "актуальна" => 4,
                    _ => 1
                });

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
