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
        var crashChoices = new[]
        {
            new Choice<CrashReportMode>("Хранить только локально — рекомендуется сейчас", CrashReportMode.LocalOnly),
            new Choice<CrashReportMode>("Спрашивать перед отправкой", CrashReportMode.AskBeforeSending),
            new Choice<CrashReportMode>("Отправлять автоматически и анонимно", CrashReportMode.AutomaticAnonymous),
        };
        var crashDestinationChoices = new[]
        {
            new Choice<CrashReportDestination>("HTTPS-приёмник", CrashReportDestination.HttpsCollector),
            new Choice<CrashReportDestination>("Напрямую в Matrix", CrashReportDestination.MatrixDirect)
        };
        SearchEngineCombo.ItemsSource = searchChoices;
        PrivacyLevelCombo.ItemsSource = privacyChoices;
        ProxyTypeCombo.ItemsSource = proxyChoices;
        CrashReportModeCombo.ItemsSource = crashChoices;
        CrashReportDestinationCombo.ItemsSource = crashDestinationChoices;
        SearchEngineCombo.SelectedItem = searchChoices.FirstOrDefault(x => x.Value == settings.SearchEngine) ?? searchChoices[0];
        PrivacyLevelCombo.SelectedItem = privacyChoices.First(x => x.Value == settings.PrivacyLevel);
        ProxyTypeCombo.SelectedItem = proxyChoices.First(x => x.Value == settings.ProxyKind);
        CrashReportModeCombo.SelectedItem = crashChoices.First(x => x.Value == settings.CrashReportMode);
        CrashReportDestinationCombo.SelectedItem = crashDestinationChoices.First(x => x.Value == settings.CrashReportDestination);
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
        CrashReportEndpointBox.Text = settings.CrashReportEndpoint;
        MatrixHomeserverBox.Text = settings.MatrixHomeserver;
        MatrixRoomIdBox.Text = settings.MatrixRoomId;
        MatrixTokenStatusText.Text = WindowsCredentialStore.HasMatrixAccessToken()
            ? "Token уже сохранён в Windows Credential Manager. Оставьте поле пустым, чтобы сохранить его."
            : "Token ещё не сохранён. Создайте отдельного Matrix-бота и вставьте его token.";
        UpdateCrashDestinationVisibility();
    }

    private void CrashReportDestinationCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateCrashDestinationVisibility();

    private void CrashReportModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateCrashDestinationVisibility();

    private void UpdateCrashDestinationVisibility()
    {
        if (CollectorSettingsPanel is null || MatrixSettingsPanel is null ||
            CrashDestinationLabel is null || CrashReportDestinationCombo is null) return;
        var localOnly = CrashReportModeCombo.SelectedItem is Choice<CrashReportMode> mode &&
                        mode.Value == CrashReportMode.LocalOnly;
        var matrix = CrashReportDestinationCombo.SelectedItem is Choice<CrashReportDestination> choice &&
                     choice.Value == CrashReportDestination.MatrixDirect;
        CrashDestinationLabel.Visibility = localOnly ? Visibility.Collapsed : Visibility.Visible;
        CrashReportDestinationCombo.Visibility = localOnly ? Visibility.Collapsed : Visibility.Visible;
        CollectorSettingsPanel.Visibility = !localOnly && !matrix ? Visibility.Visible : Visibility.Collapsed;
        MatrixSettingsPanel.Visibility = !localOnly && matrix ? Visibility.Visible : Visibility.Collapsed;
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
        _settings.CrashReportMode = CrashReportModeCombo.SelectedItem is Choice<CrashReportMode> crashMode
            ? crashMode.Value : CrashReportMode.LocalOnly;
        _settings.CrashReportDestination = CrashReportDestinationCombo.SelectedItem is Choice<CrashReportDestination> destination
            ? destination.Value : CrashReportDestination.HttpsCollector;
        _settings.CrashReportEndpoint = CrashReportEndpointBox.Text.Trim();
        _settings.MatrixHomeserver = MatrixHomeserverBox.Text.Trim().TrimEnd('/');
        _settings.MatrixRoomId = MatrixRoomIdBox.Text.Trim();
        if (_settings.CrashReportMode != CrashReportMode.LocalOnly &&
            _settings.CrashReportDestination == CrashReportDestination.HttpsCollector &&
            !string.IsNullOrWhiteSpace(_settings.CrashReportEndpoint) &&
            (!Uri.TryCreate(_settings.CrashReportEndpoint, UriKind.Absolute, out var reportEndpoint) ||
             reportEndpoint.Scheme != Uri.UriSchemeHttps))
        {
            GlassDialogWindow.Show(this, "Для отчётов Nexus Guardian разрешён только абсолютный HTTPS-адрес.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            CrashReportEndpointBox.Focus();
            return;
        }
        if (_settings.CrashReportDestination == CrashReportDestination.MatrixDirect &&
            _settings.CrashReportMode != CrashReportMode.LocalOnly &&
            !TryValidateMatrixSettings(requireToken: true)) return;

        try
        {
            if (DeleteMatrixTokenCheck.IsChecked == true)
                WindowsCredentialStore.DeleteMatrixAccessToken();
            else if (!string.IsNullOrWhiteSpace(MatrixAccessTokenBox.Password))
                WindowsCredentialStore.SaveMatrixAccessToken(MatrixAccessTokenBox.Password);
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось сохранить Matrix token в Windows Credential Manager:\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ResultSettings = _settings;
        DialogResult = true;
    }

    private async void TestMatrix_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateMatrixSettings(requireToken: true)) return;
        var token = string.IsNullOrWhiteSpace(MatrixAccessTokenBox.Password)
            ? WindowsCredentialStore.ReadMatrixAccessToken()
            : MatrixAccessTokenBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(token)) return;

        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try
        {
            var result = await MatrixCrashReportTransport.TestAsync(
                MatrixHomeserverBox.Text.Trim(), MatrixRoomIdBox.Text.Trim(), token);
            GlassDialogWindow.Show(this, result.Message, "Nexus Guardian · Matrix", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }

    private void OpenGuardianCenter_Click(object sender, RoutedEventArgs e)
    {
        var window = new GuardianCenterWindow { Owner = this };
        window.ShowDialog();
    }

    private bool TryValidateMatrixSettings(bool requireToken)
    {
        var homeserver = MatrixHomeserverBox.Text.Trim();
        if (!Uri.TryCreate(homeserver, UriKind.Absolute, out var matrixUri) || matrixUri.Scheme != Uri.UriSchemeHttps)
        {
            GlassDialogWindow.Show(this, "Matrix homeserver должен быть абсолютным HTTPS-адресом.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            MatrixHomeserverBox.Focus();
            return false;
        }
        if (!MatrixRoomIdBox.Text.Trim().StartsWith("!", StringComparison.Ordinal) ||
            !MatrixRoomIdBox.Text.Contains(':'))
        {
            GlassDialogWindow.Show(this, "Укажите внутренний Matrix Room ID вида !room:server, а не название комнаты.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            MatrixRoomIdBox.Focus();
            return false;
        }
        if (requireToken && string.IsNullOrWhiteSpace(MatrixAccessTokenBox.Password) &&
            !WindowsCredentialStore.HasMatrixAccessToken())
        {
            GlassDialogWindow.Show(this, "Укажите access token отдельного Matrix-бота.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            MatrixAccessTokenBox.Focus();
            return false;
        }
        return true;
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
