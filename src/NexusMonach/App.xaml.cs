using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using NexusMonach.Services;
using NexusMonach.Views;

namespace NexusMonach;

public partial class App : Application
{
    private void GlassTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is Button) return;
        var window = Window.GetWindow(sender as DependencyObject);
        if (window is null) return;
        if (e.ClickCount == 2 && window.ResizeMode != ResizeMode.NoResize)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            window.DragMove();
    }

    private void GlassClose_Click(object sender, RoutedEventArgs e) =>
        Window.GetWindow(sender as DependencyObject)?.Close();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (RedirectPortableLaunchToGuardian(e.Args)) return;
        // Unattended readiness probe: initialize everything, show the real
        // window, then exit with code 0. CI uses it to catch startup crashes
        // that unit tests cannot see; interactive flows stay untouched.
        var smokeSelfTest = e.Args.Any(x => x.Equals("--smoke-self-test", StringComparison.OrdinalIgnoreCase));
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        AppPaths.Initialize(e.Args);
        CrashReportService.Initialize();
        CrashReportService.AddBreadcrumb("startup", "app-paths-ready");
        if (GuardianRuntime.IsSafeMode)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            CrashReportService.AddBreadcrumb("guardian", "software-rendering-enabled");
        }
        if (!GuardianRuntime.IsSafeMode)
        {
            NexusFabricRuntime.Initialize();
        }

        var splash = new SplashWindow();
        splash.Show();
        Task startupAudio = Task.CompletedTask;

        try
        {
            await SettingsService.InitializeAsync();
            if (!SettingsService.Current.ThemeSelectionCompleted)
            {
                if (smokeSelfTest)
                {
                    var smokeSettings = SettingsService.Current.Clone();
                    smokeSettings.ThemeSelectionCompleted = true;
                    await SettingsService.SaveAsync(smokeSettings);
                }
                else
                {
                    splash.Hide();
                    var themePicker = new ThemeSelectionWindow(
                        SettingsService.Current.Theme, SettingsService.Current.ThemeMode);
                    themePicker.ShowDialog();
                    var firstRunSettings = SettingsService.Current.Clone();
                    firstRunSettings.Theme = themePicker.ResultTheme;
                    firstRunSettings.ThemeMode = themePicker.ResultMode;
                    firstRunSettings.ThemeSelectionCompleted = true;
                    await SettingsService.SaveAsync(firstRunSettings);
                    splash.Show();
                }
            }
            ThemeService.Apply(SettingsService.Current.Theme, SettingsService.Current.ThemeMode);
            CrashReportService.AddBreadcrumb("startup", "settings-ready");
            if (!smokeSelfTest &&
                SettingsService.Current.VoiceSpeakAtStartup &&
                SettingsService.Current.VoiceAssistantMode != Models.VoiceAssistantMode.Off)
                startupAudio = StartupSoundService.PlayAsync();
            if (e.Args.Any(x => x.Equals("--guardian-test-crash", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Intentional Nexus Guardian crash-pipeline test.");
            await BrowserEnvironment.InitializeAsync();
            CrashReportService.AddBreadcrumb("startup", "webview2-ready");
            await startupAudio;

            var mainWindow = new MainWindow(isPrivate: false);
            MainWindow = mainWindow;
            mainWindow.Opacity = 0;
            mainWindow.Show();
            await mainWindow.InitializeAsync(waitForFirstPage: true);
            mainWindow.Opacity = 1;
            mainWindow.Activate();
            CrashReportService.AddBreadcrumb("startup", "main-window-ready");
            if (GuardianRuntime.IsSafeMode)
            {
                GlassDialogWindow.Show(mainWindow,
                    "Nexus Guardian включил безопасный режим после повторных сбоев, графической ошибки или изменения некритических файлов. AI, расширения и аппаратное ускорение временно отключены.",
                    "Nexus Guardian — безопасный режим", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            VoiceAssistantService.Initialize();
            if (!smokeSelfTest)
            {
                VoiceAssistantService.Announce(
                    GuardianRuntime.IsSafeMode
                        ? "Nexus запущен в безопасном режиме."
                        : GuardianRuntime.IntegrityStatus.Equals("verified", StringComparison.OrdinalIgnoreCase)
                            ? "Nexus готов. Целостность браузера подтверждена."
                            : "Nexus готов к работе.",
                    GuardianRuntime.IsSafeMode
                        ? VoiceAnnouncementPriority.Critical
                        : VoiceAnnouncementPriority.Important);
                if (!GuardianRuntime.IsSafeMode)
                {
                    WhisperService.PrepareInBackground();
                    TranslationService.WarmUpInBackground();
                    LocalAiService.WarmUpInBackground();
                    _ = Task.Run(RussianStressDictionary.WarmUp);
                }
            }
            if (smokeSelfTest)
            {
                CrashReportService.AddBreadcrumb("startup", "smoke-self-test-ready");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    await Dispatcher.InvokeAsync(() => Shutdown(0));
                });
            }
            _ = ProcessCrashQueueAsync(mainWindow);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordFatal(ex, "startup", "startup-failed");
            var runtimeSnapshot = WebView2RuntimeMonitor.Check();
            GlassDialogWindow.Show(
                WebView2RuntimeMonitor.FormatStartupFailure(ex, runtimeSnapshot),
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            // A fatal error can happen while WPF is still inside its asynchronous
            // startup continuation. Shutdown() is not reliable in that state: the
            // dispatcher may survive with no windows and keep Guardian waiting.
            // The report above is persisted synchronously, so a hard process exit
            // is safe and guarantees that the taskbar icon disappears.
            try { splash.Close(); } catch { }
            Environment.Exit(-1);
        }
        finally
        {
            if (splash.IsLoaded)
                splash.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var timeout = TimeSpan.FromSeconds(2);
        ShutdownCoordinator.RunStep("fabric", NexusFabricRuntime.Shutdown, timeout);
        ShutdownCoordinator.RunStep("semantics", SemanticEmbeddingService.Stop, timeout);
        ShutdownCoordinator.RunStep("whisper", WhisperService.Shutdown, timeout);
        ShutdownCoordinator.RunStep("translation", TranslationService.Stop, timeout);
        ShutdownCoordinator.RunStep("local-ai", LocalAiService.Shutdown, timeout);
        ShutdownCoordinator.RunStep("video-voice", VideoDubbingVoiceService.Shutdown, timeout);
        ShutdownCoordinator.RunStep("assistant-voice", VoiceAssistantService.Shutdown, timeout);
        CrashReportService.MarkCleanExit();
        base.OnExit(e);
    }

    private static async Task ProcessCrashQueueAsync(Window owner)
    {
        try
        {
            if (CrashReportService.PendingCount == 0) return;
            if (SettingsService.Current.CrashReportMode == Models.CrashReportMode.AutomaticAnonymous)
            {
                await CrashReportService.SendPendingAsync(userApproved: true);
                return;
            }

            if (SettingsService.Current.CrashReportMode != Models.CrashReportMode.AskBeforeSending ||
                !CrashReportService.IsDeliveryConfigured) return;

            var answer = GlassDialogWindow.Show(owner,
                $"Nexus Guardian сохранил технических отчётов: {CrashReportService.PendingCount}.\n\n" +
                "Отчёты очищены от URL, истории, содержимого страниц, cookies, токенов и введённых данных. Отправить их разработчику?",
                "Nexus Guardian — Crash Vault", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                await CrashReportService.SendPendingAsync(userApproved: true);
        }
        catch { /* Ошибка доставки никогда не мешает запуску браузера. */ }
    }

    private static bool RedirectPortableLaunchToGuardian(IEnumerable<string> args)
    {
        if (GuardianRuntime.IsGuardianLaunch || !File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")))
            return false;
        var guardian = Path.Combine(AppContext.BaseDirectory, "NexusMonach.exe");
        if (!File.Exists(guardian)) return false;
        try
        {
            var info = new ProcessStartInfo(guardian) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            Process.Start(info);
            // This process is only a portable redirect stub. With no WPF window
            // ever opened, OnLastWindowClose cannot terminate its dispatcher.
            Environment.Exit(0);
            return true;
        }
        catch { return false; }
    }

}
