using System.Diagnostics;
using System.Windows;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class SettingsWindow : Window
{
    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
    private sealed record SearchChoice(string Label, SearchEngineKind Value, string Description)
    {
        public override string ToString() => Label;
    }

    private readonly BrowserSettings _settings;
    public BrowserSettings? ResultSettings { get; private set; }

    public SettingsWindow(BrowserSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        var searchChoices = new[]
        {
            new SearchChoice("DuckDuckGo", SearchEngineKind.DuckDuckGo,
                "Релевантность 4/5 · объём 4/5 · фильтрация средняя. Универсальная приватная отправная точка без персонального профиля."),
            new SearchChoice("Brave Search", SearchEngineKind.Brave,
                "Релевантность 4/5 · объём 4/5 · фильтрация ниже средней. Собственный индекс и хороший баланс независимости и покрытия."),
            new SearchChoice("Startpage", SearchEngineKind.Startpage,
                "Релевантность 4/5 · объём 5/5 · фильтрация средняя. Прокси-доступ к крупной выдаче без прямой передачи поиску профиля Nexus."),
            new SearchChoice("Google", SearchEngineKind.Google,
                "Релевантность 5/5 · объём 5/5 · фильтрация/персонализация высокая. Максимальное покрытие, но больше региональных и профильных факторов."),
            new SearchChoice("Яндекс", SearchEngineKind.Yandex,
                "Релевантность 4/5 для русскоязычного веба · объём 5/5 · региональная фильтрация высокая."),
            new SearchChoice("Bing", SearchEngineKind.Bing,
                "Релевантность 4/5 · объём 5/5 · фильтрация средне-высокая. Полезен как крупный альтернативный индекс."),
            new SearchChoice("Mojeek", SearchEngineKind.Mojeek,
                "Релевантность 3/5 · объём 3/5 · фильтрация низкая. Независимый индекс без персонального ранжирования; результаты могут заметно отличаться.")
        };
        var privacyChoices = new[]
        {
            new Choice<PrivacyLevel>("Базовая — максимум совместимости", PrivacyLevel.Basic),
            new Choice<PrivacyLevel>("Сбалансированная — рекомендуется", PrivacyLevel.Balanced),
            new Choice<PrivacyLevel>("Строгая — максимум блокировки", PrivacyLevel.Strict)
        };
        var proxyChoices = new[]
        {
            new Choice<ProxyKind>("SOCKS5 — подходит для Tor и локальных туннелей", ProxyKind.Socks5),
            new Choice<ProxyKind>("HTTP / HTTPS proxy", ProxyKind.Http)
        };
        SearchEngineCombo.ItemsSource = searchChoices;
        PrivacyLevelCombo.ItemsSource = privacyChoices;
        ProxyTypeCombo.ItemsSource = proxyChoices;
        SearchEngineCombo.SelectedItem = searchChoices.FirstOrDefault(x => x.Value == settings.SearchEngine) ?? searchChoices[0];
        PrivacyLevelCombo.SelectedItem = privacyChoices.First(x => x.Value == settings.PrivacyLevel);
        ProxyTypeCombo.SelectedItem = proxyChoices.First(x => x.Value == settings.ProxyKind);
        HomePageBox.Text = settings.HomePage;
        DntCheck.IsChecked = settings.SendDoNotTrack;
        GpcCheck.IsChecked = settings.SendGlobalPrivacyControl;
        StripParametersCheck.IsChecked = settings.StripTrackingParameters;
        BlockNotificationsCheck.IsChecked = settings.BlockNotifications;
        KnowledgeGraphCheck.IsChecked = settings.BuildKnowledgeGraph;
        RestoreSessionCheck.IsChecked = settings.RestoreSession;
        ClearOnExitCheck.IsChecked = settings.ClearBrowsingDataOnExit;
        MemorySaverCheck.IsChecked = settings.MemorySaver;
        ExtensionsCheck.IsChecked = settings.EnableExtensions;
        PasswordCheck.IsChecked = settings.EnablePasswordAutosave;
        AutofillCheck.IsChecked = settings.EnableGeneralAutofill;
        CustomProxyCheck.IsChecked = settings.EnableCustomProxy;
        ProxyHostBox.Text = settings.ProxyHost;
        ProxyPortBox.Text = settings.ProxyPort.ToString();
        ProxyBypassBox.Text = settings.ProxyBypassList;
        PrivacyMonitorCheck.IsChecked = settings.ShowPrivacyMonitor;
        PreventWebRtcLeakCheck.IsChecked = settings.PreventWebRtcIpLeak;
    }

    private void SearchEngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SearchEngineDescriptionText is null) return;
        SearchEngineDescriptionText.Text = SearchEngineCombo.SelectedItem is SearchChoice choice
            ? choice.Description + " Оценки ориентировочные: Nexus использует эту систему только для стартовых ссылок, затем читает и ранжирует источники локально."
            : string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var proxyEnabled = CustomProxyCheck.IsChecked == true;
        if (!int.TryParse(ProxyPortBox.Text.Trim(), out var proxyPort))
        {
            if (!proxyEnabled) proxyPort = 9050;
            else
            {
            GlassDialogWindow.Show(this, "Порт прокси должен быть целым числом.", "Настройки прокси",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ProxyPortBox.Focus();
            return;
            }
        }
        if (proxyEnabled &&
            !ProxyConfigurationService.TryValidate(ProxyHostBox.Text, proxyPort, out var proxyError))
        {
            GlassDialogWindow.Show(this, proxyError, "Настройки прокси",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ProxyHostBox.Focus();
            return;
        }

        _settings.SearchEngine = SearchEngineCombo.SelectedItem is SearchChoice search
            ? search.Value : SearchEngineKind.DuckDuckGo;
        _settings.PrivacyLevel = PrivacyLevelCombo.SelectedItem is Choice<PrivacyLevel> level
            ? level.Value : PrivacyLevel.Balanced;
        _settings.HomePage = string.IsNullOrWhiteSpace(HomePageBox.Text) ? "app://newtab" : HomePageBox.Text.Trim();
        _settings.SendDoNotTrack = DntCheck.IsChecked == true;
        _settings.SendGlobalPrivacyControl = GpcCheck.IsChecked == true;
        _settings.StripTrackingParameters = StripParametersCheck.IsChecked == true;
        _settings.BlockNotifications = BlockNotificationsCheck.IsChecked == true;
        _settings.BuildKnowledgeGraph = KnowledgeGraphCheck.IsChecked == true;
        _settings.RestoreSession = RestoreSessionCheck.IsChecked == true;
        _settings.ClearBrowsingDataOnExit = ClearOnExitCheck.IsChecked == true;
        _settings.MemorySaver = MemorySaverCheck.IsChecked == true;
        _settings.EnableExtensions = ExtensionsCheck.IsChecked == true;
        _settings.EnablePasswordAutosave = PasswordCheck.IsChecked == true;
        _settings.EnableGeneralAutofill = AutofillCheck.IsChecked == true;
        _settings.EnableDevTools = false;
        _settings.EnableCustomProxy = proxyEnabled;
        _settings.ProxyKind = ProxyTypeCombo.SelectedItem is Choice<ProxyKind> proxy
            ? proxy.Value : ProxyKind.Socks5;
        _settings.ProxyHost = ProxyHostBox.Text.Trim();
        _settings.ProxyPort = proxyPort;
        _settings.ProxyBypassList = ProxyBypassBox.Text.Trim();
        _settings.ShowPrivacyMonitor = PrivacyMonitorCheck.IsChecked == true;
        _settings.PreventWebRtcIpLeak = PreventWebRtcLeakCheck.IsChecked == true;
        ResultSettings = _settings;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenSignInOptions_Click(object sender, RoutedEventArgs e) =>
        OpenWindowsSettings("ms-settings:signinoptions");

    private void OpenFingerprintSetup_Click(object sender, RoutedEventArgs e) =>
        OpenWindowsSettings("ms-settings:signinoptions-launchfingerprintenrollment");

    private void OpenWindowsSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось открыть параметры Windows:\n\n" + ex.Message,
                "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
