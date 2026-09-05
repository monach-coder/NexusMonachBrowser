using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using NexusMonach.Services;
using NexusMonach.Services.Diagnostics;

namespace NexusMonach.Views;

public partial class GuardianCenterWindow : Window
{
    private readonly ObservableCollection<GuardianReportSnapshot> _reports = [];
    private bool _showingSledopytJournal;

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

    private static string StartupHealthReportsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusMonach", "Guardian", "Reports");

    private static string? LatestStartupHealthReport()
    {
        return Directory.Exists(StartupHealthReportsRoot)
            ? Directory.GetFiles(StartupHealthReportsRoot, "startup-health-*.json")
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Карточка «Самодиагностика старта»: вердикт и замечания последнего
    /// запуска из отчёта Guardian (плюс браузерные проверки, если браузер
    /// успел их дописать). env-сводка GuardianRuntime показывает вердикт
    /// текущей сессии, файл — последней.
    /// </summary>
    private void RefreshSelfTestStatus()
    {
        string verdict;
        string detail;
        var brush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        try
        {
            var report = LatestStartupHealthReport();
            if (report is null)
            {
                verdict = "Отчётов самодиагностики пока нет";
                detail = "Появится после первого запуска через NexusMonach.exe версии 2.9.54+.";
            }
            else
            {
                using var document = JsonDocument.Parse(File.ReadAllText(report));
                var root = document.RootElement;
                var problems = new List<string>();
                foreach (var section in new[] { "checks", "browserChecks" })
                {
                    if (!root.TryGetProperty(section, out var checks)) continue;
                    foreach (var check in checks.EnumerateArray())
                    {
                        var status = check.TryGetProperty("status", out var statusValue)
                            ? statusValue.GetString() : "ok";
                        if (status is null or "ok") continue;
                        var id = check.TryGetProperty("id", out var idValue) ? idValue.GetString() : "?";
                        problems.Add($"{id}: {status}");
                    }
                }

                var fileVerdict = root.TryGetProperty("verdict", out var verdictValue)
                    ? verdictValue.GetString() : null;
                verdict = fileVerdict switch
                {
                    "ok" => "Все механизмы в норме",
                    "warn" => $"Замечания: {problems.Count}",
                    "fail" => $"Сбои: {problems.Count}",
                    _ => "Вердикт неизвестен"
                };
                detail = problems.Count > 0
                    ? string.Join("; ", problems) + $" · отчёт {Path.GetFileName(report)}"
                    : "Проблем не найдено · " + Path.GetFileName(report);
                if (fileVerdict == "fail") brush = System.Windows.Media.Brushes.IndianRed;
                else if (problems.Count > 0) brush = System.Windows.Media.Brushes.DarkOrange;
            }

            // Вердикт текущей сессии отличается от последнего отчёта на диске —
            // не прячем: пользователь должен видеть обе картины.
            var current = GuardianRuntime.StartupHealth;
            if (current is not ("ok" or "unknown") && !verdict.Contains(current, StringComparison.Ordinal))
                detail += $" · эта сессия: {current}";
        }
        catch (Exception ex)
        {
            verdict = "Отчёт не читается";
            detail = ex.Message;
            brush = System.Windows.Media.Brushes.DarkOrange;
        }
        SelfTestStatusText.Text = verdict;
        SelfTestStatusText.Foreground = brush;
        SelfTestDetailText.Text = detail;
    }

    /// <summary>
    /// Показ полного отчёта самодиагностики в панели деталей. Нарочно без
    /// запуска внешних процессов: путь до отчёта не должен попадать в
    /// аргументы командной строки ни в каком виде.
    /// </summary>
    private void SelfTestOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = LatestStartupHealthReport();
            if (report is null)
            {
                GlassDialogWindow.Show(this,
                    "Отчётов самодиагностики пока нет: они появляются при каждом запуске через NexusMonach.exe (2.9.54+).",
                    "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _showingSledopytJournal = false;
            ReportsList.SelectedItem = null;
            DetailsBox.Text = "ОТЧЁТ САМОДИАГНОСТИКИ · " + report + Environment.NewLine +
                              new string('─', 60) + Environment.NewLine +
                              File.ReadAllText(report);
            DetailsBox.ScrollToHome();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("guardian", "self-test-open", ex);
            GlassDialogWindow.Show(this, "Не удалось прочитать отчёт:\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
            DetailsBox.Text = "Локальных рапоротов пока нет.\n\nНажмите «Создать тестовый рапорт», чтобы проверить весь локальный путь Guardian без аварийного завершения браузера.";
        RefreshSelfTestStatus();
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

    private void ReportsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedReport is not null) _showingSledopytJournal = false;
        DetailsBox.Text = SelectedReport?.Details ??
                          (_showingSledopytJournal ? SledopytDiagnosticsService.FormatForDisplay() : string.Empty);
    }

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
        _showingSledopytJournal = true;
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
        var text = _showingSledopytJournal
            ? SledopytDiagnosticsService.FormatForDisplay()
            : SelectedReport?.Details;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось скопировать рапорт:\n\n" + ex.Message, "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Открывает причинный граф рапорта как вкладку с 3D-визуализацией:
    /// вращение мышью, клик по узлам, ползунок времени проигрывает каскад
    /// отказа. Без хозяина-браузера предлагает автономный HTML-файл.
    /// </summary>
    private void ShowGraph3d_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReport is null || _showingSledopytJournal) return;
        try
        {
            using var document = JsonDocument.Parse(SelectedReport.Json);
            if (!document.RootElement.TryGetProperty("CausalGraph", out var graphElement) ||
                graphElement.ValueKind == JsonValueKind.Null)
            {
                GlassDialogWindow.Show(this,
                    "Этот рапорт записан до появления причинных графов — данных для 3D-визуализации нет.\n\n" +
                    "Все новые рапорты содержат граф автоматически.",
                    "Nexus Guardian · 3D-граф", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var graph = graphElement.Deserialize<CausalGraph>();
            if (graph is null || graph.Nodes.Count == 0)
            {
                GlassDialogWindow.Show(this, "Граф этого рапорта пуст.",
                    "Nexus Guardian · 3D-граф", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Owner is MainWindow mainWindow)
            {
                Close();
                mainWindow.AddTab(CausalGraphExporter.ToInternalTabUrl(graph), insertAfterActive: true);
            }
            else
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Экспорт интерактивного 3D-графа отказа",
                    FileName = "nexus-crash-graph-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html",
                    DefaultExt = ".html",
                    Filter = "Интерактивный отчёт (*.html)|*.html|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(dialog.FileName, CausalGraphExporter.ToInteractiveHtml(graph));
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("guardian", "graph3d-open", ex);
            GlassDialogWindow.Show(this, "Не удалось открыть 3D-граф:\n\n" + ex.Message,
                "Nexus Guardian · 3D-граф", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_showingSledopytJournal)
        {
            var journalDialog = new SaveFileDialog
            {
                Title = "Экспорт полного рапорта Nexus Следопыт",
                FileName = $"nexus-sledopyt-report-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExt = ".json",
                Filter = "Sledopyt report (*.json)|*.json|Все файлы (*.*)|*.*"
            };
            if (journalDialog.ShowDialog(this) != true) return;
            if (!SledopytDiagnosticsService.Export(journalDialog.FileName))
                GlassDialogWindow.Show(this, "Не удалось экспортировать рапорт Следопыта.", "Nexus Guardian",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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

    private void SendEmail_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReport is null)
        {
            GlassDialogWindow.Show(this, "Сначала выберите локальный рапорт.", "Nexus Guardian",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!CrashReportMailService.IsEncryptionReady)
        {
            GlassDialogWindow.Show(this,
                "В этой сборке отсутствует открытый ключ шифрования почтовых рапортов.\n\n" +
                "Разработчику необходимо выполнить scripts/New-CrashReportKey.ps1 и пересобрать Nexus Monach.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = GlassDialogWindow.Show(this,
            "Guardian создаст отдельную зашифрованную копию выбранного рапорта и откроет почтовый клиент.\n\n" +
            $"Получатель: {CrashReportMailService.Recipient}\n" +
            "Письмо не отправляется автоматически: проверьте его и подтвердите отправку самостоятельно.",
            "Nexus Guardian · отправка рапорта", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var result = CrashReportMailService.Compose(SelectedReport,
                new WindowInteropHelper(this).EnsureHandle());
            GlassDialogWindow.Show(this,
                result.Message + $"\n\nЗашифрованный файл:\n{result.AttachmentPath}",
                "Nexus Guardian · почта", MessageBoxButton.OK,
                result.Opened ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("guardian-mail", "compose", ex);
            GlassDialogWindow.Show(this,
                "Не удалось подготовить письмо. Исходный локальный рапорт не изменён.\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _showingSledopytJournal
                ? Path.GetDirectoryName(AppPaths.SledopytDiagnosticsFile)!
                : CrashReportService.VaultPath;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
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
