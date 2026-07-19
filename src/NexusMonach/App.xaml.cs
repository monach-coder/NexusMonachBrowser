using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        AppPaths.Initialize(e.Args);
        CrashReportService.Initialize();
        CrashReportService.AddBreadcrumb("startup", "app-paths-ready");
        if (!GuardianRuntime.IsSafeMode)
        {
            DevToolsAiBridgeService.Start();
            NexusFabricRuntime.Initialize();
        }

        var splash = new SplashWindow();
        splash.Show();
        var startupAudio = StartupSoundService.PlayAsync();

        try
        {
            await SettingsService.InitializeAsync();
            CrashReportService.AddBreadcrumb("startup", "settings-ready");
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
                    "Nexus Guardian включил безопасный режим после повторных сбоев или изменения некритических файлов. AI и расширения временно отключены.",
                    "Nexus Guardian — безопасный режим", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (!GuardianRuntime.IsSafeMode)
            {
                WhisperService.PrepareInBackground();
                TranslationService.WarmUpInBackground();
                LocalAiService.WarmUpInBackground();
            }
            _ = ProcessCrashQueueAsync(mainWindow);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordFatal(ex, "startup", "startup-failed");
            GlassDialogWindow.Show(
                "Nexus Monach не смог запуститься.\n\n" + ex.Message +
                "\n\nПроверьте наличие актуального Microsoft Edge WebView2 Runtime.",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            splash.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DevToolsAiBridgeService.Stop();
        NexusFabricRuntime.Shutdown();
        SemanticEmbeddingService.Stop();
        WhisperService.Shutdown();
        TranslationService.Stop();
        LocalAiService.Shutdown();
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
            Current.Shutdown();
            return true;
        }
        catch { return false; }
    }

}
