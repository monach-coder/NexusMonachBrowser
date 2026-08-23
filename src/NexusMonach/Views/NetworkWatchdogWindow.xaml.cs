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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        Close();
    }
}
