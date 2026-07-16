using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NexusMonach.Views;

public partial class SshTerminalDockControl : UserControl
{
    private static readonly Regex SafeTarget = new(
        @"^(?:[A-Za-z0-9._-]+@)?(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\])$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.CultureInvariant);
    private readonly DispatcherTimer _sessionTimer;
    private Process? _process;
    private DateTime _connectedAtUtc;

    public event EventHandler? CloseRequested;

    public SshTerminalDockControl()
    {
        InitializeComponent();
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionTimer.Tick += (_, _) => UpdateSessionTime();
    }

    public void FocusTarget()
    {
        TargetBox.Focus();
        TargetBox.SelectAll();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_process is { HasExited: false }) { Disconnect(); return; }
        var target = TargetBox.Text.Trim();
        if (!SafeTarget.IsMatch(target) || target.StartsWith("-", StringComparison.Ordinal))
        {
            Append("[Nexus] Укажите адрес в формате user@host без пробелов.\n");
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Append("[Nexus] Порт SSH должен быть числом от 1 до 65535.\n");
            return;
        }
        var ssh = FindSshExecutable();
        if (ssh is null)
        {
            Append("[Nexus] Не найден Windows OpenSSH Client. Установите компонент «OpenSSH Client» в дополнительных компонентах Windows.\n");
            return;
        }

        var start = new ProcessStartInfo(ssh)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "-tt", "-p", port.ToString(), "-o", "ServerAliveInterval=20",
                     "-o", "ServerAliveCountMax=3", target
                 })
            start.ArgumentList.Add(argument);

        try
        {
            OutputBox.Clear();
            Append($"[Nexus] SSH {target} · порт {port}\n");
            Append("[Nexus] Браузер не выполняет команды автоматически.\n\n");
            _process = Process.Start(start) ?? throw new InvalidOperationException("ssh.exe не запустился.");
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += Process_OutputDataReceived;
            _process.ErrorDataReceived += Process_OutputDataReceived;
            _process.Exited += Process_Exited;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _connectedAtUtc = DateTime.UtcNow;
            _sessionTimer.Start();
            SetConnected(true, target, port);
            await Task.Yield();
            CommandBox.Focus();
        }
        catch (Exception ex)
        {
            Append("[Nexus] Ошибка подключения: " + ex.Message + "\n");
            Disconnect();
        }
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        _ = Dispatcher.InvokeAsync(() => Append(Ansi.Replace(e.Data, string.Empty) + "\n"));
    }

    private void Process_Exited(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            Append("\n[Nexus] SSH-сессия завершена.\n");
            SetConnected(false, null, ParsePort());
            DisposeProcess();
        });

    private void Send_Click(object sender, RoutedEventArgs e) => SendCommand();

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendCommand();
    }

    private void SendCommand()
    {
        var command = CommandBox.Text;
        if (string.IsNullOrWhiteSpace(command) || _process is not { HasExited: false }) return;
        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
            CommandBox.Clear();
        }
        catch (Exception ex) { Append("[Nexus] Команда не отправлена: " + ex.Message + "\n"); }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

    public void Disconnect()
    {
        _sessionTimer.Stop();
        var process = _process;
        if (process is { HasExited: false })
        {
            try { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); } catch { }
            try { if (!process.WaitForExit(700)) process.Kill(entireProcessTree: true); } catch { }
        }
        DisposeProcess();
        SetConnected(false, null, ParsePort());
    }

    private void DisposeProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        process.OutputDataReceived -= Process_OutputDataReceived;
        process.ErrorDataReceived -= Process_OutputDataReceived;
        process.Exited -= Process_Exited;
        process.Dispose();
    }

    private void SetConnected(bool connected, string? target, int port)
    {
        TargetBox.IsEnabled = !connected;
        PortBox.IsEnabled = !connected;
        ConnectButton.Content = connected ? "Подключено" : "Подключить";
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        CommandBox.IsEnabled = connected;
        SendButton.IsEnabled = connected;
        ConnectionText.Text = connected ? $"{target} · порт {port}" : $"Не подключено · порт {port}";
        if (!connected) _sessionTimer.Stop();
        UpdateSessionTime();
    }

    private void UpdateSessionTime()
    {
        var elapsed = _process is { HasExited: false } ? DateTime.UtcNow - _connectedAtUtc : TimeSpan.Zero;
        SessionTimeText.Text = $"СЕССИЯ {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private int ParsePort() => int.TryParse(PortBox.Text, out var port) && port is >= 1 and <= 65535 ? port : 22;

    private void Append(string value)
    {
        OutputBox.AppendText(value);
        OutputBox.ScrollToEnd();
    }

    private static string? FindSshExecutable()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Path.Combine(windows, "System32", "OpenSSH", "ssh.exe");
        if (File.Exists(system)) return system;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "ssh.exe")).FirstOrDefault(File.Exists);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Control_Unloaded(object sender, RoutedEventArgs e) => Disconnect();
}
