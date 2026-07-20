using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class GuardianCenterWindow : Window
{
    private readonly ObservableCollection<GuardianReportSnapshot> _reports = [];

    public GuardianCenterWindow()
    {
        InitializeComponent();
        ReportsList.ItemsSource = _reports;
        WebView2RuntimeMonitor.StatusChanged += WebView2RuntimeMonitor_StatusChanged;
        Closed += (_, _) => WebView2RuntimeMonitor.StatusChanged -= WebView2RuntimeMonitor_StatusChanged;
        RefreshReports();
        RefreshCoreStatus(WebView2RuntimeMonitor.Check());
    }

    private GuardianReportSnapshot? SelectedReport => ReportsList.SelectedItem as GuardianReportSnapshot;

    private void RefreshReports()
    {
        var selectedPath = SelectedReport?.FilePath;
        _reports.Clear();
        foreach (var report in CrashReportService.GetLocalReports()) _reports.Add(report);

        IntegrityStatusText.Text = DescribeIntegrity(GuardianRuntime.IntegrityStatus);
        SafeModeStatusText.Text = GuardianRuntime.IsSafeMode
            ? "Безопасный режим · программный рендеринг"
            : "Обычный режим";
        SafeModeStatusText.Foreground = GuardianRuntime.IsSafeMode
            ? System.Windows.Media.Brushes.DarkOrange
            : (System.Windows.Media.Brush)FindResource("AccentBrush");
        ReportCountText.Text = $"Рапорты: {_reports.Count} · Следопыт: {SledopytDiagnosticsService.Count}";

        ReportsList.SelectedItem = _reports.FirstOrDefault(x =>
            string.Equals(x.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? _reports.FirstOrDefault();
        if (ReportsList.SelectedItem is null)
            DetailsBox.Text = "Локальных рапортов пока нет.\n\nНажмите «Создать тестовый рапорт», чтобы проверить весь локальный путь Guardian без аварийного завершения браузера.";
    }

    private static string DescribeIntegrity(string status) => status switch
    {
        "verified" => "Проверено · подпись и SHA-256",
        "degraded" => "Изменены некритические файлы",
        "critical-mismatch" => "Нарушена целостность",
        "invalid-signature" => "Недействительная подпись",
        "development-unverified" => "Локальная сборка без подписи",
        "not-launched-by-guardian" => "Запуск выполнен без Guardian",
        _ => "Не проверена в dev-запуске"
    };

    private void ReportsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        DetailsBox.Text = SelectedReport?.Details ?? string.Empty;

    private void CreateTestReport_Click(object sender, RoutedEventArgs e)
    {
        CrashReportService.CreateDiagnosticTestReport();
        RefreshReports();
    }

    private async void FullCheck_Click(object sender, RoutedEventArgs e)
    {
        var guardian = Path.Combine(AppContext.BaseDirectory, "NexusMonach.exe");
        if (!File.Exists(guardian))
        {
            GlassDialogWindow.Show(this,
                "Полная проверка доступна в portable-сборке, запущенной через NexusMonach.exe.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FullCheckButton.IsEnabled = false;
        IntegrityStatusText.Text = "Полная проверка выполняется…";
        try
        {
            var info = new ProcessStartInfo(guardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add("--verify-only");
            info.ArgumentList.Add(AppContext.BaseDirectory);
            info.ArgumentList.Add("--full-integrity-check");
            using var process = Process.Start(info) ??
                throw new InvalidOperationException("Windows не создал процесс полной проверки.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode == 0)
            {
                IntegrityStatusText.Text = "Проверено полностью · SHA-256";
                GlassDialogWindow.Show(this,
                    "Полная проверка завершена: подпись манифеста и SHA-256 всех файлов совпадают.",
                    "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                IntegrityStatusText.Text = "Полная проверка обнаружила изменение";
                var details = string.Join("\n", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (details.Length > 1800) details = details[..1800] + "…";
                GlassDialogWindow.Show(this,
                    "Проверка не пройдена. Не запускайте изменённую сборку и распакуйте официальный архив заново.\n\n" + details,
                    "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            IntegrityStatusText.Text = DescribeIntegrity(GuardianRuntime.IntegrityStatus);
            CrashReportService.RecordNonFatal("guardian", "full-integrity-check", ex);
            GlassDialogWindow.Show(this, "Полная проверка не завершена:\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            FullCheckButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshReports();

    private void SledopytJournal_Click(object sender, RoutedEventArgs e)
    {
        ReportsList.SelectedItem = null;
        DetailsBox.Text = SledopytDiagnosticsService.FormatForDisplay();
        DetailsBox.ScrollToHome();
    }

    private void WebView2RuntimeMonitor_StatusChanged(object? sender, WebView2RuntimeSnapshot snapshot) =>
        Dispatcher.BeginInvoke(new Action(() => RefreshCoreStatus(snapshot)));

    private void RefreshCoreStatus(WebView2RuntimeSnapshot snapshot)
    {
        CoreStatusText.Text = snapshot.State switch
        {
            WebView2RuntimeState.Current => "Ядро работает штатно",
            WebView2RuntimeState.RestartRequired => "Доступно после перезапуска",
            WebView2RuntimeState.Missing => "Ядро не найдено",
            _ => "Статус не определён"
        };
        CoreStatusText.Foreground = snapshot.State switch
        {
            WebView2RuntimeState.RestartRequired => System.Windows.Media.Brushes.DarkOrange,
            WebView2RuntimeState.Missing => System.Windows.Media.Brushes.IndianRed,
            WebView2RuntimeState.Unknown => System.Windows.Media.Brushes.DarkOrange,
            _ => (System.Windows.Media.Brush)FindResource("AccentBrush")
        };
        CoreActiveVersionText.Text = snapshot.ActiveVersion;
        CoreInstalledVersionText.Text = snapshot.InstalledVersion;
        CoreLastCheckText.Text = "Проверено: " + snapshot.CheckedAt.ToString("dd.MM.yyyy HH:mm:ss");
        CoreStatusText.ToolTip = snapshot.Message + $"\nSDK: {snapshot.SdkVersion}";
        CheckCoreButton.Content = snapshot.State == WebView2RuntimeState.RestartRequired
            ? "Перезапустить Nexus"
            : "Проверить ядро";
    }

    private void CheckCore_Click(object sender, RoutedEventArgs e)
    {
        CheckCoreButton.IsEnabled = false;
        CoreStatusText.Text = "Проверка локального ядра…";
        try
        {
            var snapshot = WebView2RuntimeMonitor.Check();
            RefreshCoreStatus(snapshot);
            var restartReady = snapshot.State == WebView2RuntimeState.RestartRequired;
            var answer = GlassDialogWindow.Show(this,
                snapshot.Message + $"\n\nАктивная версия: {snapshot.ActiveVersion}" +
                $"\nУстановленная версия: {snapshot.InstalledVersion}" +
                $"\nВерсия SDK: {snapshot.SdkVersion}" +
                (restartReady
                    ? "\n\nПерезапустить Nexus сейчас? Вкладки и допустимые непарольные поля " +
                      "будут локально зашифрованы средствами Windows."
                    : "\n\nGuardian ничего не скачивает и не устанавливает."),
                "Nexus Guardian · ядро WebView2",
                restartReady ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                snapshot.State is WebView2RuntimeState.Missing or WebView2RuntimeState.Unknown
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Information);
            if (restartReady && answer == MessageBoxResult.Yes)
            {
                if (Owner is MainWindow mainWindow)
                {
                    Close();
                    mainWindow.RequestSecureRestart();
                }
                else
                {
                    GlassDialogWindow.Show(this,
                        "Закройте все окна Nexus Monach и запустите браузер снова, чтобы применить новое ядро.",
                        "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("guardian", "webview2-runtime-check", ex);
            GlassDialogWindow.Show(this, "Проверка ядра не завершена:\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CheckCoreButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReport is null) return;
        try { Clipboard.SetText(SelectedReport.Details); }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось скопировать рапорт:\n\n" + ex.Message, "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReport is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт локального рапорта Nexus Guardian",
            FileName = SelectedReport.FileName.Replace(".pending", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(".sent", string.Empty, StringComparison.OrdinalIgnoreCase),
            DefaultExt = ".json",
            Filter = "Guardian report (*.json)|*.json|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!CrashReportService.ExportLocalReport(SelectedReport.FilePath, dialog.FileName))
            GlassDialogWindow.Show(this, "Не удалось экспортировать выбранный рапорт.", "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CrashReportService.VaultPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CrashReportService.VaultPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось открыть Crash Vault:\n\n" + ex.Message, "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReport is null) return;
        var answer = GlassDialogWindow.Show(this,
            "Удалить выбранный локальный рапорт? Восстановить его после удаления нельзя.",
            "Nexus Guardian", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (!CrashReportService.DeleteLocalReport(SelectedReport.FilePath))
            GlassDialogWindow.Show(this, "Не удалось удалить выбранный рапорт.", "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshReports();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
