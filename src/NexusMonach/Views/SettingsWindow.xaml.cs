using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        var themeChoices = new[]
        {
            new Choice<BrowserTheme>("Monach Aqua — фирменная бирюза", BrowserTheme.MonachAqua),
            new Choice<BrowserTheme>("Ocean — глубокий синий", BrowserTheme.Ocean),
            new Choice<BrowserTheme>("Forest — спокойный зелёный", BrowserTheme.Forest),
            new Choice<BrowserTheme>("Amethyst — тёмный фиолетовый", BrowserTheme.Amethyst)
        };
        var themeModeChoices = new[]
        {
            new Choice<BrowserThemeMode>("Тёмный — тёмный фон и светлый текст", BrowserThemeMode.Dark),
            new Choice<BrowserThemeMode>("Светлый — светлый фон и тёмный текст", BrowserThemeMode.Light)
        };
        var proxyChoices = new[]
        {
            new Choice<ProxyKind>("SOCKS5 — подходит для Tor и локальных туннелей", ProxyKind.Socks5),
            new Choice<ProxyKind>("HTTP / HTTPS proxy", ProxyKind.Http)
        };
        var secureDnsModeChoices = new[]
        {
            new Choice<SecureDnsMode>("Строгий DoH — без незашифрованного fallback (рекомендуется)",
                SecureDnsMode.Strict),
            new Choice<SecureDnsMode>("Автоматический DoH — системный fallback при ошибке",
                SecureDnsMode.Automatic),
            new Choice<SecureDnsMode>("DNS системы Windows", SecureDnsMode.System)
        };
        var secureDnsProviderChoices = new[]
        {
            new Choice<SecureDnsProvider>("Cloudflare 1.1.1.1 — без категорийной фильтрации",
                SecureDnsProvider.Cloudflare),
            new Choice<SecureDnsProvider>("Quad9 Secure — блокирует известные вредоносные домены",
                SecureDnsProvider.Quad9)
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
            new Choice<CrashReportDestination>("В GitHub Issues", CrashReportDestination.GitHubIssues)
        };
        var voiceChoices = new[]
        {
            new Choice<VoiceAssistantMode>("Выключен", VoiceAssistantMode.Off),
            new Choice<VoiceAssistantMode>("Только важное — рекомендуется", VoiceAssistantMode.ImportantOnly),
            new Choice<VoiceAssistantMode>("Помощник — важное и ход операций", VoiceAssistantMode.Assistant)
        };
        var neuralVoiceChoices = new[]
        {
            new Choice<NeuralVoiceProfile>("Ксения · женский (рекомендуется)", NeuralVoiceProfile.Natasha),
            new Choice<NeuralVoiceProfile>("Ирина · женский, спокойный", NeuralVoiceProfile.Irina),
            new Choice<NeuralVoiceProfile>("Аврора · женский, выразительный", NeuralVoiceProfile.Aurora),
            new Choice<NeuralVoiceProfile>("Евгений · мужский", NeuralVoiceProfile.Eugene)
        };
        var videoTranslationChoices = new[]
        {
            new Choice<VideoTranslationMode>("Быстрый · минимальная задержка", VideoTranslationMode.Fast),
            new Choice<VideoTranslationMode>("Сбалансированный · рекомендуется", VideoTranslationMode.Balanced),
            new Choice<VideoTranslationMode>("Качественный · больше контекста", VideoTranslationMode.Quality)
        };
        SearchEngineCombo.ItemsSource = searchChoices;
        PrivacyLevelCombo.ItemsSource = privacyChoices;
        ThemeCombo.ItemsSource = themeChoices;
        ThemeModeCombo.ItemsSource = themeModeChoices;
        ProxyTypeCombo.ItemsSource = proxyChoices;
        SecureDnsModeCombo.ItemsSource = secureDnsModeChoices;
        SecureDnsProviderCombo.ItemsSource = secureDnsProviderChoices;
        CrashReportModeCombo.ItemsSource = crashChoices;
        CrashReportDestinationCombo.ItemsSource = crashDestinationChoices;
        VoiceModeCombo.ItemsSource = voiceChoices;
        NeuralVoiceCombo.ItemsSource = neuralVoiceChoices;
        VideoTranslationModeCombo.ItemsSource = videoTranslationChoices;
        SearchEngineCombo.SelectedItem = searchChoices.FirstOrDefault(x => x.Value == settings.SearchEngine) ?? searchChoices[0];
        PrivacyLevelCombo.SelectedItem = privacyChoices.First(x => x.Value == settings.PrivacyLevel);
        ThemeCombo.SelectedItem = themeChoices.First(x => x.Value == settings.Theme);
        ThemeModeCombo.SelectedItem = themeModeChoices.First(x => x.Value == settings.ThemeMode);
        ProxyTypeCombo.SelectedItem = proxyChoices.First(x => x.Value == settings.ProxyKind);
        SecureDnsModeCombo.SelectedItem = secureDnsModeChoices.First(x => x.Value == settings.SecureDnsMode);
        SecureDnsProviderCombo.SelectedItem = secureDnsProviderChoices.First(x => x.Value == settings.SecureDnsProvider);
        CrashReportModeCombo.SelectedItem = crashChoices.First(x => x.Value == settings.CrashReportMode);
        CrashReportDestinationCombo.SelectedItem = crashDestinationChoices.First(x => x.Value == settings.CrashReportDestination);
        VoiceModeCombo.SelectedItem = voiceChoices.First(x => x.Value == settings.VoiceAssistantMode);
        NeuralVoiceCombo.SelectedItem = neuralVoiceChoices.First(x => x.Value == settings.NeuralVoiceProfile);
        VideoTranslationModeCombo.SelectedItem = videoTranslationChoices.FirstOrDefault(
            x => x.Value == settings.VideoTranslationMode) ?? videoTranslationChoices[1];
        VoiceEngineStatusText.Text = VoiceAssistantService.EngineStatus;
        HomePageBox.Text = settings.HomePage;
        DntCheck.IsChecked = settings.SendDoNotTrack;
        GpcCheck.IsChecked = settings.SendGlobalPrivacyControl;
        StripParametersCheck.IsChecked = settings.StripTrackingParameters;
        BlockNotificationsCheck.IsChecked = settings.BlockNotifications;
        KnowledgeGraphCheck.IsChecked = settings.BuildKnowledgeGraph;
        MemorySaverCheck.IsChecked = settings.MemorySaver;
        ExtensionsCheck.IsChecked = settings.EnableExtensions;
        PasswordCheck.IsChecked = settings.EnablePasswordAutosave;
        AutofillCheck.IsChecked = settings.EnableGeneralAutofill;
        VoiceStartupCheck.IsChecked = settings.VoiceSpeakAtStartup;
        VoiceHandsFreeCheck.IsChecked = settings.VoiceHandsFreeEnabled;
        CustomProxyCheck.IsChecked = settings.EnableCustomProxy;
        ProxyHostBox.Text = settings.ProxyHost;
        ProxyPortBox.Text = settings.ProxyPort.ToString();
        ProxyBypassBox.Text = settings.ProxyBypassList;
        TrailModeCheck.IsChecked = settings.TrailModeEnabled;
        TorRelayCheck.IsChecked = settings.TorRelayEnabled;
        VlessUriBox.Text = settings.VlessProfileUri;
        VlessEnabledCheck.IsChecked = settings.VlessEnabled;
        TorInChainCheck.IsChecked = settings.TorInChain;
        UpdateVlessStatus();
        UpdateRelayStatus(settings);
        TorBridgesBox.Text = settings.TorCustomBridges;
        UpdateTorStatus();
        HttpsFirstCheck.IsChecked = settings.HttpsFirstEnabled;
        PrivacyMonitorCheck.IsChecked = settings.ShowPrivacyMonitor;
        PreventWebRtcLeakCheck.IsChecked = settings.PreventWebRtcIpLeak;
        PortShieldModeCombo.ItemsSource = new[]
        {
            new Choice<Services.PortShieldMode>("Авто — закрывать утечки на сессию (один UAC)", Services.PortShieldMode.Auto),
            new Choice<Services.PortShieldMode>("Только уведомлять голосом", Services.PortShieldMode.NotifyOnly),
            new Choice<Services.PortShieldMode>("Выключить", Services.PortShieldMode.Off)
        };
        PortShieldModeCombo.SelectedItem = PortShieldModeCombo.ItemsSource
            .Cast<Choice<Services.PortShieldMode>>()
            .FirstOrDefault(c => c.Value == settings.PortShieldMode);
        CrashReportEndpointBox.Text = settings.CrashReportEndpoint;
        GitHubRepositoryBox.Text = settings.GitHubRepository;
        GitHubTokenStatusText.Text = WindowsCredentialStore.HasGitHubAccessToken()
            ? "Token уже сохранён в Windows Credential Manager. Оставьте поле пустым, чтобы сохранить его."
            : "Token ещё не сохранён. Создайте fine-grained PAT с правами Issues: Read & Write на нужный репозиторий.";
        UpdateCrashDestinationVisibility();
        ShowSection("Search");
    }

    private void SettingsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string section })
        {
            ShowSection(section);
            if (section == "Network") UpdateTorStatus();
        }
    }

    private void PortShieldModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
    }

    /// <summary>
    /// Прозрачность UAC: показываем ровно тот скрипт, который выполнит
    /// порт-щит при повышении прав, с пояснением каждой строки.
    /// </summary>
    private void PreviewShieldScript_Click(object sender, RoutedEventArgs e)
    {
        var script = Services.PortShieldService.BuildRuleScript(
            Services.PortShieldService.AutoClosedLeaks.ToList(), add: true);
        var explained = string.Join(Environment.NewLine, script.TrimEnd().Split('\n')
            .Select(line => line.TrimStart())
            .Select(line => line.StartsWith("New-NetFirewallRule", StringComparison.Ordinal)
                ? line + "   ← создать правило блокировки"
                : line.StartsWith("Remove-NetFirewallRule", StringComparison.Ordinal)
                    ? line + "   ← снять предыдущее правило (идемпотентность)"
                    : line));
        GlassDialogWindow.Show(this,
            "Этот скрипт выполняется при включённом режиме «Авто» — один раз за сессию, скрыто (conhost --headless), виден только стандартный диалог UAC:\n\n" +
            explained +
            "\n\nПорты: 5353 mDNS, 1900 SSDP, 137–139 NetBIOS — утечки локальной сети. " +
            "Пользовательские службы (RDP, SMB, VNC) скрипт не трогает.",
            "Порт-щит · предпросмотр скрипта UAC",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateRelayStatus(BrowserSettings settings)
    {
        if (TorRelayStatusText is null) return;
        var state = Services.Tor.TorRelayService.GetState(
            settings.TorRelayEnabled, settings.TorRelayOrPort, settings.TorRelayObfs4Port);
        TorRelayStatusText.Text = Services.Tor.TorRelayService.Describe(
            state, settings.TorRelayOrPort, settings.TorRelayObfs4Port);
    }

    private async void TorStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (TorStartButton is null) return;
        TorStartButton.IsEnabled = false;
        TorStartButton.Content = "Запускаю Tor…";
        try
        {
            var settings = SettingsService.Current.Clone();
            settings.TorCustomBridges = TorBridgesBox.Text.Trim();
            var state = await Services.Tor.TorBridgeManager.RestartWithBridgesAsync(settings);
            UpdateTorStatus();
            if (state == Services.Tor.TorState.Failed)
                GlassDialogWindow.Show(this,
                    "Tor не смог запуститься. Проверьте, что tor.exe установлен (C:\\Tor) и мосты указаны верно.",
                    "Tor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("tor", "start-from-settings", ex);
        }
        finally
        {
            TorStartButton.IsEnabled = true;
            TorStartButton.Content = "Запустить Tor";
        }
    }

    private void UpdateTorStatus()
    {
        if (TorStatusLabel is null) return;
        var (ready, status) = Services.Tor.TrailMode.CheckTorStatus();
        var vpn = Services.Tor.VpnDetector.Detect();        var vpnText = vpn.VpnActive
            ? $"\nVPN: {vpn.AdapterName} ({vpn.AdapterType})"
            : "\nVPN: не найден";
        var portsText = vpn.OpenPorts.Count > 0
            ? $"\nОткрытые порты: {string.Join(", ", vpn.OpenPorts)}"
            : "";
        TorStatusLabel.Text = status + vpnText + portsText;
    }

    private void UpdateVlessStatus()
    {
        if (VlessStatusLabel is null) return;
        var snapshot = Services.NetworkChainService.Snapshot();
        var profile = Services.Vless.VlessProfile.TryParse(
            SettingsService.Current.VlessProfileUri, out _, out _) ? "" : "Ссылка не заполнена. ";
        VlessStatusLabel.Text = profile + snapshot.StatusText +
            (snapshot.VlessRunning ? $" (SOCKS {Services.Vless.VlessRuntime.SocksPort})" : "");
    }

    private async void VlessConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (VlessConnectButton is null) return;
        VlessConnectButton.IsEnabled = false;
        VlessConnectButton.Content = "Подключаю…";
        try
        {
            var settings = SettingsService.Current;
            settings.VlessProfileUri = VlessUriBox.Text.Trim();
            if (!Services.Vless.VlessProfile.TryParse(settings.VlessProfileUri, out _, out var error))
            {
                VlessStatusLabel.Text = "Ссылка не принята: " + error;
                return;
            }
            await SettingsService.SaveAsync(settings);
            var snapshot = await Services.NetworkChainService.EnsureVlessAsync();
            VlessEnabledCheck.IsChecked = SettingsService.Current.VlessEnabled;
            VlessStatusLabel.Text = snapshot.StatusText +
                (snapshot.VlessRunning ? $" (SOCKS {Services.Vless.VlessRuntime.SocksPort})" : "");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("vless", "connect-from-settings", ex);
            VlessStatusLabel.Text = "Ошибка подключения: " + ex.Message;
        }
        finally
        {
            VlessConnectButton.IsEnabled = true;
            VlessConnectButton.Content = "Проверить и подключить";
        }
    }

    private void ShowSection(string section)
    {
        if (SearchSection is null) return;
        var sections = new Dictionary<string, (FrameworkElement View, string Title)>
        {
            ["Search"] = (SearchSection, "Поиск и стартовая страница"),
            ["Appearance"] = (AppearanceSection, "Внешний вид"),
            ["Privacy"] = (PrivacySection, "Приватность"),
            ["Compatibility"] = (CompatibilitySection, "Совместимость и функции"),
            ["Guardian"] = (GuardianSection, "Nexus Guardian"),
            ["Network"] = (NetworkSection, "Сеть, прокси и DNS"),
            ["Monitor"] = (MonitorSection, "Privacy Dock"),
            ["Passkeys"] = (PasskeysSection, "Passkeys и Windows Hello")
        };
        foreach (var item in sections.Values)
            item.View.Visibility = Visibility.Collapsed;
        if (!sections.TryGetValue(section, out var selected))
            selected = sections["Search"];
        selected.View.Visibility = Visibility.Visible;
        SettingsSectionTitle.Text = selected.Title;
    }

    private void CrashReportDestinationCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateCrashDestinationVisibility();

    private void CrashReportModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateCrashDestinationVisibility();

    private void UpdateCrashDestinationVisibility()
    {
        if (CollectorSettingsPanel is null || GitHubSettingsPanel is null ||
            CrashDestinationLabel is null || CrashReportDestinationCombo is null) return;
        var localOnly = CrashReportModeCombo.SelectedItem is Choice<CrashReportMode> mode &&
                        mode.Value == CrashReportMode.LocalOnly;
        var gitHub = CrashReportDestinationCombo.SelectedItem is Choice<CrashReportDestination> choice &&
                     choice.Value == CrashReportDestination.GitHubIssues;
        CrashDestinationLabel.Visibility = localOnly ? Visibility.Collapsed : Visibility.Visible;
        CrashReportDestinationCombo.Visibility = localOnly ? Visibility.Collapsed : Visibility.Visible;
        CollectorSettingsPanel.Visibility = !localOnly && !gitHub ? Visibility.Visible : Visibility.Collapsed;
        GitHubSettingsPanel.Visibility = !localOnly && gitHub ? Visibility.Visible : Visibility.Collapsed;
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
        _settings.Theme = ThemeCombo.SelectedItem is Choice<BrowserTheme> theme
            ? theme.Value : BrowserTheme.MonachAqua;
        _settings.ThemeMode = ThemeModeCombo.SelectedItem is Choice<BrowserThemeMode> mode
            ? mode.Value : BrowserThemeMode.Dark;
        _settings.ThemeSelectionCompleted = true;
        _settings.HomePage = string.IsNullOrWhiteSpace(HomePageBox.Text) ? "app://newtab" : HomePageBox.Text.Trim();
        _settings.SendDoNotTrack = DntCheck.IsChecked == true;
        _settings.SendGlobalPrivacyControl = GpcCheck.IsChecked == true;
        _settings.StripTrackingParameters = StripParametersCheck.IsChecked == true;
        _settings.BlockNotifications = BlockNotificationsCheck.IsChecked == true;
        _settings.BuildKnowledgeGraph = KnowledgeGraphCheck.IsChecked == true;
        _settings.RestoreSession = false;
        // История навигации, поисковые переходы и дисковый кэш очищаются
        // автоматически при полном закрытии браузера. Cookies, авторизация,
        // пароли и локальный граф знаний в эту очистку не входят.
        _settings.ClearBrowsingDataOnExit = true;
        _settings.MemorySaver = MemorySaverCheck.IsChecked == true;
        _settings.EnableExtensions = ExtensionsCheck.IsChecked == true;
        _settings.EnablePasswordAutosave = PasswordCheck.IsChecked == true;
        _settings.EnableGeneralAutofill = AutofillCheck.IsChecked == true;
        _settings.EnableDevTools = false;
        _settings.VoiceAssistantMode = VoiceModeCombo.SelectedItem is Choice<VoiceAssistantMode> voiceMode
            ? voiceMode.Value : VoiceAssistantMode.ImportantOnly;
        _settings.NeuralVoiceProfile = NeuralVoiceCombo.SelectedItem is Choice<NeuralVoiceProfile> neuralVoice
            ? neuralVoice.Value : NeuralVoiceProfile.Natasha;
        _settings.VideoTranslationMode = VideoTranslationModeCombo.SelectedItem is Choice<VideoTranslationMode> videoMode
            ? videoMode.Value : VideoTranslationMode.Balanced;
        _settings.VoiceSpeakAtStartup = VoiceStartupCheck.IsChecked == true;
        _settings.VoiceHandsFreeEnabled = VoiceHandsFreeCheck.IsChecked == true &&
                                          _settings.VoiceAssistantMode != VoiceAssistantMode.Off;
        _settings.EnableCustomProxy = proxyEnabled;
        _settings.ProxyKind = ProxyTypeCombo.SelectedItem is Choice<ProxyKind> proxy
            ? proxy.Value : ProxyKind.Socks5;
        _settings.ProxyHost = ProxyHostBox.Text.Trim();
        _settings.ProxyPort = proxyPort;
        _settings.ProxyBypassList = ProxyBypassBox.Text.Trim();
        _settings.TrailModeEnabled = TrailModeCheck.IsChecked == true;
        _settings.TorCustomBridges = TorBridgesBox.Text.Trim();
        _settings.TorRelayEnabled = TorRelayCheck.IsChecked == true;
        // Сервер и цепочка: ссылка могла быть только что подключена кнопкой —
        // берём фактическое состояние служб, а не только чекбоксы.
        _settings.VlessProfileUri = VlessUriBox.Text.Trim();
        _settings.VlessEnabled = VlessEnabledCheck.IsChecked == true ||
                                 Services.Vless.VlessRuntime.IsRunning;
        _settings.TorInChain = TorInChainCheck.IsChecked == true;
        // Включение моста — осознанное решение: человек должен понимать,
        // что увидит провайдер и когда мост реально полезен.
        if (_settings.TorRelayEnabled &&
            !SettingsService.Current.TorRelayEnabled &&
            !SettingsService.Current.TorRelayAcknowledged)
        {
            var answer = GlassDialogWindow.Show(this,
                "Вы включаете релейный мост Tor — эта копия браузера станет точкой входа в сеть Tor для людей из цензурных сетей.\n\n" +
                "Что нужно знать:\n" +
                "• Провайдер увидит Tor-трафик с вашей машины (вы НЕ выходной узел — чужой трафик через вас не проходит наружу).\n" +
                "• Без проброса портов 9101–9102 на роутере мост бесполезен для других, но трафик виден. Порт-проброс делается в админке роутера.\n" +
                "• В отдельных странах сам факт работы моста — серая зона. Решайте сами.\n\n" +
                "Включить мост?",
                "Релейный мост Tor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                _settings.TorRelayEnabled = false;
                TorRelayCheck.IsChecked = false;
            }
            else
            {
                _settings.TorRelayAcknowledged = true;
            }
        }
        if (string.IsNullOrWhiteSpace(_settings.TorRelayNickname))
            _settings.TorRelayNickname = Services.Tor.TorRelayService.DefaultNickname();
        if (_settings.TrailModeEnabled)
        {
            // «Режим След» применяет полную анонимную конфигурацию поверх
            // обычных настроек: Tor SOCKS5, строгая приватность, всё выключено.
            Services.Tor.TrailMode.Apply(_settings);
        }
        _settings.HttpsFirstEnabled = HttpsFirstCheck.IsChecked == true;
        _settings.SecureDnsMode = SecureDnsModeCombo.SelectedItem is Choice<SecureDnsMode> dnsMode
            ? dnsMode.Value : SecureDnsMode.Strict;
        _settings.SecureDnsProvider = SecureDnsProviderCombo.SelectedItem is Choice<SecureDnsProvider> dnsProvider
            ? dnsProvider.Value : SecureDnsProvider.Cloudflare;
        _settings.ShowPrivacyMonitor = PrivacyMonitorCheck.IsChecked == true;
        _settings.PortShieldMode = PortShieldModeCombo.SelectedItem is Choice<Services.PortShieldMode> shieldMode
            ? shieldMode.Value : Services.PortShieldMode.NotifyOnly;
        _settings.PreventWebRtcIpLeak = PreventWebRtcLeakCheck.IsChecked == true;
        _settings.CrashReportMode = CrashReportModeCombo.SelectedItem is Choice<CrashReportMode> crashMode
            ? crashMode.Value : CrashReportMode.LocalOnly;
        _settings.CrashReportDestination = CrashReportDestinationCombo.SelectedItem is Choice<CrashReportDestination> destination
            ? destination.Value : CrashReportDestination.HttpsCollector;
        _settings.CrashReportEndpoint = CrashReportEndpointBox.Text.Trim();
        _settings.GitHubRepository = GitHubRepositoryBox.Text.Trim();
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
        if (_settings.CrashReportDestination == CrashReportDestination.GitHubIssues &&
            _settings.CrashReportMode != CrashReportMode.LocalOnly &&
            !TryValidateGitHubSettings(requireToken: true)) return;

        try
        {
            if (DeleteGitHubTokenCheck.IsChecked == true)
                WindowsCredentialStore.DeleteGitHubAccessToken();
            else if (!string.IsNullOrWhiteSpace(GitHubAccessTokenBox.Password))
                WindowsCredentialStore.SaveGitHubAccessToken(GitHubAccessTokenBox.Password);
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось сохранить GitHub token в Windows Credential Manager:\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ResultSettings = _settings;
        DialogResult = true;
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        var previousMode = SettingsService.Current.VoiceAssistantMode;
        var previousVoice = SettingsService.Current.NeuralVoiceProfile;
        SettingsService.Current.VoiceAssistantMode = VoiceAssistantMode.Assistant;
        SettingsService.Current.NeuralVoiceProfile = NeuralVoiceCombo.SelectedItem is Choice<NeuralVoiceProfile> voice
            ? voice.Value : NeuralVoiceProfile.Natasha;
        try { VoiceAssistantService.SpeakTestPhrase(); }
        finally
        {
            SettingsService.Current.VoiceAssistantMode = previousMode;
            SettingsService.Current.NeuralVoiceProfile = previousVoice;
        }
    }

    private async void TestGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateGitHubSettings(requireToken: true)) return;
        var token = string.IsNullOrWhiteSpace(GitHubAccessTokenBox.Password)
            ? WindowsCredentialStore.ReadGitHubAccessToken()
            : GitHubAccessTokenBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(token)) return;

        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try
        {
            var result = await GitHubCrashReportTransport.TestAsync(GitHubRepositoryBox.Text.Trim(), token);
            GlassDialogWindow.Show(this, result.Message, "Nexus Guardian · GitHub", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }

    private void OpenGuardianCenter_Click(object sender, RoutedEventArgs e)
    {
        var window = new GuardianCenterWindow { Owner = this };
        window.ShowDialog();
    }

    private bool TryValidateGitHubSettings(bool requireToken)
    {
        var repository = GitHubRepositoryBox.Text.Trim();
        if (!GitHubCrashReportTransport.IsValidRepository(repository))
        {
            GlassDialogWindow.Show(this,
                "Репозиторий указывается в формате «владелец/имя», например monach-coder/NexusMonachBrowser.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            GitHubRepositoryBox.Focus();
            return false;
        }
        if (requireToken && string.IsNullOrWhiteSpace(GitHubAccessTokenBox.Password) &&
            !WindowsCredentialStore.HasGitHubAccessToken())
        {
            GlassDialogWindow.Show(this,
                "Укажите access token: fine-grained PAT с правами Issues: Read & Write на выбранный репозиторий.",
                "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
            GitHubAccessTokenBox.Focus();
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
