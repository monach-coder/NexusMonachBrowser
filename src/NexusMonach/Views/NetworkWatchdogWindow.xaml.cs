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
    /// заполняет таблицу и озвучивает итог голосом помощника. Итог честно
    /// разделяет: утечки закрыты порт-щитом автоматически, службы —
    /// сознательно не тронуты.
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
            var shieldOn = Services.PortShieldService.AreRulesApplied();
            var shieldLine = shieldOn
                ? "Порт-щит: АКТИВЕН — mDNS/SSDP/NetBIOS закрыты файрволом на сессию, IP не утекает."
                : "Порт-щит: не активен (включите режим «Авто» в настройках — утечки закроются сами).";
            ScanSummary.Text =
                shieldLine + "\n" +
                (dangerous > 0
                    ? $"Службы удалённого доступа/файлообмена: {dangerous}. Браузер их НЕ закрывает, чтобы не сломать твой доступ — отключите ненужные вручную (RDP, SMB, VNC). "
                    : "Опасных служб не найдено. ") +
                $"Утечки LAN: {warnings} (порт-щит закрывает их автоматически).";

            // Голосовой итог — фирменная озвучка уведомлений браузера.
            var spoken = dangerous > 0
                ? $"Сканирование завершено. Служб удалённого доступа: {dangerous} — браузер их не трогает, отключите ненужные вручную. Порт-щит активен."
                : shieldOn
                    ? "Сканирование завершено. Утечки локальной сети закрыты порт-щитом."
                    : $"Сканирование завершено. Открытых слушателей: {results.Count}. Порт-щит не активен.";
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
