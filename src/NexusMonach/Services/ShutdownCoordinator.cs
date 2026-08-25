namespace NexusMonach.Services;

/// <summary>
/// Keeps best-effort native and AI cleanup from holding the WPF process open
/// forever. Timed-out cleanup continues only on a background thread and the
/// operating system reclaims the remaining process resources on exit.
/// </summary>
internal static class ShutdownCoordinator
{
    public static bool RunStep(string name, Action action, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var completed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                action();
                completed.TrySetResult(null);
            }
            catch (Exception ex) { completed.TrySetResult(ex); }
        })
        {
            IsBackground = true,
            Name = "Nexus shutdown: " + name
        };
        worker.Start();
        if (!completed.Task.Wait(timeout))
        {
            CrashReportService.AddBreadcrumb("shutdown", name + "-timeout");
            return false;
        }
        var failure = completed.Task.Result;
        if (failure is null) return true;
        CrashReportService.RecordNonFatal("shutdown", name, failure);
        return false;
    }
}
