using System.Diagnostics;
using System.Net.Sockets;

namespace NexusMonach.Services.Tor;

public enum TorState { Stopped, Bootstrapping, Connected, Failed }

/// <summary>
/// Обнаружение и управление Tor. Использует готовый torrc пользователя,
/// где настроены мосты и транспорты для обхода DPI.
/// </summary>
public static class TorService
{
    public const int SocksPort = 9051;
    private static Process? _process;
    private static readonly object Gate = new();

    public static bool IsRunning => Probe(9051) || Probe(9050);
    public static bool IsManaged { get; private set; }

    /// <summary>
    /// Запускает tor.exe. Явный torrcPath — конфиг, сгенерированный браузером
    /// (мосты + релей); без него используется torrc пользователя.
    /// </summary>
    public static async Task<TorState> EnsureRunningAsync(
        string? torrcPath = null, CancellationToken ct = default)
    {
        if (IsRunning) return TorState.Connected;
        var exe = FindTor();
        if (exe is null) return TorState.Failed;
        var dir = Path.GetDirectoryName(exe)!;

        Process process;
        lock (Gate)
        {
            if (_process is { HasExited: false }) return TorState.Connected;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(torrcPath ?? Path.Combine(dir, "torrc"));
            process = Process.Start(psi) ?? throw new InvalidOperationException("Tor не запустился.");
            _process = process;
            IsManaged = true;
        }

        // Ждём готовности SOCKS5.
        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return TorState.Failed;
            if (Probe(SocksPort)) return TorState.Connected;
            await Task.Delay(1500, ct);
        }
        return TorState.Bootstrapping;
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_process is { HasExited: false })
                try { _process.Kill(true); } catch { }
            _process = null;
            IsManaged = false;
        }
    }

    /// <summary>SOCKS5-хендшейк: отправляем 05 01 00, ждём 05 XX.</summary>
    internal static bool Probe(int port)
    {
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync("127.0.0.1", port);
            if (!connect.Wait(2000))
            {
                PortProbeObserver.Observe(connect);
                return false;
            }
            c.GetStream().Write(new byte[] { 5, 1, 0 }, 0, 3);
            var r = new byte[2];
            c.GetStream().Read(r, 0, 2);
            return r[0] == 5;
        }
        catch { return false; }
    }

    private static string? FindTor() =>
        File.Exists(@"C:\Tor\tor.exe") ? @"C:\Tor\tor.exe" :
        File.Exists(Path.Combine(AppContext.BaseDirectory, "tor", "tor.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "tor", "tor.exe") : null;
}
