namespace NexusMonach.Services;

/// <summary>
/// Наблюдатель брошенных по таймауту проб портов. Проба ждёт соединение
/// ограниченное время (Wait с таймаутом) и при его превышении просто возвращает
/// «не отвечает». Но сама задача ConnectAsync продолжает жить и завершается
/// позже исключением — без наблюдателя оно всплывает из финализатора как
/// «unobserved task exception» и превращается в ложный рапорт о сбое.
/// </summary>
public static class PortProbeObserver
{
    public static void Observe(Task connect)
    {
        _ = connect.ContinueWith(
            completed => _ = completed.Exception,
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
