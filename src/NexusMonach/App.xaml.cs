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

    /// <summary>
    /// Прогон полного конвейера дубляжа на файле дорожки: пишет отчёт с
    /// таймкодами, текстами и длительностями — численная проверка
    /// синхронности без прослушивания.
    /// </summary>
    private async Task RunDubTrackDiagnosticAsync(string trackPath, string reportPath)
    {
        var exitCode = 0;
        try
        {
            // Путь приходит из командной строки: в декод допускается только
            // существующий локальный аудиофайл из белого списка расширений —
            // никакие другие значения до аргументов процесса не доходят.
            var allowed = new[]
            {
                ".wav", ".m4a", ".mp4", ".mp3", ".flac", ".ogg", ".oga", ".aac", ".webm", ".bin"
            };
            var extension = Path.GetExtension(trackPath);
            if (!Path.IsPathRooted(trackPath) ||
                !File.Exists(trackPath) ||
                trackPath.IndexOf('\0') >= 0 ||
                !allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Путь дорожки должен быть абсолютным путём к существующему аудиофайлу " +
                    "(" + string.Join(", ", allowed) + ").");
            AppPaths.Initialize(["--dub-track", trackPath]);
            CrashReportService.Initialize();
            NexusFabricRuntime.Initialize();
            await SettingsService.InitializeAsync();
            var report = string.IsNullOrWhiteSpace(reportPath)
                ? Path.ChangeExtension(trackPath, ".dubreport.txt")
                : reportPath;
            var lines = new List<string>
            {
                $"дорожка: {trackPath}",
                $"ffmpeg: {AiModelCatalog.FfmpegExecutable ?? "нет"}",
                $"whisper: {AiModelCatalog.WhisperServer ?? "нет"}"
            };
            await File.WriteAllLinesAsync(report, lines);

            byte[] trackWav;
            if (Path.GetExtension(trackPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                trackWav = await File.ReadAllBytesAsync(trackPath);
            }
            else
            {
                var decoded = Path.Combine(Path.GetTempPath(),
                    "nexus-dubtrack-" + Guid.NewGuid().ToString("N") + ".wav");
                var ffmpeg = AiModelCatalog.FfmpegExecutable
                             ?? throw new InvalidOperationException("ffmpeg недоступен");
                var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(trackPath);
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-ar");
                psi.ArgumentList.Add("16000");
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(decoded);
                using (var process = System.Diagnostics.Process.Start(psi) ??
                                     throw new InvalidOperationException("ffmpeg не запустился"))
                {
                    _ = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                }
                trackWav = await File.ReadAllBytesAsync(decoded);
                try { File.Delete(decoded); } catch { }
            }
            lines.Add($"длительность дорожки: {AudioRateRestore.PcmDurationSeconds(trackWav):F1} с");

            var progress = new Progress<string>(step =>
            {
                try
                {
                    File.AppendAllText(report,
                        $"{DateTimeOffset.Now:HH:mm:ss.fff} {step}\n");
                }
                catch { }
            });
            var wavs = new List<string>();
            var phrases = await TrackDubbingComposer.ComposeAsync(
                trackWav, 0, double.MaxValue, wavs, progress, CancellationToken.None);
            // Диагностика качества: исходные реплики whisper с маркерами.
            var raw = await WhisperService.TranscribeDetailedAsync(
                AudioRateRestore.SliceByTime(trackWav, 0, 20),
                WhisperLane.Dubbing, CancellationToken.None);
            var rawLines = new List<string> { string.Empty, "=== WHISPER СЫРЬЁ (0–20 с) ===" };
            foreach (var segment in raw.Segments)
                rawLines.Add(
                    $"{segment.Start,6:F2}-{segment.End,6:F2} nospeech={segment.NoSpeechProb:F2} logp={segment.AvgLogProb:F2} | {segment.Text}");
            await File.AppendAllLinesAsync(report, rawLines);
            lines.Add(string.Empty);
            lines.Add("=== РЕПЛИКИ ===");
            foreach (var phrase in phrases)
            {
                var dubSeconds = phrase.WavPaths.Sum(path =>
                    AudioRateRestore.PcmDurationSeconds(File.ReadAllBytes(path)));
                lines.Add(
                    $"{phrase.StartSeconds,7:F2} → {phrase.SlotEndSeconds,7:F2} " +
                    $"(слот {phrase.SlotEndSeconds - phrase.StartSeconds,5:F2} с, дуб {dubSeconds,5:F2} с, " +
                    $"файлы {phrase.WavPaths.Count}) | {phrase.RussianText}");
            }
            lines.Add(string.Empty);
            lines.Add($"итого реплик: {phrases.Count}");
            await File.AppendAllLinesAsync(report, lines);
            Console.WriteLine($"готово: {phrases.Count} реплик, отчёт: {report}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ошибка: " + ex.Message);
            exitCode = 1;
        }
        finally
        {
            VideoDubbingVoiceService.Stop();
            TranslationService.Stop();
            WhisperService.Shutdown();
            Shutdown(exitCode);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (RedirectPortableLaunchToGuardian(e.Args)) return;
        // Диагностический стенд дубляжа: полный конвейер на файле без окна —
        // верификация синхронности оффлайн, без браузера и страницы.
        if (e.Args.Length >= 2 &&
            e.Args[0].Equals("--dub-track", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunDubTrackDiagnosticAsync(e.Args[1],
                e.Args.Length >= 3 ? e.Args[2] : string.Empty);
            return;
        }
        // Unattended readiness probe: initialize everything, show the real
        // window, then exit with code 0. CI uses it to catch startup crashes
        // that unit tests cannot see; interactive flows stay untouched.
        var smokeSelfTest = e.Args.Any(x => x.Equals("--smoke-self-test", StringComparison.OrdinalIgnoreCase));
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        AppPaths.Initialize(e.Args);
        Services.Ui.CaptureFrom(this);
        CrashReportService.Initialize();
        CrashReportService.AddBreadcrumb("startup", "app-paths-ready");
        if (GuardianRuntime.IsSafeMode)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            CrashReportService.AddBreadcrumb("guardian", "software-rendering-enabled");
        }
        else if (GuardianRuntime.DisableGpuOnly)
        {
            // Осторожный режим после одиночного сбоя графики: рисуем без GPU,
            // но AI, расширения и голос работают — фишки браузера не теряем.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            CrashReportService.AddBreadcrumb("guardian", "cautious-gpu-off-rendering");
        }
        if (!GuardianRuntime.IsSafeMode)
        {
            NexusFabricRuntime.Initialize();
        }

        var splash = new SplashWindow();
        splash.Show();
        // Единое стартовое окно: Guardian показал проверку целостности и ход
        // обновления своим круглым секторным окном; здесь эстафета продолжается.
        var integrityVerified =
            GuardianRuntime.IntegrityStatus.Equals("verified", StringComparison.OrdinalIgnoreCase);
        splash.SetStatus("Guardian: целостность " +
            (integrityVerified ? "подтверждена" : GuardianRuntime.IntegrityStatus));
        if (integrityVerified) splash.CompleteSector(0);
        _ = Services.SplashUpdateWatcher.RunAsync(splash);
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
                    firstRunSettings.NeuralVoiceProfile = themePicker.ResultVoice;
                    // Мастер безопасности: порт-щит, Дозор, релейный мост.
                    firstRunSettings.PortShieldMode = themePicker.ResultPortShield;
                    firstRunSettings.NetworkWatchdogEnabled = themePicker.ResultWatchdog;
                    firstRunSettings.TorRelayEnabled = themePicker.ResultRelay;
                    firstRunSettings.TorRelayAcknowledged = themePicker.ResultRelay;
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
            // Сектор «запуск» закрыт: кольцо стартовых процессов пройдено.
            splash.CompleteSector(3);
            if (GuardianRuntime.IsSafeMode)
            {
                GlassDialogWindow.Show(mainWindow,
                    "Nexus Guardian включил безопасный режим после повторных сбоев, графической ошибки или изменения некритических файлов. AI, расширения и аппаратное ускорение временно отключены.",
                    "Nexus Guardian — безопасный режим", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            VoiceAssistantService.Initialize();
            if (!smokeSelfTest)
            {
                // Только что встало обновление — говорим об этом явно.
                if (GuardianRuntime.UpdatedToVersion is { Length: > 0 } updated)
                {
                    CrashReportService.AddBreadcrumb("startup", "updated-to-" + updated);
                    VoiceAssistantService.Announce(
                        $"Nexus обновлён до версии {updated}.",
                        VoiceAnnouncementPriority.Important);
                }
                VoiceAssistantService.Announce(
                    GuardianRuntime.IsSafeMode
                        ? "Nexus запущен в безопасном режиме."
                        : GuardianRuntime.DisableGpuOnly
                            ? "Nexus готов. Графическое ускорение временно отключено после сбоя графики."
                            : GuardianRuntime.IntegrityStatus.Equals("verified", StringComparison.OrdinalIgnoreCase)
                                ? "Nexus готов. Целостность браузера подтверждена."
                                : "Nexus готов к работе.",
                    GuardianRuntime.IsSafeMode
                        ? VoiceAnnouncementPriority.Critical
                        : VoiceAnnouncementPriority.Important);
                if (!GuardianRuntime.IsSafeMode)
                {
                    // Первый запуск с сетевой поставкой: не греем конвейеры,
                    // пока модели не приехали — иначе старт задыхается на
                    // скачке, распаковке и холодных стартах одновременно.
                    if (Services.OnlinePack.AiPackFetchService.WarmUpAfterFetch(
                            () =>
                            {
                                WhisperService.PrepareInBackground();
                                TranslationService.WarmUpInBackground();
                                LocalAiService.WarmUpInBackground();
                            }))
                    {
                        CrashReportService.AddBreadcrumb("startup", "ai-warmup-deferred-until-fetch");
                    }
                    else
                    {
                        WhisperService.PrepareInBackground();
                        TranslationService.WarmUpInBackground();
                        LocalAiService.WarmUpInBackground();
                    }
                    _ = Task.Run(RussianStressDictionary.WarmUp);
                    // Осторожный режим: пробуем вернуть полный режим сами.
                    GpuRecoveryService.StartIfCautiousMode();
                    // Недостающие AI-модели подтягиваются по сети в фоне.
                    Services.OnlinePack.AiPackFetchService.StartBackgroundFetch();
                    // Порт-щит: скан и автозакрытие утекающих портов на сессию.
                    Services.PortShieldService.StartAsync(SettingsService.Current);
                    // Скан VPN на машине: Tor в Режиме Следа оборачивается в
                    // найденный туннель; результат слышен и виден в логах.
                    _ = Task.Run(ReportVpnState);
                    // Сетевая цепочка: сначала транспорт (Xray), потом Тор —
                    // torrc генерируется уже с обёрткой в поднятый сервер.
                    _ = Task.Run(StartNetworkChainAsync);
                    // Слои доверия: целостность профиля между сессиями,
                    // свежесть движка, канарейка цепочки.
                    Services.ProfileIntegrityService.VerifyAtStartup();
                    _ = Task.Run(Services.WebView2RuntimeWatchdog.CheckAsync);
                    Services.EgressCanaryService.Start();
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
            // Самопроверка окна Дозора: конструкция когда-то падала на
            // XamlParseException (SettingsCard), убивая браузер по клику меню.
            // Прогон конструирования и показа ловит этот класс багов в CI.
            if (e.Args.Any(x => x.Equals("--self-test-watchdog-window", StringComparison.OrdinalIgnoreCase)))
            {
                CrashReportService.AddBreadcrumb("watchdog", "window-self-test");
                var watchdog = new Services.Tor.NetworkWatchdog();
                var watchdogWindow = new NetworkWatchdogWindow(watchdog) { Owner = mainWindow };
                watchdogWindow.Show();
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (!watchdogWindow.IsLoaded)
                    throw new InvalidOperationException("Окно Сетевого Дозора не загрузилось.");
                watchdogWindow.Close();
                CrashReportService.AddBreadcrumb("watchdog", "window-self-test-ok");
                Shutdown(0);
                return;
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
        ShutdownCoordinator.RunStep("gpu-recovery", GpuRecoveryService.Stop, timeout);
        // Снимок целостности профиля: при следующем старте сверим — менялось
        // ли что-то, пока браузер был закрыт.
        ShutdownCoordinator.RunStep("profile-integrity", Services.ProfileIntegrityService.CaptureAtExit, timeout);
        Services.EgressCanaryService.Stop();
        // Правила порт-щита живут только пока работает браузер.
        Services.PortShieldService.RemoveSessionShield();
        CrashReportService.MarkCleanExit();
        base.OnExit(e);
    }

    /// <summary>
    /// Видимый стартовый скан VPN: озвучивает результат и оставляет след
    /// в breadcrumbs. Tor в Режиме Следа оборачивается в найденный туннель.
    /// </summary>
    private static void ReportVpnState()
    {
        try
        {
            var vpn = Services.Tor.VpnDetector.Detect();
            CrashReportService.AddBreadcrumb("vpn-scan",
                vpn.VpnActive ? "active:" + vpn.AdapterName : "not-found");
            Ui.Post(() => Services.VoiceAssistantService.Announce(
                vpn.VpnActive
                    ? $"Обнаружен системный туннель: {vpn.AdapterName}. Маршрут будет работать через него."
                    : "Системный туннель на машине не обнаружен.",
                Services.VoiceAnnouncementPriority.Important));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("vpn-scan", "startup", ex);
        }
    }

    /// <summary>
    /// Поднимает сетевую цепочку сессии: транспорт сервера (если включён),
    /// затем Тор (если он в цепочке, включён релей или Режим Следа).
    /// Падение транспорта перестраивает Тора без мёртвого прокси.
    /// </summary>
    private static async Task StartNetworkChainAsync()
    {
        var settings = SettingsService.Current;
        try
        {
            if (settings.VlessEnabled &&
                Services.Vless.VlessProfile.TryParse(settings.VlessProfileUri,
                    out var profile, out _) && profile is not null)
            {
                var state = await Services.Vless.VlessRuntime.EnsureRunningAsync(profile);
                CrashReportService.AddBreadcrumb("startup", "vless-" + state);
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("vless", "startup", ex);
        }

        // Транспорт упал по ходу сессии — Тор перегенерирует torrc без
        // мёртвого Socks5Proxy и продолжит через VPN или в ожидании.
        Services.Vless.VlessRuntime.TransportLost += () =>
            _ = Task.Run(async () =>
            {
                try
                {
                    var current = SettingsService.Current;
                    if (current.TorInChain || current.TorRelayEnabled || current.TrailModeEnabled)
                    {
                        await Services.Tor.TorBridgeManager.RestartWithBridgesAsync(current);
                        Ui.Post(() => Services.VoiceAssistantService.Announce(
                            "Транспорт сервера упал. Маршрут переключился на системный туннель или ждёт.",
                            Services.VoiceAnnouncementPriority.Important));
                    }
                }
                catch (Exception ex)
                {
                    CrashReportService.RecordNonFatal("vless", "transport-lost-rewrap", ex);
                }
            });

        if (settings.TorInChain || settings.TorRelayEnabled || settings.TrailModeEnabled)
            await Views.MainWindow.StartTorAndRelayOnceAsync();
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
