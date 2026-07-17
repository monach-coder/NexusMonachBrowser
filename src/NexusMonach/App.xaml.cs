using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        AppPaths.Initialize(e.Args);
        DevToolsAiBridgeService.Start();
        NexusFabricRuntime.Initialize();

        var splash = new SplashWindow();
        splash.Show();
        var startupAudio = StartupSoundService.PlayAsync();

        try
        {
            await SettingsService.InitializeAsync();
            await BrowserEnvironment.InitializeAsync();
            await startupAudio;

            var mainWindow = new MainWindow(isPrivate: false);
            MainWindow = mainWindow;
            mainWindow.Opacity = 0;
            mainWindow.Show();
            await mainWindow.InitializeAsync(waitForFirstPage: true);
            mainWindow.Opacity = 1;
            mainWindow.Activate();
            WhisperService.PrepareInBackground();
            TranslationService.WarmUpInBackground();
            LocalAiService.WarmUpInBackground();
        }
        catch (Exception ex)
        {
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
        base.OnExit(e);
    }

}
