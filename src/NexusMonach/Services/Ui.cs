using System.Windows.Threading;

namespace NexusMonach.Services;

/// <summary>
/// Единственная точка доступа бизнес-сервисов к UI-потоку. Сервисы не
/// трогают WPF напрямую: маршализация идёт через этот шлюз. Без запущенного
/// приложения (юнит-тесты, консольная диагностика) действия исполняются
/// синхронно — сервисы остаются тестируемыми без окна.
/// </summary>
public static class Ui
{
    private static Dispatcher? _dispatcher;

    /// <summary>Захватывает диспетчер приложения; вызывается на старте UI.</summary>
    public static void CaptureFrom(System.Windows.Application application) =>
        _dispatcher = application.Dispatcher;

    /// <summary>
    /// Выполняет действие в UI-потоке. Без диспетчера или уже в UI-потоке —
    /// синхронно на месте.
    /// </summary>
    public static void Invoke(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>Выполняет действие в UI-потоке без ожидания (fire-and-forget).</summary>
    public static void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
