using System.Windows;
using NexusMonach.Services.Tor;

namespace NexusMonach.Views;

public partial class NetworkWatchdogWindow : Window
{
    private readonly NetworkWatchdog _watchdog;
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;

    public NetworkWatchdogWindow(NetworkWatchdog watchdog)
    {
        InitializeComponent();
        _watchdog = watchdog;
        _watchdog.ThreatDetected += OnThreat;
        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += (_, _) => RefreshStats();
        _uiTimer.Start();
        RefreshStats();
    }

    private void OnThreat(ThreatEvent threat)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ThreatLog.Items.Insert(0, threat);
            if (ThreatLog.Items.Count > 200)
                ThreatLog.Items.RemoveAt(ThreatLog.Items.Count - 1);
        });
    }

    private void RefreshStats()
    {
        ThreatCount.Text = _watchdog.Threats.Count.ToString();
        BlockedCount.Text = _watchdog.BlockedSources.Count.ToString();
        HoneypotCount.Text = "7";
        WatchdogStatus.Text = "Активен";
    }

    private void UnblockAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var source in _watchdog.BlockedSources.Keys.ToList())
            _watchdog.Unblock(source);
        RefreshStats();
    }

    /// <summary>
    /// Сканирует порты этой машины через IP Helper API (без прав админа),
    /// заполняет таблицу и озвучивает итог голосом помощника.
    /// </summary>
    private void ScanPorts_Click(object sender, RoutedEventArgs e)
    {
        ScanPortsButton.IsEnabled = false;
        ScanPortsButton.Content = "Сканирование…";
        ScanSummary.Text = "Опрашиваю таблицу слушателей TCP/UDP…";
        try
        {
            var results = Services.LocalPortScanner.Scan();
            PortResults.ItemsSource = results;
            PortResults.Visibility = Visibility.Visible;

            var dangerous = results.Count(r => r.Severity == 2);
            var warnings = results.Count(r => r.Severity == 1);
            ScanSummary.Text =
                $"Готово: слушателей — {results.Count}, опасных — {dangerous}, " +
                $"требуют внимания — {warnings}. Красное стоит закрыть или привязать к 127.0.0.1.";

            // Голосовой итог — фирменная озвучка уведомлений браузера.
            var spoken = dangerous > 0
                ? $"Сканирование завершено. Обнаружено опасных портов: {dangerous}. " +
                  "Рекомендую закрыть их или проверить настройки удалённого доступа."
                : $"Сканирование завершено. Открытых слушателей: {results.Count}. Опасных портов не найдено.";
            Services.VoiceAssistantService.Announce(spoken,
                Services.VoiceAnnouncementPriority.Important);
        }
        catch (Exception ex)
        {
            ScanSummary.Text = "Не удалось просканировать порты: " + ex.Message;
            Services.VoiceAssistantService.Announce(
                "Не удалось просканировать порты.", Services.VoiceAnnouncementPriority.Important);
        }
        finally
        {
            ScanPortsButton.IsEnabled = true;
            ScanPortsButton.Content = "Сканировать мои порты";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        Close();
    }
}
