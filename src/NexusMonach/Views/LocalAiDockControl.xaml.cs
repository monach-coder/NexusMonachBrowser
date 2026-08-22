using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Win32;
using NexusMonach.Intelligence;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class LocalAiDockControl : UserControl
{
    private BrowserTab? _tab;
    private string? _model;
    private string? _pageUrl;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _videoCancellation;
    private IReadOnlyList<NexusResearchDocument> _lastResearchDocuments = [];
    private IReadOnlyList<string> _lastResearchNotes = [];
    private string _lastResearchQuery = string.Empty;
    private string? _shoppingImagePath;
    private bool _shoppingAllowsCrossSiteResults;
    private readonly Dictionary<string, NexusSearchReport> _backgroundResearch =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _showingBackgroundResearch;
    private string _backgroundResearchHost = string.Empty;
    private readonly List<string> _backgroundResearchStages = [];

    public LocalAiDockControl()
    {
        InitializeComponent();
    }

    public async Task ShowForTabAsync(BrowserTab tab)
    {
        Visibility = Visibility.Visible;
        if (!ReferenceEquals(_tab, tab) || !string.Equals(_pageUrl, tab.CurrentUrl, StringComparison.Ordinal))
        {
            _cancellation?.Cancel();
            _tab = tab;
            _pageUrl = tab.CurrentUrl;
            ResultBox.Text = "Nexus Следопыт готовит локальный анализ текущей страницы…";
        }
        PageTitleText.Text = tab.Title + " · " + tab.CurrentHost;
        await EnsureModelAsync();
    }

    public void UpdateTab(BrowserTab? tab)
    {
        if (Visibility != Visibility.Visible || tab is null) return;
        HandleNavigation(tab);
        if (Visibility != Visibility.Visible) return;
        _ = ShowForTabAsync(tab);
    }

    public async Task TranslateCurrentPageAsync(BrowserTab tab)
    {
        StopVideoTranslation();
        VideoDubbingVoiceService.Stop();
        VoiceAssistantService.Announce("Перевожу интерфейс и готовлю озвучивание статьи.",
            VoiceAnnouncementPriority.Important, tab.IsPrivate);
        Visibility = Visibility.Collapsed;
        _tab = tab;
        _pageUrl = tab.CurrentUrl;
        if (!AiModelCatalog.TranslationReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTranslationRuntimeMessage,
                "Локальный перевод", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var completed = 0;
        var spokenFragments = 0;
        var translatedControls = 0;
        IReadOnlyList<TranslationSegment> articleSegments = [];
        IReadOnlyList<TranslationSegment> interactiveSegments = [];
        try
        {
            articleSegments = await tab.CaptureTranslationSegmentsAsync();
            interactiveSegments = await tab.CaptureInteractiveTranslationSegmentsAsync();
            _cancellation.CancelAfter(PageNarrationPolicy.SelectOperationTimeout(
                articleSegments.Sum(segment => segment.Text.Length), articleSegments.Count));
            if (articleSegments.Count == 0 && interactiveSegments.Count == 0)
                throw new InvalidOperationException(
                    "На странице не найдено основное содержание или элементы интерфейса для перевода.");
            await tab.BeginInPageTranslationAsync(articleSegments.Count + interactiveSegments.Count);

            // В DOM попадают только подписи полей, кнопки, меню и подсказки.
            // Значения input/textarea и содержимое статьи этот путь не меняет.
            foreach (var group in interactiveSegments.Chunk(12))
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var translated = await LocalIntelligenceService.TranslateSegmentsAsync(
                    group, _cancellation.Token);
                translatedControls += await tab.ApplyInteractiveTranslationSegmentsAsync(
                    translated, translatedControls, interactiveSegments.Count);
            }
            await tab.UpdateSpokenPageTranslationStatusAsync(
                spokenFragments, articleSegments.Count, translatedControls);

            // Основная статья переводится в памяти и отдаётся только женскому
            // голосу. Меню, реклама, боковые панели и формы были исключены ещё
            // при DOM-захвате в BrowserTab.
            foreach (var group in articleSegments.Chunk(8))
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var translated = await LocalIntelligenceService.TranslateSegmentsAsync(
                    group, _cancellation.Token);
                var translatedById = translated.ToDictionary(item => item.Id, StringComparer.Ordinal);
                var narration = group.Select(item => translatedById.TryGetValue(item.Id, out var ready)
                        ? ready.Text
                        : ContainsCyrillic(item.Text) ? item.Text : string.Empty)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                foreach (var speechChunk in PageNarrationPolicy.CreateSpeechChunks(narration))
                {
                    var spoken = await VoiceAssistantService.SpeakAndWaitAsync(
                        speechChunk, VoiceAnnouncementPriority.Important, tab.IsPrivate,
                        userInitiated: true, rateOverride: 0,
                        cancellationToken: _cancellation.Token);
                    if (!spoken) throw new InvalidOperationException(
                        "Женский голос Nexus не смог озвучить статью.");
                }
                spokenFragments += narration.Length;
                completed = spokenFragments + translatedControls;
                await tab.UpdateSpokenPageTranslationStatusAsync(
                    spokenFragments, articleSegments.Count, translatedControls);
            }

            completed = spokenFragments + translatedControls;
            if (completed == 0)
                throw new InvalidOperationException(
                    "Локальная модель не вернула проверенный русский текст. Страница сохранена без изменений.");
            await tab.CompleteSpokenPageTranslationAsync(
                spokenFragments, articleSegments.Count, translatedControls);
            VoiceAssistantService.Announce(
                "Озвучивание статьи завершено. Элементы интерфейса переведены.",
                VoiceAnnouncementPriority.Important, tab.IsPrivate);
        }
        catch (OperationCanceledException)
        {
            VoiceAssistantService.StopSpeaking();
            await tab.CompleteSpokenPageTranslationAsync(
                spokenFragments, articleSegments.Count, translatedControls,
                "остановлено пользователем");
            VoiceAssistantService.Announce("Озвучивание страницы остановлено.",
                VoiceAnnouncementPriority.Critical, tab.IsPrivate);
        }
        catch (Exception ex)
        {
            VoiceAssistantService.StopSpeaking();
            await tab.CompleteSpokenPageTranslationAsync(
                spokenFragments, articleSegments.Count, translatedControls, ex.Message);
            VoiceAssistantService.Announce("Озвучивание страницы не выполнено.",
                VoiceAnnouncementPriority.Critical, tab.IsPrivate);
        }
    }

    public async Task TranslateVideoAudioAsync(BrowserTab tab)
    {
        _cancellation?.Cancel();
        VoiceAssistantService.StopSpeaking();
        Visibility = Visibility.Collapsed;
        _tab = tab;
        _pageUrl = tab.CurrentUrl;
        if (!AiModelCatalog.TranslationReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTranslationRuntimeMessage,
                "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AiModelCatalog.SpeechReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingSpeechRuntimeMessage,
                "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AiModelCatalog.NeuralVoiceReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingNeuralVoiceMessage,
                "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopVideoTranslation();
        var session = new CancellationTokenSource();
        _videoCancellation = session;
        var configuredVideoMode = SettingsService.Current.VideoTranslationMode;
        var durationSeconds = await tab.GetActiveVideoDurationAsync();
        var videoMode = VideoDubbingPolicy.SelectEffectiveMode(
            configuredVideoMode, durationSeconds);
        await tab.BeginLiveAudioTranslationAsync();
        var finalStatus = "Перевод звука остановлен.";
        var profile = VideoDubbingPolicy.ForMode(videoMode);
        await using var diagnostics = new VideoDubbingDiagnosticLog(videoMode);
        IContinuousAudioCaptureSession? earlyLoopback = null;
        try
        {
            CrashReportService.AddBreadcrumb("video-translation", "warmup-started");
            await tab.UpdateLiveAudioTranslationStatusAsync(
                "Слушаю начало видео · параллельно прогреваю локальные модели…");

            // Capture starts before model warm-up. The old ordering let the video
            // continue for five to ten seconds and permanently lost its opening.
            var firstDirectStartedAt = DateTimeOffset.UtcNow;
            var firstDirectTask = tab.CaptureActiveVideoAudioAsync(
                profile.SegmentMilliseconds, session.Token,
                profile.SegmentOverlapMilliseconds);
            var earlyLoopbackTask = StartEarlyLoopbackAsync();
            var warmupTask = Task.WhenAll(
                WhisperService.WarmUpAsync(WhisperLane.Dubbing, session.Token),
                TranslationService.WarmUpForLiveVideoAsync(
                    includeAllSourceRoutes: false, cancellationToken: session.Token),
                VideoDubbingVoiceService.WarmUpAsync(session.Token));
            var firstDirect = await firstDirectTask;
            earlyLoopback = await earlyLoopbackTask;
            await warmupTask;
            CrashReportService.AddBreadcrumb("video-translation", "warmup-completed");
            await tab.EnableVideoDubbingMixAsync();
            await tab.UpdateLiveAudioTranslationStatusAsync(
                $"Слушаю видео · режим {VideoModeLabel(videoMode, videoMode != configuredVideoMode)} · готовлю запас Nexus Voice");

            var translationContext = new VideoSpeechTranslationContext(videoMode);
            var recentTranscripts = new RecentVideoPhraseGuard(
                capacity: profile.ContextPhrases, retentionSeconds: profile.ContextSeconds);
            // Дубли перевода подавляются только в коротком окне: повтор
            // распознавания одного звука приходит секундами позже, а честный
            // повтор говорящим («да», припев, акцент) обязан прозвучать.
            var recentTranslations = new RecentVideoPhraseGuard(
                capacity: profile.ContextPhrases, retentionSeconds: 15);
            var consecutiveErrors = 0;

            async Task<VideoSpeechTranslationText?> TranslateSegmentAsync(LiveAudioSegment segment)
            {
                if (!VideoDubbingPolicy.IsFresh(segment.CapturedAt, DateTimeOffset.UtcNow)) return null;
                var translated = await translationContext.TranslateAsync(segment, session.Token);
                if (translated is null) return null;
                var now = DateTimeOffset.UtcNow;
                if (!recentTranscripts.IsNovel(translated.Transcript, now)) return null;
                var text = translated.RussianText;
                if (string.IsNullOrWhiteSpace(text) ||
                    !recentTranslations.IsNovel(text, now)) return null;
                return translated;
            }

            async Task<VideoSpeechTranslationText?> TranslateSegmentSafelyAsync(LiveAudioSegment segment)
            {
                try
                {
                    var text = await TranslateSegmentAsync(segment);
                    consecutiveErrors = 0;
                    return text;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    if (consecutiveErrors >= 3)
                        await tab.UpdateLiveAudioTranslationStatusAsync(
                            "Продолжаю слушать · последняя ошибка: " +
                            ex.Message[..Math.Min(ex.Message.Length, 140)]);
                    return null;
                }
            }

            var silentDirectProbes = 0;
            var useLoopback = false;
            var useCapturedOpening = true;
            while (true)
            {
                if (!useCapturedOpening)
                {
                    firstDirectStartedAt = DateTimeOffset.UtcNow;
                    firstDirect = await tab.CaptureActiveVideoAudioAsync(
                        profile.SegmentMilliseconds, session.Token,
                        profile.SegmentOverlapMilliseconds);
                }
                useCapturedOpening = false;
                if (firstDirect.WaitingForPlayback)
                {
                    await tab.UpdateLiveAudioTranslationStatusAsync(
                        "Видео на паузе · прямой перевод начнётся вместе с воспроизведением");
                    if (await tab.ShouldStopLiveAudioTranslationAsync())
                        throw new OperationCanceledException(session.Token);
                    await Task.Delay(VideoDubbingPolicy.PlaybackProbeMilliseconds, session.Token);
                    continue;
                }
                if (VideoDubbingPolicy.IsSilentDirectCapture(
                        firstDirect.Success, firstDirect.WavBase64))
                {
                    if (earlyLoopback?.IsProcessIsolated == true)
                    {
                        useLoopback = true;
                        break;
                    }
                    silentDirectProbes++;
                    if (silentDirectProbes < VideoDubbingPolicy.DirectSilenceProbeLimit)
                    {
                        await tab.UpdateLiveAudioTranslationStatusAsync(
                            $"Проверяю аудиопоток видео · тишина {silentDirectProbes} / {VideoDubbingPolicy.DirectSilenceProbeLimit}");
                        continue;
                    }
                    useLoopback = true;
                }
                else if (!firstDirect.Success)
                    useLoopback = true;
                break;
            }
            if (VideoDubbingPolicy.HasUsableDirectAudio(
                    firstDirect.Success, firstDirect.WavBase64))
            {
                if (earlyLoopback is not null)
                {
                    await earlyLoopback.DisposeAsync();
                    earlyLoopback = null;
                }
                CrashReportService.AddBreadcrumb("video-translation", "direct-capture-selected");
                await tab.UpdateLiveAudioTranslationStatusAsync(
                    "Прямой перевод · распознавание не пропускает речь во время озвучки");
                var directSegments = Channel.CreateBounded<LiveAudioSegment>(new BoundedChannelOptions(
                    VideoDubbingPolicy.MaxBufferedSegments)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });
                if (!string.IsNullOrWhiteSpace(firstDirect.WavBase64))
                    directSegments.Writer.TryWrite(new LiveAudioSegment(
                        Convert.FromBase64String(firstDirect.WavBase64), firstDirectStartedAt,
                        TimeSpan.FromMilliseconds(profile.SegmentMilliseconds)));
                using var producerCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(session.Token);

                async Task ProduceDirectSegmentsAsync()
                {
                    var failures = 0;
                    var silentCaptures = 0;
                    try
                    {
                        while (!producerCancellation.IsCancellationRequested)
                        {
                            var capturedAt = DateTimeOffset.UtcNow -
                                             TimeSpan.FromMilliseconds(
                                                 profile.SegmentOverlapMilliseconds);
                            var captured = await tab.CaptureActiveVideoAudioAsync(
                                profile.SegmentMilliseconds, producerCancellation.Token,
                                profile.SegmentOverlapMilliseconds);
                            if (captured.WaitingForPlayback)
                            {
                                failures = 0;
                                await tab.UpdateLiveAudioTranslationStatusAsync(
                                    "Видео на паузе · перевод продолжится вместе с воспроизведением");
                                await Task.Delay(VideoDubbingPolicy.PlaybackProbeMilliseconds,
                                    producerCancellation.Token);
                                continue;
                            }
                            if (captured.Success)
                            {
                                failures = 0;
                                if (VideoDubbingPolicy.IsSilentDirectCapture(
                                        captured.Success, captured.WavBase64))
                                {
                                    if (++silentCaptures >= VideoDubbingPolicy.DirectSilenceProbeLimit)
                                    {
                                        useLoopback = true;
                                        break;
                                    }
                                    continue;
                                }
                                silentCaptures = 0;
                                await directSegments.Writer.WriteAsync(
                                    new LiveAudioSegment(
                                        Convert.FromBase64String(captured.WavBase64), capturedAt,
                                        TimeSpan.FromMilliseconds(profile.SegmentMilliseconds +
                                                                  profile.SegmentOverlapMilliseconds)),
                                    producerCancellation.Token);
                                continue;
                            }
                            if (++failures >= 3)
                            {
                                useLoopback = true;
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { directSegments.Writer.TryComplete(ex); return; }
                    directSegments.Writer.TryComplete();
                }

                var producer = ProduceDirectSegmentsAsync();
                await using var dubbingBuffer = new VideoDubbingBuffer(
                    profile, diagnostics, session.Token);
                try
                {
                    await foreach (var segment in directSegments.Reader.ReadAllAsync(session.Token))
                    {
                        if (await tab.ShouldStopLiveAudioTranslationAsync())
                        {
                            dubbingBuffer.Stop();
                            throw new OperationCanceledException(session.Token);
                        }
                        var translation = await TranslateSegmentSafelyAsync(segment);
                        if (translation is not null)
                            await dubbingBuffer.QueueAsync(translation);
                    }
                }
                finally
                {
                    producerCancellation.Cancel();
                    try { await producer; } catch { }
                    await dubbingBuffer.CompleteAsync();
                }
            }
            if (useLoopback)
            {
                CrashReportService.AddBreadcrumb("video-translation", "loopback-selected");
                // DRM и некоторые нестандартные плееры запрещают captureStream.
                // В резерве сначала пробуем process-loopback, который слышит
                // только WebView2. Endpoint-loopback никогда не управляет
                // воспроизведением видео: на время Kseniya приостанавливается
                // исключительно захват общего аудиоустройства.
                await tab.UpdateLiveAudioTranslationStatusAsync(
                    "Подключаю изолированный аудиопоток WebView2…");
                var audio = earlyLoopback ??
                            await SystemAudioCaptureService.StartPreferredContinuousCaptureAsync(
                                tab.WebViewProcessId,
                                segmentMilliseconds: profile.SegmentMilliseconds,
                                overlapMilliseconds: profile.SegmentOverlapMilliseconds,
                                cancellationToken: session.Token);
                earlyLoopback = null;
                await using (audio)
                {
                await tab.UpdateLiveAudioTranslationStatusAsync(audio.IsProcessIsolated
                    ? "Закадровый перевод · изолированный поток WebView2 · непрерывное видео"
                    : "Совместимый перевод · видео воспроизводится непрерывно");
                await using var capturedSegments =
                    audio.ReadSegmentsAsync(session.Token).GetAsyncEnumerator(session.Token);
                bool hasFirstSegment;
                try
                {
                    hasFirstSegment = await capturedSegments.MoveNextAsync().AsTask().WaitAsync(
                        TimeSpan.FromMilliseconds(
                            VideoDubbingPolicy.FirstLoopbackSegmentTimeoutMilliseconds),
                        session.Token);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException(
                        "Аудиопоток вкладки не передал звук. Проверьте воспроизведение и устройство вывода.");
                }
                if (!hasFirstSegment)
                    throw new InvalidOperationException("Аудиопоток вкладки завершился без звука.");

                if (audio.IsProcessIsolated)
                {
                    await using var dubbingBuffer = new VideoDubbingBuffer(
                        profile, diagnostics, session.Token);
                    try
                    {
                        do
                        {
                            if (await tab.ShouldStopLiveAudioTranslationAsync())
                            {
                                dubbingBuffer.Stop();
                                throw new OperationCanceledException(session.Token);
                            }
                            var segment = capturedSegments.Current;
                            var translation = await TranslateSegmentSafelyAsync(
                                new LiveAudioSegment(segment.Wav, segment.CapturedAt,
                                    TimeSpan.FromMilliseconds(profile.SegmentMilliseconds)));
                            if (translation is not null)
                                await dubbingBuffer.QueueAsync(translation);
                        }
                        while (await capturedSegments.MoveNextAsync());
                    }
                    finally
                    {
                        await dubbingBuffer.CompleteAsync();
                    }
                }
                else
                {
                    await using var dubbingBuffer = new VideoDubbingBuffer(
                        profile, diagnostics, session.Token,
                        beforeSpeaking: () =>
                        {
                            audio.SuspendForDubbing();
                            return Task.CompletedTask;
                        },
                        afterSpeaking: () =>
                        {
                            audio.Resume();
                            return Task.CompletedTask;
                        });
                    try
                    {
                        do
                        {
                            if (await tab.ShouldStopLiveAudioTranslationAsync())
                            {
                                dubbingBuffer.Stop();
                                throw new OperationCanceledException(session.Token);
                            }
                            var segment = capturedSegments.Current;
                            var translation = await TranslateSegmentSafelyAsync(
                                new LiveAudioSegment(segment.Wav, segment.CapturedAt,
                                    TimeSpan.FromMilliseconds(profile.SegmentMilliseconds)));
                            if (translation is not null)
                                await dubbingBuffer.QueueAsync(translation);
                        }
                        while (await capturedSegments.MoveNextAsync());
                    }
                    finally { await dubbingBuffer.CompleteAsync(); }
                }
                }
            }

            async Task<IContinuousAudioCaptureSession?> StartEarlyLoopbackAsync()
            {
                try
                {
                    return await SystemAudioCaptureService.StartPreferredContinuousCaptureAsync(
                        tab.WebViewProcessId,
                        segmentMilliseconds: profile.SegmentMilliseconds,
                        overlapMilliseconds: profile.SegmentOverlapMilliseconds,
                        cancellationToken: session.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    CrashReportService.RecordNonFatal("video-translation",
                        "early-loopback-unavailable", ex);
                    return null;
                }
            }
        }
        catch (OperationCanceledException) { finalStatus = "Перевод звука остановлен."; }
        catch (Exception ex)
        {
            if (ReferenceEquals(Volatile.Read(ref _videoCancellation), session))
                finalStatus = "Ошибка перевода: " + ex.Message;
        }
        finally
        {
            if (earlyLoopback is not null)
                try { await earlyLoopback.DisposeAsync(); } catch { }
            VideoDubbingVoiceService.Stop();
            Interlocked.CompareExchange(ref _videoCancellation, null, session);
            session.Dispose();
            try { await tab.ResumeVideoAfterSpokenTranslationAsync(); } catch { }
            try { await tab.EndLiveAudioTranslationAsync(finalStatus); } catch { }
        }
    }

    public void StopVideoTranslation() => Interlocked.Exchange(ref _videoCancellation, null)?.Cancel();

    /// <summary>
    /// Двухпроходный дубляж как у профессиональной закадровой студии: сначала
    /// фильм прогоняется на адаптивно ускоренной скорости и целиком
    /// переводится целыми предложениями, затем видео возвращается в начало и
    /// идёт с мгновенным синхронным дубляжом по таймкоду — без задержки
    /// перевода и без потерь смысла от спешки.
    /// </summary>
    public async Task PrecomputeVideoDubbingAsync(BrowserTab tab)
    {
        _cancellation?.Cancel();
        VoiceAssistantService.StopSpeaking();
        Visibility = Visibility.Collapsed;
        _tab = tab;
        _pageUrl = tab.CurrentUrl;
        if (!AiModelCatalog.TranslationReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTranslationRuntimeMessage,
                "Предперевод фильма", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AiModelCatalog.SpeechReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingSpeechRuntimeMessage,
                "Предперевод фильма", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AiModelCatalog.NeuralVoiceReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingNeuralVoiceMessage,
                "Предперевод фильма", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopVideoTranslation();
        var session = new CancellationTokenSource();
        _videoCancellation = session;
        var finalStatus = "Предперевод остановлен.";
        var allWavs = new List<string>();
        PrecomputedDubbingPlayer? player = null;
        try
        {
            var duration = await tab.GetActiveVideoDurationAsync()
                         ?? throw new InvalidOperationException(
                                "Не удалось определить длительность видео. Откройте видео и попробуйте снова.");
            var profile = VideoDubbingPolicy.ForPrecompute();
            await tab.BeginLiveAudioTranslationAsync();
            await tab.PrepareVideoForAnalysisAsync();
            await tab.EnableVideoDubbingMixAsync();
            await tab.UpdateLiveAudioTranslationStatusAsync(
                "Прогреваю локальные модели…");
            await Task.WhenAll(
                WhisperService.WarmUpAsync(WhisperLane.Dubbing, session.Token),
                TranslationService.WarmUpForLiveVideoAsync(
                    includeAllSourceRoutes: false, cancellationToken: session.Token),
                VideoDubbingVoiceService.WarmUpAsync(session.Token));
            CrashReportService.AddBreadcrumb("video-translation", "precompute-analysis-started");

            var phrases = await AnalyzeVideoAsync(tab, profile, duration, allWavs, session.Token);
            if (phrases.Count == 0)
                throw new InvalidOperationException(
                    "Речь в видео не распознана. Проверьте, что звук включён.");
            phrases.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));

            await tab.RestartVideoForDubbedPlaybackAsync();
            await tab.UpdateLiveAudioTranslationStatusAsync(
                $"Синхронный дубляж · {phrases.Count} реплик · приятного просмотра");
            CrashReportService.AddBreadcrumb("video-translation", "precompute-playback-started");
            player = new PrecomputedDubbingPlayer();
            await PlayPrecomputedAsync(tab, phrases, player, session.Token);
            finalStatus = "Синхронный дубляж завершён.";
        }
        catch (OperationCanceledException) { finalStatus = "Предперевод остановлен."; }
        catch (Exception ex)
        {
            if (ReferenceEquals(Volatile.Read(ref _videoCancellation), session))
                finalStatus = "Предперевод прерван: " +
                              ex.Message[..Math.Min(ex.Message.Length, 160)];
            CrashReportService.RecordNonFatal("video-translation", "precompute", ex);
        }
        finally
        {
            player?.Dispose();
            foreach (var wav in allWavs)
                try { File.Delete(wav); } catch { }
            VideoDubbingVoiceService.Stop();
            Interlocked.CompareExchange(ref _videoCancellation, null, session);
            session.Dispose();
            try { await tab.EndLiveAudioTranslationAsync(finalStatus); } catch { }
        }
    }

    private async Task<List<PrecomputedDubbingPhrase>> AnalyzeVideoAsync(
        BrowserTab tab, VideoDubbingModeProfile profile, double durationSeconds,
        List<string> allWavs, CancellationToken cancellationToken)
    {
        var phrases = new List<PrecomputedDubbingPhrase>();
        var context = new VideoSpeechTranslationContext(profile);
        var analysisRate = 1.0;
        var throughputEma = 1.0;
        var lastPosition = -1.0;
        var consecutiveSilence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = await tab.GetVideoStateAsync();
            if (state is null) break;
            if (state.Ended) break;
            if (await tab.ShouldStopLiveAudioTranslationAsync())
                throw new OperationCanceledException(cancellationToken);
            if (state.Paused)
            {
                await tab.UpdateLiveAudioTranslationStatusAsync(
                    "Пауза · предперевод продолжится вместе с видео");
                await Task.Delay(300, cancellationToken);
                continue;
            }
            // Перемотка назад перезапускает накопление фразы: старые реплики
            // остаются, playback-планировщик отсортирует их по таймкоду.
            if (state.Position < lastPosition - 1.5)
                context = new VideoSpeechTranslationContext(profile);
            lastPosition = state.Position;

            var processingStopwatch = Stopwatch.StartNew();
            var captured = await tab.CaptureActiveVideoAudioAsync(
                profile.SegmentMilliseconds, cancellationToken,
                profile.SegmentOverlapMilliseconds);
            if (captured.WaitingForPlayback) continue;
            if (!captured.Success || string.IsNullOrWhiteSpace(captured.WavBase64))
            {
                if (++consecutiveSilence >= 6 && analysisRate > 4)
                {
                    // Некоторые плееры глушат звук на большой скорости — сбрасываем.
                    analysisRate = 4;
                    await tab.SetVideoAnalysisRateAsync(analysisRate);
                    consecutiveSilence = 0;
                }
                continue;
            }
            consecutiveSilence = 0;
            var segment = new LiveAudioSegment(
                Convert.FromBase64String(captured.WavBase64), DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(profile.SegmentMilliseconds));
            VideoSpeechTranslationText? translated = null;
            try
            {
                translated = await context.TranslateAsync(segment, cancellationToken,
                    captured.VideoPosition);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("video-translation",
                    "precompute-translate", ex);
            }
            if (translated is not null && !double.IsNaN(translated.VideoStartedAt))
            {
                var wavs = new List<string>();
                foreach (var chunk in VideoDubbingPolicy.SplitTtsText(
                             translated.RussianText, profile))
                {
                    var speech = await VideoDubbingVoiceService.PrepareAsync(chunk, 0,
                        cancellationToken);
                    wavs.Add(speech.Path);
                    allWavs.Add(speech.Path);
                }
                if (wavs.Count > 0)
                    phrases.Add(new PrecomputedDubbingPhrase(
                        translated.VideoStartedAt, translated.VideoEndedAt, wavs,
                        translated.RussianText));
            }

            processingStopwatch.Stop();
            var audioSeconds = profile.SegmentMilliseconds / 1000.0;
            var instantThroughput = processingStopwatch.ElapsedMilliseconds > 0
                ? audioSeconds / (processingStopwatch.ElapsedMilliseconds / 1000.0)
                : 2.0;
            throughputEma = throughputEma * 0.7 + instantThroughput * 0.3;
            var targetRate = Math.Clamp(throughputEma * 0.85, 1,
                VideoDubbingPolicy.MaximumAnalysisRate);
            if (Math.Abs(targetRate - analysisRate) > 0.4)
            {
                analysisRate = targetRate;
                await tab.SetVideoAnalysisRateAsync(analysisRate);
            }
            var progress = Math.Clamp(state.Position / durationSeconds, 0, 1);
            await tab.UpdateLiveAudioTranslationStatusAsync(
                $"Предперевод {(int)(progress * 100)}% · скорость ×{analysisRate:F1} · реплик: {phrases.Count}");
        }
        return phrases;
    }

    private static async Task PlayPrecomputedAsync(
        BrowserTab tab, IReadOnlyList<PrecomputedDubbingPhrase> phrases,
        PrecomputedDubbingPlayer player, CancellationToken cancellationToken)
    {
        var next = 0;
        var paused = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = await tab.GetVideoStateAsync();
            if (state is null || state.Ended) break;
            if (await tab.ShouldStopLiveAudioTranslationAsync()) break;
            if (state.Paused)
            {
                if (!paused)
                {
                    player.Pause();
                    paused = true;
                    await tab.UpdateLiveAudioTranslationStatusAsync("Пауза");
                }
                await Task.Delay(250, cancellationToken);
                continue;
            }
            if (paused)
            {
                player.Resume();
                paused = false;
            }
            // Перемотка вперёд пропускает реплики, целиком оставшиеся позади.
            while (next < phrases.Count && phrases[next].EndSeconds < state.Position - 0.5)
                next++;
            if (next < phrases.Count &&
                phrases[next].StartSeconds <= state.Position + 0.12 &&
                !player.IsPlaying)
            {
                player.Enqueue(phrases[next].WavPaths);
                next++;
            }
            await Task.Delay(120, cancellationToken);
        }
    }

    private static string VideoModeLabel(VideoTranslationMode mode, bool automatic = false) =>
        (mode switch
    {
        VideoTranslationMode.Fast => "Быстрый",
        VideoTranslationMode.Quality => "Качественный",
        _ => "Сбалансированный"
    }) + (automatic ? " · авто по длительности" : string.Empty);

    private static bool ContainsCyrillic(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Any(character => character is >= '\u0400' and <= '\u04FF');

    public async Task PrepareShoppingAgentAsync(BrowserTab tab)
    {
        _showingBackgroundResearch = false;
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "NEXUS СЛЕДОПЫТ";
        TestLocalAiButton.Visibility = Visibility.Collapsed;
        ShoppingAgentPanel.Visibility = Visibility.Visible;
        ShoppingQueryBox.Text = "Что нужно найти?";
        _shoppingImagePath = null;
        _shoppingAllowsCrossSiteResults = false;
        ShoppingImageNameText.Text = "Фото не выбрано";
        var surface = GetShoppingSurface(tab);
        if (_backgroundResearch.TryGetValue(tab.CurrentUrl, out var research))
            ShowTextResult(FormatBackgroundResearch(research) +
                "\n\nНиже можно отдельно найти товары в каталоге этого сайта.");
        else if (surface is "new-tab" or "search-provider")
            ShowTextResult("Введите товар и нажмите «Начать поиск» или Enter. Следопыт запросит настроенную поисковую машину, прочитает найденные страницы и локально соберёт сравнение. Ничего не покупается и не добавляется в корзину.");
        else
            ShowTextResult("Введите описание товара или выберите фотографию, затем нажмите «Начать поиск» или Enter. Следопыт сначала использует поиск этого сайта и просмотрит до пяти страниц. Если каталог не читается, он выполнит резервный поиск только по этому домену. Корзина, вход и оформление заказа не затрагиваются.");
        StatusText.Text = NexusFabricRuntime.IsAvailable
            ? surface is "new-tab" or "search-provider"
                ? "Режим: поиск через настроенную поисковую машину. Запуск — по кнопке или Enter."
                : "Режим: поиск и сравнение на текущем сайте. Запуск — по кнопке или Enter."
            : NexusFabricRuntime.Status.Message;
        ShoppingQueryBox.Focus();
        ShoppingQueryBox.SelectAll();
    }

    public void StoreBackgroundResearch(BrowserTab tab, string sourceUrl, NexusSearchReport report)
    {
        _backgroundResearch[sourceUrl] = report;
        if (!_showingBackgroundResearch || !IsSameSite(_backgroundResearchHost, tab.CurrentHost)) return;
        ShowBackgroundResearch(report);
        StatusText.Text = $"Готово · изучено материалов сайта: {report.Items.Count} · сохранено в граф знаний";
    }

    public void BeginBackgroundResearch(BrowserTab tab, string query)
    {
        _cancellation?.Cancel();
        _tab = tab;
        _pageUrl = tab.CurrentUrl;
        _lastResearchQuery = query;
        _showingBackgroundResearch = true;
        _backgroundResearchHost = tab.CurrentHost;
        _backgroundResearchStages.Clear();
        Visibility = Visibility.Visible;
        ModeTitleText.Text = "NEXUS СЛЕДОПЫТ";
        PageTitleText.Text = tab.Title + " · " + tab.CurrentHost;
        TestLocalAiButton.Visibility = Visibility.Collapsed;
        ShoppingAgentPanel.Visibility = Visibility.Collapsed;
        ShowTextResult("Ищу важную информацию по запросу:\n«" + query +
                       "»\n\n1. Читаю открытую страницу…\n2. Отбираю релевантные разделы этого сайта…\n3. Сопоставляю факты локально…");
        StatusText.Text = "Следопыт работает · текущая страница остаётся доступной";
    }

    public void UpdateBackgroundResearchProgress(BrowserTab tab, string message)
    {
        if (!_showingBackgroundResearch || !ReferenceEquals(_tab, tab) ||
            !IsSameSite(_backgroundResearchHost, tab.CurrentHost) || string.IsNullOrWhiteSpace(message)) return;
        message = message.Trim();
        if (_backgroundResearchStages.Count == 0 ||
            !_backgroundResearchStages[^1].Equals(message, StringComparison.Ordinal))
            _backgroundResearchStages.Add(message);
        if (_backgroundResearchStages.Count > 8) _backgroundResearchStages.RemoveAt(0);
        ShowTextResult("Следопыт продолжает исследование сайта…\n\n" +
                       string.Join("\n", _backgroundResearchStages.Select(x => "• " + x)));
        StatusText.Text = message;
    }

    public void FailBackgroundResearch(string message)
    {
        if (!_showingBackgroundResearch) return;
        ShowTextResult("Следопыт не завершил анализ:\n" + message);
        StatusText.Text = "Исследование не завершено.";
    }

    public void HandleNavigation(BrowserTab tab)
    {
        if (ReferenceEquals(_tab, tab) &&
            !string.IsNullOrWhiteSpace(_pageUrl) &&
            !string.Equals(_pageUrl, tab.CurrentUrl, StringComparison.Ordinal))
        {
            _cancellation?.Cancel();
            StopVideoTranslation();
            VideoDubbingVoiceService.Stop();
            VoiceAssistantService.StopSpeaking();
            _pageUrl = tab.CurrentUrl;
        }
        if (!_showingBackgroundResearch) return;
        if (ReferenceEquals(_tab, tab) && IsSameSite(_backgroundResearchHost, tab.CurrentHost)) return;
        _showingBackgroundResearch = false;
        Visibility = Visibility.Collapsed;
    }

    private static bool IsSameSite(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
         left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
         right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase));

    private static string FormatBackgroundResearch(NexusSearchReport report)
    {
        var lines = new List<string> { "ВЫЖИМКА СЛЕДОПЫТА", report.DirectAnswer };
        if (report.Items.Count > 0)
        {
            lines.Add("\nВАЖНОЕ НА ЭТОМ САЙТЕ");
            lines.AddRange(report.Items.Take(6).Select((item, index) =>
                $"{index + 1}. {item.Title}\n{item.Answer}\n{item.Url}"));
        }
        return string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private void ShowBackgroundResearch(NexusSearchReport report)
    {
        ResultBox.Visibility = Visibility.Collapsed;
        ShoppingCardsScroll.Visibility = Visibility.Visible;
        ShoppingCardsPanel.Children.Clear();
        ShoppingCardsPanel.Children.Add(new TextBlock
        {
            Text = "ВЫЖИМКА СЛЕДОПЫТА\n" + report.DirectAnswer,
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextBrush"),
            FontSize = 13.5, Margin = new Thickness(2, 0, 2, 12)
        });
        foreach (var item in report.Items.Take(6))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = item.Title, TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("TextBrush")
            });
            content.Children.Add(new TextBlock
            {
                Text = item.Answer, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Foreground = ThemeBrush("MutedTextBrush")
            });
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                var open = new Button
                {
                    Content = "Открыть раздел", Tag = item.Url, Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left
                };
                open.Click += ResearchSourceOpen_Click;
                content.Children.Add(open);
            }
            ShoppingCardsPanel.Children.Add(new Border
            {
                Background = ThemeBrush("PanelBrush"),
                BorderBrush = ThemeBrush("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 9), Child = content
            });
        }
        ShoppingCardsScroll.ScrollToHome();
    }

    public async Task ShowSearchFollowUpAsync(BrowserTab tab, string query)
    {
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "NEXUS · ИССЛЕДОВАТЕЛЬ";
        ShoppingAgentPanel.Visibility = Visibility.Collapsed;
        ShowTextResult("Анализирую выбранную страницу по исходному запросу…");
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        try
        {
            var pageText = await tab.GetReadablePageTextAsync();
            var answer = await LocalIntelligenceService.AnswerFromSelectedPageAsync(
                query, tab.Title, tab.CurrentUrl, pageText, _cancellation.Token);
            ShowTextResult(answer);
            StatusText.Text = "Выжимка по выбранному источнику готова. Анализ выполнен локально.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Анализ выбранной страницы остановлен."; }
        catch (Exception ex) { ShowTextResult(ex.Message); StatusText.Text = "Не удалось подготовить выжимку."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private async Task EnsureModelAsync()
    {
        StatusText.Text = "Проверка автономного AI-комплекта…";
        try
        {
            _model = await LocalAiService.GetPreferredModelAsync();
            ModelNameText.Text = _model ?? "AI-комплект неполный";
            ModelSetupPanel.Visibility = _model is null ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _model is null ? AiModelCatalog.ReadinessSummary :
                NexusFabricRuntime.ModelRoutingSummary;
        }
        catch (Exception ex)
        {
            _model = null;
            ModelNameText.Text = "AI-комплект недоступен";
            ModelSetupPanel.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
        }
    }

    private async void TestLocalAi_Click(object sender, RoutedEventArgs e)
    {
        await EnsureModelAsync();
        if (_model is null) return;
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancelButton.IsEnabled = true;
        try
        {
            StatusText.Text = "Проверка генерации — первый запуск модели может занять до минуты…";
            var answer = await NexusFabricRuntime.AskTextAsync(
                "Ответь строго JSON: {\"status\":\"ok\"}.", "Проверка локальной модели.", _cancellation.Token);
            using var document = JsonDocument.Parse(LocalIntelligenceService.ExtractJson(answer));
            ResultBox.Text = document.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok"
                ? $"Автономный AI работает. Модель {_model} отвечает локально."
                : "Модель ответила, но не выполнила тестовый формат: " + answer;
            StatusText.Text = "Проверка завершена.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ResultBox.Text = ex.Message;
            StatusText.Text = "Автономный AI не прошёл проверку.";
        }
        finally { CancelButton.IsEnabled = false; }
    }

    private async void ShoppingAgent_Click(object sender, RoutedEventArgs e) => await RunShoppingAgentAsync();

    private void ShoppingImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите фотографию или рисунок товара",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        _shoppingImagePath = dialog.FileName;
        ShoppingImageNameText.Text = "Фото: " + Path.GetFileName(dialog.FileName);
        StatusText.Text = "Фото выбрано. Нажмите «Начать поиск».";
    }

    private async void ShoppingQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunShoppingAgentAsync();
    }

    private void ShoppingQueryBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ShoppingQueryBox.Text.StartsWith("Что нужно", StringComparison.Ordinal)) ShoppingQueryBox.Clear();
    }

    private async Task RunShoppingAgentAsync()
    {
        var tab = _tab;
        var surface = tab is null ? "unknown" : GetShoppingSurface(tab);
        var runId = SledopytDiagnosticsService.Begin("shopping", "button-or-enter", surface);
        if (tab is null)
        {
            SledopytDiagnosticsService.Record("shopping", "blocked", "failed", code: "tab-unavailable",
                runId: runId, trigger: "button-or-enter", surface: surface);
            StatusText.Text = "Активная вкладка недоступна.";
            return;
        }
        var query = ShoppingQueryBox.Text.Trim();
        if (query.StartsWith("Что нужно", StringComparison.Ordinal)) query = string.Empty;
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(_shoppingImagePath))
        {
            SledopytDiagnosticsService.Record("shopping", "blocked", "failed", code: "missing-query",
                runId: runId, trigger: "button-or-enter", surface: surface);
            StatusText.Text = "Введите описание товара или выберите фотографию.";
            return;
        }
        if (_model is null)
        {
            await EnsureModelAsync();
            if (_model is null)
            {
                SledopytDiagnosticsService.Record("shopping", "blocked", "failed", code: "model-unavailable",
                    runId: runId, trigger: "button-or-enter", surface: surface);
                return;
            }
        }
        _cancellation?.Cancel();
        var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cancellation = operation;
        CancelButton.IsEnabled = true; ResultBox.Clear();
        var stopwatch = Stopwatch.StartNew();
        var rawCount = 0;
        var pagesViewed = 0;
        var globalSearch = surface is "new-tab" or "search-provider";
        _shoppingAllowsCrossSiteResults = globalSearch;
        SledopytDiagnosticsService.Record("shopping", "started", "success", runId: runId,
            trigger: "button-or-enter", surface: surface);
        CrashReportService.AddBreadcrumb("sledopyt", "shopping-started");
        VoiceAssistantService.Announce(globalSearch
                ? "Следопыт начал поиск товаров через поисковую машину."
                : "Следопыт начал анализ каталога.",
            VoiceAnnouncementPriority.Important, tab.IsPrivate);
        try
        {
            if (!string.IsNullOrWhiteSpace(_shoppingImagePath))
            {
                try
                {
                    StatusText.Text = "Nexus Vision локально распознаёт товар на фото…";
                    var imageInfo = new FileInfo(_shoppingImagePath);
                    if (!imageInfo.Exists || imageInfo.Length > 20 * 1024 * 1024)
                        throw new InvalidOperationException("Изображение не найдено или превышает безопасный лимит 20 МБ.");
                    var imageAnswer = await NexusFabricRuntime.UnderstandImageAsync(
                        await File.ReadAllBytesAsync(_shoppingImagePath, operation.Token), operation.Token);
                    using var imageDocument = JsonDocument.Parse(LocalIntelligenceService.ExtractJson(imageAnswer));
                    var imageQuery = imageDocument.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
                    if (string.IsNullOrWhiteSpace(imageQuery))
                        throw new InvalidOperationException("Nexus Vision не смог составить запрос по фотографии.");
                    query = string.IsNullOrWhiteSpace(query) ? imageQuery : query + ". По фотографии: " + imageQuery;
                    ShoppingQueryBox.Text = query;
                    SledopytDiagnosticsService.Record("shopping", "vision", "success",
                        stopwatch.ElapsedMilliseconds, resultCount: 1, runId: runId,
                        trigger: "button-or-enter", surface: surface);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var code = !AiModelCatalog.VisionReady ? "vision-unavailable" : "vision-failed";
                    SledopytDiagnosticsService.Record("shopping", "vision", "failed",
                        stopwatch.ElapsedMilliseconds, code: code, runId: runId,
                        trigger: "button-or-enter", surface: surface);
                    throw;
                }
            }
            var rawItems = new List<string>();
            var itemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pages = 0;
            var previewCount = 0;
            ShoppingReport? preview = null;
            string CardsJson() => "[" + string.Join(",", rawItems) + "]";
            void AppendCardsJson(string json)
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) return;
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var key = item.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString())
                            ? url.GetString()!
                            : item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(key) || !itemKeys.Add(key)) continue;
                        rawItems.Add(item.GetRawText());
                        if (rawItems.Count >= 150) break;
                    }
                }
                catch (JsonException) { }
            }
            void UpdatePreview()
            {
                if (rawItems.Count == 0 || rawItems.Count == previewCount) return;
                var candidate = LocalIntelligenceService.BuildShoppingPreview(query, CardsJson());
                if (candidate.Items.Count == 0) return;
                preview = candidate;
                previewCount = rawItems.Count;
                ShowShoppingCards(candidate);
                StatusText.Text = $"Уже найдено карточек: {candidate.Items.Count} · продолжаю обход каталога…";
            }

            if (globalSearch)
            {
                StatusText.Text = "Поисковая машина находит страницы товаров; Nexus читает их локально…";
                var progress = new Progress<string>(message => StatusText.Text = message);
                var discovery = await NexusSearchService.SearchShoppingAsync(query, null, progress,
                    operation.Token);
                AppendCardsJson(discovery.CardsJson);
                rawCount = rawItems.Count;
                pages = discovery.DiscoveryCount;
                pagesViewed = discovery.DiscoveryCount;
                SledopytDiagnosticsService.Record("shopping", "provider-search",
                    rawCount > 0 ? "success" : "failed", stopwatch.ElapsedMilliseconds,
                    discovery.DiscoveryCount, rawCount, rawCount > 0 ? "ok" : "no-cards",
                    runId, "button-or-enter", surface);
                UpdatePreview();
            }
            else
            {
                StatusText.Text = "Nexus Следопыт находит поиск сайта и ждёт обновления DOM…";
                var searched = await tab.SearchCurrentSiteForAgentAsync(query, operation.Token);
                SledopytDiagnosticsService.Record("shopping", "search-submit",
                    searched ? "success" : "partial", stopwatch.ElapsedMilliseconds,
                    code: searched ? "site-search-confirmed" : "search-field-not-found",
                    runId: runId, trigger: "button-or-enter", surface: surface);
                if (!searched)
                    StatusText.Text = "Поле поиска не найдено — читаю открытый каталог, затем проверю домен через поисковую машину…";
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tab.CurrentUrl };
                for (var page = 1; page <= 5 && rawItems.Count < 150; page++)
                {
                    pages = page;
                    pagesViewed = page;
                    StatusText.Text = $"Сбор результатов: страница {page} из 5…";
                    async Task AppendCurrentPageAsync()
                    {
                        AppendCardsJson(await tab.ExtractShoppingCardsAsync());
                    }
                    await AppendCurrentPageAsync();
                    UpdatePreview();
                    SledopytDiagnosticsService.Record("shopping", "page-extracted",
                        rawItems.Count > 0 ? "success" : "partial", stopwatch.ElapsedMilliseconds,
                        rawItems.Count, code: $"page-{page}", runId: runId,
                        trigger: "button-or-enter", surface: surface);
                    for (var scrollRound = 0; scrollRound < 6 && rawItems.Count < 150; scrollRound++)
                    {
                        var catalogChanged = await tab.ScrollShoppingResultsAsync();
                        await AppendCurrentPageAsync();
                        UpdatePreview();
                        if (!catalogChanged) break;
                    }
                    var next = await tab.GetNextShoppingPageUrlAsync();
                    if (page == 5) break;
                    if (string.IsNullOrWhiteSpace(next))
                    {
                        StatusText.Text = $"Поиск кнопки следующей страницы после {page}…";
                        if (!await tab.TryClickNextShoppingPageAsync()) break;
                        continue;
                    }
                    if (!visited.Add(next)) break;
                    StatusText.Text = $"Переход к странице {page + 1}…";
                    if (!await tab.NavigateAndWaitAsync(next, TimeSpan.FromSeconds(20))) break;
                    await Task.Delay(1200, operation.Token);
                }

                if (rawItems.Count == 0)
                {
                    StatusText.Text = "Карточки сайта не прочитаны — выполняю резервный поиск только по этому домену…";
                    var progress = new Progress<string>(message => StatusText.Text = message);
                    var discovery = await NexusSearchService.SearchShoppingAsync(query, tab.CurrentHost, progress,
                        operation.Token);
                    AppendCardsJson(discovery.CardsJson);
                    SledopytDiagnosticsService.Record("shopping", "site-fallback",
                        rawItems.Count > 0 ? "success" : "failed", stopwatch.ElapsedMilliseconds,
                        discovery.DiscoveryCount, rawItems.Count, rawItems.Count > 0 ? "ok" : "no-cards",
                        runId, "button-or-enter", surface);
                    UpdatePreview();
                }
            }
            var count = rawItems.Count;
            rawCount = count;
            if (count == 0)
            {
                var diagnosis = globalSearch
                    ? "Поисковая машина не вернула страниц, которые можно безопасно использовать как карточки товаров."
                    : "Ни DOM каталога, ни ограниченный поиск по домену не дали проверяемых карточек товаров.";
                throw new InvalidOperationException("Карточки товаров не извлечены. " + diagnosis);
            }
            var cards = CardsJson();
            preview ??= LocalIntelligenceService.BuildShoppingPreview(query, cards);
            if (preview.Items.Count > 0) ShowShoppingCards(preview);
            StatusText.Text = $"Карточки готовы · уточняю итог среди {count} вариантов локально…";
            SledopytDiagnosticsService.Record("shopping", "ranking", "success",
                stopwatch.ElapsedMilliseconds, count, preview.Items.Count, runId: runId,
                trigger: "button-or-enter", surface: surface);
            ShoppingReport report = preview;
            using (var rankingBudget = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
            {
                rankingBudget.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    report = await LocalIntelligenceService.AnalyzeShoppingResultsAsync(
                        query, globalSearch ? "поисковая машина" : tab.CurrentHost, cards, rankingBudget.Token);
                }
                catch (OperationCanceledException) when (!operation.IsCancellationRequested)
                {
                    // Deterministic cards are already visible; a slow optional
                    // recommendation must not make the search appear unfinished.
                    report = preview;
                }
            }
            if (report.Items.Count == 0)
                throw new InvalidOperationException(
                    "Каталог открыт, но карточек, связанных с запросом, не найдено. " +
                    "Следопыт не будет подменять результат несвязанными товарами.");
            if (!tab.IsPrivate)
                await KnowledgeGraphService.RecordShoppingResearchAsync(report, operation.Token);
            ShowShoppingCards(report);
            StatusText.Text = globalSearch
                ? $"Готово. Поиском изучено источников: {pages}; вариантов в выводе: {report.Items.Count}."
                : $"Готово. Просмотрено страниц: {pages}; вариантов в выводе: {report.Items.Count}.";
            SledopytDiagnosticsService.Record("shopping", "completed", "success",
                stopwatch.ElapsedMilliseconds, rawCount, report.Items.Count, $"pages-{pagesViewed}",
                runId, "button-or-enter", surface);
            CrashReportService.AddBreadcrumb("sledopyt", "shopping-completed");
            var top = report.Items.FirstOrDefault();
            VoiceAssistantService.Announce(
                $"Анализ каталога завершён. Найдено вариантов: {report.Items.Count}. " +
                (top is null ? string.Empty : $"Первый вариант: {top.Name}. Цена: {top.Price}."),
                VoiceAnnouncementPriority.Important, tab.IsPrivate);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            SledopytDiagnosticsService.Record("shopping", "cancelled", "partial",
                stopwatch.ElapsedMilliseconds, rawCount, code: "user-or-timeout", runId: runId,
                trigger: "button-or-enter", surface: surface);
            StatusText.Text = "Сбор остановлен.";
        }
        catch (OperationCanceledException)
        {
            SledopytDiagnosticsService.Record("shopping", "failed", "failed",
                stopwatch.ElapsedMilliseconds, rawCount, code: "stage-timeout", runId: runId,
                trigger: "button-or-enter", surface: surface);
            ResultBox.Text = "Один из сетевых этапов превысил лимит времени. Попробуйте повторить запрос или сменить поисковую систему.";
            StatusText.Text = "Nexus Следопыт не завершил сетевой этап.";
        }
        catch (Exception ex)
        {
            SledopytDiagnosticsService.Record("shopping", "failed", "failed",
                stopwatch.ElapsedMilliseconds, rawCount, code: ClassifyShoppingFailure(ex), runId: runId,
                trigger: "button-or-enter", surface: surface);
            CrashReportService.AddBreadcrumb("sledopyt", "shopping-failed");
            ResultBox.Text = ex.Message;
            StatusText.Text = "Nexus Следопыт не собрал сравнение.";
            VoiceAssistantService.Announce("Следопыт не смог собрать сравнение товаров.",
                VoiceAnnouncementPriority.Critical, tab.IsPrivate);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, operation)) _cancellation = null;
            operation.Dispose();
            CancelButton.IsEnabled = false;
        }
    }

    private static string GetShoppingSurface(BrowserTab tab)
    {
        if (tab.CurrentUrl.Equals(UrlService.NewTabUrl, StringComparison.OrdinalIgnoreCase)) return "new-tab";
        if (UrlService.IsSearchProviderUrl(tab.CurrentUrl) ||
            tab.CurrentUrl.StartsWith("https://nexus.local/search.html", StringComparison.OrdinalIgnoreCase))
            return "search-provider";
        return UrlService.IsInternal(tab.CurrentUrl) ? "new-tab" : "site";
    }

    private static string ClassifyShoppingFailure(Exception ex) => ex switch
    {
        TimeoutException => "timeout",
        HttpRequestException => "network",
        JsonException => "invalid-response",
        InvalidOperationException => "catalog-unavailable",
        _ => "operation-error"
    };

    private static async Task WaitForVoiceSilenceAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30 && VoiceAssistantService.IsBusy; attempt++)
            await Task.Delay(100, cancellationToken);
        await Task.Delay(120, cancellationToken);
    }

    private void ShowTextResult(string text)
    {
        ShoppingCardsScroll.Visibility = Visibility.Collapsed;
        ResultBox.Visibility = Visibility.Visible;
        ResultBox.Text = text;
        ResultBox.ScrollToHome();
    }

    private void ShowShoppingCards(ShoppingReport report)
    {
        ResultBox.Visibility = Visibility.Collapsed;
        ShoppingCardsScroll.Visibility = Visibility.Visible;
        ShoppingCardsPanel.Children.Clear();
        ShoppingCardsPanel.Children.Add(new TextBlock
        {
            Text = "Найдено вариантов: " + report.Items.Count,
            Foreground = ThemeBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 2, 9)
        });
        foreach (var item in report.Items.Take(5))
        {
            var content = new StackPanel();
            if (!string.IsNullOrWhiteSpace(item.Url) || !string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                var image = new Image
                {
                    Height = 130, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 9), Visibility = Visibility.Collapsed
                };
                content.Children.Add(image);
                _ = LoadShoppingImageAsync(image, item);
            }
            content.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("TextBrush") });
            content.Children.Add(new TextBlock { Text = $"Цена: {item.Price}   Рейтинг: {item.Rating}", Margin = new Thickness(0, 6, 0, 0), Foreground = ThemeBrush("AccentBrush") });
            content.Children.Add(new TextBlock { Text = "Купили/отзывы: " + item.Buyers, Margin = new Thickness(0, 3, 0, 0), Foreground = ThemeBrush("MutedTextBrush") });
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                var open = new Button { Content = "Открыть товар", Tag = item.Url, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
                open.Click += ShoppingProductOpen_Click;
                content.Children.Add(open);
            }
            ShoppingCardsPanel.Children.Add(new Border
            {
                Background = ThemeBrush("PanelBrush"),
                BorderBrush = ThemeBrush("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), Child = content
            });
        }
        ShoppingCardsPanel.Children.Add(new TextBlock
        {
            Text = "ВЫВОД NEXUS AI\n" + report.Recommendation +
                   (string.IsNullOrWhiteSpace(report.Caveat) ? string.Empty : "\n\nОграничение: " + report.Caveat),
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextBrush"), Margin = new Thickness(3, 5, 3, 12)
        });
        ShoppingCardsScroll.ScrollToHome();
    }

    private static Brush ThemeBrush(string key) =>
        (Brush)Application.Current.FindResource(key);

    private async Task LoadShoppingImageAsync(Image image, ShoppingCandidate item)
    {
        try
        {
            if (_tab is not null && !string.IsNullOrWhiteSpace(item.Url))
            {
                var bytes = await _tab.CaptureShoppingProductImageAsync(item.Url);
                if (bytes is { Length: > 0 })
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    var captured = new BitmapImage();
                    captured.BeginInit();
                    captured.CacheOption = BitmapCacheOption.OnLoad;
                    captured.DecodePixelWidth = 320;
                    captured.StreamSource = stream;
                    captured.EndInit();
                    captured.Freeze();
                    image.Source = captured;
                    image.Visibility = Visibility.Visible;
                    return;
                }
            }
            if (!Uri.TryCreate(item.ImageUrl, UriKind.Absolute, out var imageUri) ||
                imageUri.Scheme is not ("http" or "https")) return;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = imageUri;
            bitmap.DecodePixelWidth = 320;
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.EndInit();
            image.Source = bitmap;
            image.Visibility = Visibility.Visible;
        }
        catch { /* Ошибка миниатюры не должна скрывать карточку товара. */ }
    }

    private void ShoppingProductOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || _tab?.Core is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            !NexusSearchService.IsAllowedResultUrl(target.AbsoluteUri)) return;
        if (!_shoppingAllowsCrossSiteResults &&
            (!Uri.TryCreate(_tab.CurrentUrl, UriKind.Absolute, out var current) ||
             !(target.Host.Equals(current.Host, StringComparison.OrdinalIgnoreCase) ||
               target.Host.EndsWith('.' + current.Host, StringComparison.OrdinalIgnoreCase) ||
               current.Host.EndsWith('.' + target.Host, StringComparison.OrdinalIgnoreCase)))) return;
        if (!_tab.IsPrivate)
            _ = KnowledgeGraphService.RecordResearchChoiceAsync(ShoppingQueryBox.Text.Trim(), target.AbsoluteUri);
        _tab.Core.Navigate(target.AbsoluteUri);
    }

    private void ResearchSourceOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || _tab?.Core is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            !Uri.TryCreate(_tab.CurrentUrl, UriKind.Absolute, out var current) ||
            !(target.Host.Equals(current.Host, StringComparison.OrdinalIgnoreCase) ||
              target.Host.EndsWith('.' + current.Host, StringComparison.OrdinalIgnoreCase) ||
              current.Host.EndsWith('.' + target.Host, StringComparison.OrdinalIgnoreCase))) return;
        if (!_tab.IsPrivate && !string.IsNullOrWhiteSpace(_lastResearchQuery))
            _ = KnowledgeGraphService.RecordResearchChoiceAsync(_lastResearchQuery, target.AbsoluteUri);
        _tab.Core.Navigate(target.AbsoluteUri);
    }

    private async void DeepAnalysis_Click(object sender, RoutedEventArgs e) => await RunDeepAnalysisAsync();
    private async void DeepResearch_Click(object sender, RoutedEventArgs e) => await RunDeepResearchAsync();
    private async void AgentSummary_Click(object sender, RoutedEventArgs e) => await RunAgentSummaryAsync();

    private async Task RunDeepAnalysisAsync()
    {
        if (!TryPrepareFabricOperation(out var tab)) return;
        var query = GetAgentQuery("Подробно проанализируй эту страницу: ключевые факты, аргументы, ограничения и практический вывод.");
        BeginAgentOperation("Собираю очищенный текст текущей страницы…");
        var cancellation = _cancellation!;
        try
        {
            var document = await CaptureResearchDocumentAsync(tab, "s1", 1, cancellation.Token);
            if (document is null) throw new InvalidOperationException("На странице недостаточно читаемого текста для анализа.");
            _lastResearchDocuments = [document];
            _lastResearchQuery = query;
            StatusText.Text = "Nexus Intelligence Fabric выполняет глубокий анализ…";
            var response = await NexusFabricRuntime.ExecuteAsync(
                NexusFabricRequest.Create(NexusFabricOperations.DeepPageAnalysis,
                    new NexusDeepAnalysisRequest(query, document)), cancellation.Token);
            var summary = ReadFabricSummary(response);
            _lastResearchNotes = BuildAgentNotes(summary);
            ResultBox.Text = FormatAgentSummary(summary, _lastResearchDocuments);
            ResultBox.ScrollToHome();
            StatusText.Text = "Глубокий анализ завершён локально.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Глубокий анализ остановлен."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Глубокий анализ не завершён."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private async Task RunDeepResearchAsync()
    {
        if (!TryPrepareFabricOperation(out var tab)) return;
        var query = GetAgentQuery("Найди и сопоставь наиболее важную информацию по теме открытой страницы.");
        var startUrl = tab.CurrentUrl;
        BeginAgentOperation("Готовлю безопасный маршрут исследования…");
        var cancellation = _cancellation!;
        var documents = new List<NexusResearchDocument>();
        try
        {
            var first = await CaptureResearchDocumentAsync(tab, "s1", 1, cancellation.Token);
            if (first is not null) documents.Add(first);
            var links = await tab.GetResearchLinksAsync(query, 12);
            var sourceRank = 2;
            foreach (var link in links.Take(6))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                StatusText.Text = $"Углублённый поиск: источник {sourceRank} из {Math.Min(7, links.Count + 1)}…";
                if (!await tab.NavigateAndWaitAsync(link, TimeSpan.FromSeconds(20))) continue;
                await Task.Delay(700, cancellation.Token);
                var document = await CaptureResearchDocumentAsync(tab, $"s{sourceRank}", sourceRank, cancellation.Token);
                if (document is not null && documents.All(x => !x.Url.Equals(document.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    documents.Add(document);
                    sourceRank++;
                }
            }
            if (documents.Count == 0) throw new InvalidOperationException("Не удалось получить читаемые источники на текущем сайте.");
            _lastResearchDocuments = documents;
            _lastResearchQuery = query;
            StatusText.Text = $"Fabric сопоставляет {documents.Count} источников и ищет противоречия…";
            var response = await NexusFabricRuntime.ExecuteAsync(
                NexusFabricRequest.Create(NexusFabricOperations.DeepResearch,
                    new NexusDeepResearchRequest(query, documents, documents.Count)), cancellation.Token);
            var summary = ReadFabricSummary(response);
            _lastResearchNotes = BuildAgentNotes(summary);
            ResultBox.Text = FormatAgentSummary(summary, documents);
            ResultBox.ScrollToHome();
            StatusText.Text = $"Углублённый поиск завершён. Проверено источников: {documents.Count}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Углублённый поиск остановлен."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Углублённый поиск не завершён."; }
        finally
        {
            CancelButton.IsEnabled = false;
            var resultStatus = StatusText.Text;
            if (!string.Equals(tab.CurrentUrl, startUrl, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = resultStatus + " Возвращаю исходную страницу…";
                try
                {
                    var restored = await tab.NavigateAndWaitAsync(startUrl, TimeSpan.FromSeconds(20));
                    StatusText.Text = resultStatus + (restored
                        ? " Исходная страница восстановлена."
                        : " Не удалось автоматически вернуть исходную страницу.");
                }
                catch { StatusText.Text = resultStatus + " Не удалось автоматически вернуть исходную страницу."; }
            }
        }
    }

    private async Task RunAgentSummaryAsync()
    {
        if (!TryPrepareFabricOperation(out _)) return;
        if (_lastResearchDocuments.Count == 0)
        {
            ResultBox.Text = "Сначала выполните «Глубокий анализ» или «Углублённый поиск». Сводка использует только уже собранные локально материалы.";
            StatusText.Text = "Нет материалов для сводки.";
            return;
        }
        var query = GetAgentQuery(string.IsNullOrWhiteSpace(_lastResearchQuery) ? "Подготовь итоговую сводку." : _lastResearchQuery);
        BeginAgentOperation("Nexus Следопыт сводит выводы, источники, противоречия и пробелы…");
        var cancellation = _cancellation!;
        try
        {
            var response = await NexusFabricRuntime.ExecuteAsync(
                NexusFabricRequest.Create(NexusFabricOperations.AgentResearchSummary,
                    new NexusAgentSummaryRequest(query, _lastResearchDocuments, _lastResearchNotes)), cancellation.Token);
            var summary = ReadFabricSummary(response);
            _lastResearchQuery = query;
            _lastResearchNotes = BuildAgentNotes(summary);
            ResultBox.Text = FormatAgentSummary(summary, _lastResearchDocuments);
            ResultBox.ScrollToHome();
            StatusText.Text = "Сводка Nexus Следопыта готова.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Подготовка сводки остановлена."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Сводка Nexus Следопыта не подготовлена."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private bool TryPrepareFabricOperation(out BrowserTab tab)
    {
        tab = _tab!;
        if (_tab is null || UrlService.IsInternal(_tab.CurrentUrl))
        {
            StatusText.Text = "Откройте обычную веб-страницу.";
            return false;
        }
        if (!NexusFabricRuntime.IsAvailable)
        {
            ResultBox.Text = NexusFabricRuntime.Status.Message +
                "\n\nNexus Intelligence Fabric входит в открытый исходный код браузера. " +
                "Проверьте готовность локальных моделей и целостность установленной сборки.";
            StatusText.Text = "Открытый Fabric не инициализирован.";
            return false;
        }
        tab = _tab;
        return true;
    }

    private void BeginAgentOperation(string status)
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ResultBox.Clear();
        StatusText.Text = status;
    }

    private string GetAgentQuery(string fallback)
    {
        var query = ShoppingQueryBox.Text.Trim();
        return string.IsNullOrWhiteSpace(query) || query.StartsWith("Что ищем", StringComparison.Ordinal)
            ? fallback
            : query;
    }

    private static async Task<NexusResearchDocument?> CaptureResearchDocumentAsync(
        BrowserTab tab, string id, int sourceRank, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = (await tab.GetReadablePageTextAsync()).Trim();
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length < 80) return null;
        if (text.Length > 12000) text = text[..12000];
        return new NexusResearchDocument(id, tab.Title, tab.CurrentUrl, text, sourceRank);
    }

    private static NexusAgentSummary ReadFabricSummary(NexusFabricResponse response)
    {
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Fabric не вернул результат.");
        return response.ReadPayload<NexusAgentSummary>()
            ?? throw new InvalidOperationException("Fabric вернул повреждённую сводку.");
    }

    private static IReadOnlyList<string> BuildAgentNotes(NexusAgentSummary summary)
    {
        var notes = new List<string> { summary.Summary, summary.Recommendation };
        notes.AddRange(summary.Conflicts ?? []);
        notes.AddRange(summary.MissingInformation ?? []);
        return notes.Where(x => !string.IsNullOrWhiteSpace(x)).Take(24).ToArray();
    }

    private static string FormatAgentSummary(
        NexusAgentSummary summary, IReadOnlyList<NexusResearchDocument> documents)
    {
        var sourceMap = documents.ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string> { "СВОДКА NEXUS INTELLIGENCE FABRIC", summary.Summary };
        var findings = summary.Findings ?? [];
        var conflicts = summary.Conflicts ?? [];
        var missingInformation = summary.MissingInformation ?? [];
        if (findings.Count > 0)
        {
            lines.Add("\nКЛЮЧЕВЫЕ ВЫВОДЫ");
            var index = 1;
            foreach (var finding in findings)
            {
                var sources = string.Join(", ", (finding.SourceIds ?? [])
                    .Where(sourceMap.ContainsKey).Select(id => id.ToUpperInvariant()));
                lines.Add($"{index++}. {finding.Claim}\n   Уверенность: {finding.Confidence}" +
                          (string.IsNullOrWhiteSpace(sources) ? string.Empty : $" · Источники: {sources}"));
            }
        }
        if (conflicts.Count > 0)
            lines.Add("\nПРОТИВОРЕЧИЯ\n" + string.Join("\n", conflicts.Select(x => "• " + x)));
        if (missingInformation.Count > 0)
            lines.Add("\nЧЕГО НЕ ХВАТАЕТ\n" + string.Join("\n", missingInformation.Select(x => "• " + x)));
        if (!string.IsNullOrWhiteSpace(summary.Recommendation))
            lines.Add("\nИТОГ NEXUS СЛЕДОПЫТА\n" + summary.Recommendation);
        lines.Add("\nИСТОЧНИКИ");
        lines.AddRange(documents.Select(x => $"{x.Id.ToUpperInvariant()}. {x.Title}\n   {x.Url}"));
        lines.Add("\nNexus Следопыт ничего не вводил в формы, не авторизовывался и не совершал действий от имени пользователя.");
        return string.Join("\n", lines);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _showingBackgroundResearch = false;
        Visibility = Visibility.Collapsed;
    }

}
