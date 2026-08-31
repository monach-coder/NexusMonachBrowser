using NAudio.CoreAudioApi;

namespace NexusMonach.Services;

/// <summary>Устройство вывода голоса.</summary>
public sealed record VoiceOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Менеджер аудиовыхода голоса: голос должен звучать из устройства, которое
/// Windows считает умолчанием — а не из закешированного NAudio-устройства.
/// Список устройств доступен через WASAPI (MMDeviceEnumerator); при
/// пропадании выбранного устройства — автоматический откат на умолчание
/// с голосовым предупреждением.
/// </summary>
public static class VoiceOutputService
{
    private static string? _deviceId;

    /// <summary>Список активных устройств вывода (WASAPI).</summary>
    public static IReadOnlyList<VoiceOutputDevice> Devices
    {
        get
        {
            var result = new List<VoiceOutputDevice>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                result.Add(new VoiceOutputDevice(
                    defaultDevice?.ID ?? "default",
                    defaultDevice?.FriendlyName ?? "Умолчание Windows", true));
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.ID != defaultDevice?.ID)
                        result.Add(new VoiceOutputDevice(device.ID, device.FriendlyName, false));
                }
            }
            catch
            {
                result.Add(new VoiceOutputDevice("default", "Умолчание Windows", true));
            }
            return result;
        }
    }

    /// <summary>Имя текущего устройства.</summary>
    public static string DeviceName
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device?.FriendlyName ?? "Умолчание Windows";
            }
            catch
            {
                return "Умолчание Windows";
            }
        }
    }

    /// <summary>Выбранное устройство (пусто = умолчание).</summary>
    public static string? SelectedDeviceId => _deviceId;

    /// <summary>Выбрать устройство по WASAPI ID.</summary>
    public static void Select(string? deviceId)
    {
        _deviceId = deviceId;
        if (!string.IsNullOrWhiteSpace(deviceId))
            CrashReportService.AddBreadcrumb("voice-output", "selected-" + deviceId[..Math.Min(20, deviceId.Length)]);
    }

    /// <summary>Восстановить из настроек (по имени устройства).</summary>
    public static void SelectByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Select(null);
            return;
        }
        foreach (var device in Devices)
        {
            if (device.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                Select(device.Id);
                return;
            }
        }
        // Устройство пропало — откат на умолчание.
        Select(null);
    }

    /// <summary>
    /// Текущее устройство живо? (проверка через WASAPI-перечислитель)
    /// </summary>
    public static bool IsDeviceAlive()
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (device.ID == _deviceId)
                    return true;
            return false;
        }
        catch
        {
            return true; // не можем проверить — считаем живым
        }
    }

    /// <summary>
    /// Автолечение: если устройство исчезло — вернуть умолчание и сообщить.
    /// Вызывается перед каждым воспроизведением.
    /// </summary>
    public static void HealIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(_deviceId) && !IsDeviceAlive())
        {
            Select(null);
            Ui.Post(() => VoiceAssistantService.Announce(
                "Аудиоустройство отключено. Голос переключён на умолчание.",
                VoiceAnnouncementPriority.Important));
        }
    }
}
