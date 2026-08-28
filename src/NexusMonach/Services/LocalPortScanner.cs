namespace NexusMonach.Services;

/// <summary>
/// Одна строка результата сканирования: порт, адрес привязки, владелец и оценка.
/// </summary>
public sealed record LocalPortEntry(
    string Protocol,
    int Port,
    string Address,
    string ProcessName,
    string Risk,
    string Note,
    int Severity)
{
    /// <summary>0 — норма, 1 — внимание, 2 — опасно.</summary>
    public System.Windows.Media.Brush RiskBrush =>
        Severity switch
        {
            2 => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)),
            1 => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB8, 0x6C)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0xD8, 0x90))
        };
}

/// <summary>
/// Сканер открытых портов этой машины: показывает, кто слушает соединения
/// и насколько это рискованно в анонимном режиме. Данные берёт из
/// <see cref="WindowsPortService"/> (IP Helper API, без прав администратора),
/// затем оценивает каждый порт по справочнику рисков.
/// </summary>
public static class LocalPortScanner
{
    private static readonly HashSet<int> HoneypotPorts = [2222, 3000, 5000, 6379, 8080, 8888, 27017];

    /// <summary>
    /// Сканирует слушающие порты и возвращает записи, отсортированные
    /// по убыванию опасности, затем по протоколу и номеру порта.
    /// </summary>
    public static List<LocalPortEntry> Scan()
    {
        var ports = WindowsPortService.GetListeningPorts();
        var entries = ports
            .GroupBy(p => (p.Protocol, p.Port, p.Address))
            .Select(g => g.OrderByDescending(p => p.ProcessId != 0).First())
            .Select(Classify)
            .ToList();
        return entries
            .OrderByDescending(e => e.Severity)
            .ThenBy(e => e.Protocol)
            .ThenBy(e => e.Port)
            .ToList();
    }

    /// <summary>
    /// Оценивает один слушатель. Чистая функция без обращений к ОС —
    /// проверяется юнит-тестами на платформе без Windows.
    /// </summary>
    internal static LocalPortEntry Classify(LocalPortInfo info)
    {
        var exposed = IsWildcard(info.Address);
        var isOwn = info.ProcessName.StartsWith("NexusMonach", StringComparison.OrdinalIgnoreCase);

        // Наши ловушки и Tor — ожидаемые слушатели анонимного режима.
        if (isOwn && info.Protocol == "TCP" && HoneypotPorts.Contains(info.Port))
            return new LocalPortEntry(info.Protocol, info.Port, info.Address, info.ProcessName,
                "Ловушка Дозора", "Приманка для сканеров — это наша защита", 0);
        if (info.Protocol == "TCP" && info.Port is 9050 or 9051 &&
            info.ProcessName.StartsWith("tor", StringComparison.OrdinalIgnoreCase))
            return new LocalPortEntry(info.Protocol, info.Port, info.Address, info.ProcessName,
                "След", "Прокси-порт режима След", 0);

        var (risk, note, severity) = (info.Protocol, info.Port) switch
        {
            (_, 53) => ("DNS", "Может обходить маршрут — проверьте стража DNS", 1),
            ("UDP", 5353) => ("mDNS", "Утечка имён локальной сети", 1),
            (_, 1900) => ("SSDP/UPnP", "Утечка топологии сети", 1),
            (_, 137 or 138 or 139) => ("NetBIOS", "Раздаёт имена машины в сеть", 1),
            // Порты ниже — службы пользователя: порт-щит их принципиально
            // НЕ закрывает автоматически, чтобы не сломать удалённый доступ
            // и файлообмен. Сообщение — проверка, а не приказ.
            (_, 445) => ("SMB", "Общий доступ к файлам: порт-щит не трогает — отключите, если не пользуетесь", 2),
            (_, 3389) => ("RDP", "Удалённый рабочий стол: порт-щит не трогает — отключите, если не пользуетесь", 2),
            (_, 5900) => ("VNC", "Удалённое управление: порт-щит не трогает — отключите, если не пользуетесь", 2),
            (_, 22 or 23) => ("Telnet/SSH", "Удалённый терминал: порт-щит не трогает — отключите, если не пользуетесь", 2),
            (_, 1080 or 8118) => ("Прокси", "Может конфликтовать с маршрутом", 1),
            _ => ("Открыт", IsLoopback(info.Address) ? "Локальный слушатель" : "Слушатель машины", 0)
        };

        // Привязка ко всем интерфейсам означает видимость из локальной сети.
        if (exposed)
        {
            if (severity == 0)
            {
                risk = "Доступен из сети";
                note = "Привязан ко всем интерфейсам (0.0.0.0)";
                severity = 1;
            }
            else
            {
                note += " · доступен из сети";
            }
        }
        return new LocalPortEntry(info.Protocol, info.Port, info.Address, info.ProcessName,
            risk, note, severity);
    }

    private static bool IsWildcard(string address) =>
        address.Equals("0.0.0.0", StringComparison.Ordinal) ||
        address.Equals("::", StringComparison.Ordinal) ||
        address.Equals(":::", StringComparison.Ordinal);

    private static bool IsLoopback(string address) =>
        address.StartsWith("127.", StringComparison.Ordinal) ||
        address.Equals("::1", StringComparison.Ordinal);
}
