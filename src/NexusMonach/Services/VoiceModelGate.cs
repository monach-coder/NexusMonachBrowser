using System.IO;

namespace NexusMonach.Services;

/// <summary>
/// Ожидание готовности нейроголоса: модель должна приехать на диск ДО
/// открытия главного окна — чтобы браузер заговорил живым голосом с первой
/// секунды, а не роботом SAPI. Сплэш показывает статус; таймаут 90 секунд
/// защищает от вечного ожидания на медленной сети.
/// </summary>
public static class VoiceModelGate
{
    /// <summary>
    /// Ждать готовности голосовой модели. Возвращает true когда готова,
    /// false — по таймауту (браузер откроется с SAPI-фоллбэком).
    /// </summary>
    public static async Task<bool> WaitAsync(
        Views.SplashWindow splash,
        TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(90);
        var deadline = DateTimeOffset.UtcNow + budget;
        var lastPercent = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // Silero-модель на месте?
            if (Services.AiModelCatalog.SileroVoiceReady ||
                Services.AiModelCatalog.PiperVoiceReady)
            {
                splash.SetStatus("Нейроголос готов");
                splash.SetDetail(string.Empty);
                return true;
            }

            // Модель не готова — смотрим докачку (.part файлы) для прогресса.
            var (percent, detail) = DownloadProgress();
            if (percent != lastPercent)
            {
                lastPercent = percent;
                splash.SetStatus("Загружаю нейросети для голоса…");
                splash.SetDetail(detail);
                if (percent >= 0)
                    splash.ActivateSector(2, percent);
                else
                    splash.ActivateSector(2, -1);
            }

            await Task.Delay(1000);
        }

        // Таймаут: браузер открывается с SAPI — честно предупредить.
        splash.SetStatus("Нейроголос не успел — открываю с резервным голосом");
        return false;
    }

    /// <summary>Прогресс докачки AI-пакетов из .part-файлов.</summary>
    private static (int Percent, string Detail) DownloadProgress()
    {
        try
        {
            var temp = Path.GetTempPath();
            var modelsPart = Path.Combine(temp, "nexus-ai-models.zip.part");
            var runtimePart = Path.Combine(temp, "nexus-ai-runtime.zip.part");
            long current = 0;
            var parts = 0;

            if (File.Exists(modelsPart))
            {
                current += new FileInfo(modelsPart).Length;
                parts++;
            }
            if (File.Exists(runtimePart))
            {
                current += new FileInfo(runtimePart).Length;
                parts++;
            }

            if (parts == 0) return (-1, string.Empty);

            // Ожидаемые размеры (приблизительно, из манифеста поставки):
            // models ~1.26 ГБ, runtime ~0.42 ГБ — итого ~1.68 ГБ.
            const long expectedTotal = 1_803_540_480;
            var percent = (int)(current * 100 / expectedTotal);
            var mb = current / 1024.0 / 1024.0;
            return (Math.Clamp(percent, 0, 99), $"{mb:F0} МБ из {expectedTotal / 1024 / 1024} МБ");
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
