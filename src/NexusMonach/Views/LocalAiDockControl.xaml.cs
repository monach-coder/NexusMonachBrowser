using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Windows.Threading;
using System.Diagnostics;
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
    private readonly DispatcherTimer _devHelpTimer;
    private string? _pendingDevHelp;
    private IReadOnlyList<NexusResearchDocument> _lastResearchDocuments = [];
    private IReadOnlyList<string> _lastResearchNotes = [];
    private string _lastResearchQuery = string.Empty;
    private string? _shoppingImagePath;
    private readonly Dictionary<string, NexusSearchReport> _backgroundResearch =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _showingBackgroundResearch;
    private string _backgroundResearchHost = string.Empty;
    private readonly List<string> _backgroundResearchStages = [];

    public LocalAiDockControl()
    {
        InitializeComponent();
        _devHelpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _devHelpTimer.Tick += DevHelpTimer_Tick;
    }

    public async Task ShowForTabAsync(BrowserTab tab)
    {
        Visibility = Visibility.Visible;
        if (!ReferenceEquals(_tab, tab) || !string.Equals(_pageUrl, tab.CurrentUrl, StringComparison.Ordinal))
        {
            _cancellation?.Cancel();
            _tab = tab;
            _pageUrl = tab.CurrentUrl;
            ResultBox.Text = "DevTools AI готовит диагностику текущей страницы…";
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
        _cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        var completed = 0;
        IReadOnlyList<TranslationSegment> segments = [];
        try
        {
            segments = await tab.CaptureTranslationSegmentsAsync();
            if (segments.Count == 0) throw new InvalidOperationException("На странице нет видимого текста для перевода.");
            await tab.BeginInPageTranslationAsync(segments.Count);
            // Tier 0 is visible immediately and never starts a model. Only the
            // unknown text goes to the generative correction pass.
            var immediate = LocalTranslationDictionary.TranslateKnown(segments);
            completed += await tab.ApplyTranslationSegmentsAsync(immediate, completed, segments.Count);
            var knownIds = immediate.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var pending = segments.Where(x => !knownIds.Contains(x.Id)).ToArray();
            await LocalIntelligenceService.TranslateSegmentsAsync(pending, _cancellation.Token,
                async translatedBatch =>
                {
                    completed += await tab.ApplyTranslationSegmentsAsync(
                        translatedBatch, completed, segments.Count);
                });
            if (completed == 0)
                throw new InvalidOperationException(
                    "Локальная модель не вернула ни одного проверенного русского фрагмента. Оригинал страницы сохранён.");
            await tab.CompleteInPageTranslationAsync(completed, segments.Count);
        }
        catch (OperationCanceledException)
        { await tab.CompleteInPageTranslationAsync(completed, segments.Count, "остановлен пользователем"); }
        catch (Exception ex)
        { await tab.CompleteInPageTranslationAsync(completed, segments.Count, ex.Message); }
    }

    public async Task TranslateVideoAudioAsync(BrowserTab tab)
    {
        Visibility = Visibility.Collapsed;
        _tab = tab;
        if (!AiModelCatalog.TranslationReady)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTranslationRuntimeMessage,
                "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopVideoTranslation();
        var session = new CancellationTokenSource();
        _videoCancellation = session;
        await tab.BeginLiveAudioTranslationAsync();
        var finalStatus = "Перевод звука остановлен.";
        try
        {
            await tab.UpdateLiveAudioTranslationStatusAsync("Один раз загружаю Whisper и Nexus OPUS…");
            await WhisperService.WarmUpAsync(session.Token);
            await tab.UpdateLiveAudioTranslationStatusAsync(
                "Слушаю системный звук непрерывно · русские субтитры включены");

            var lastSubtitle = string.Empty;
            var lastTranscript = string.Empty;
            var consecutiveErrors = 0;
            await using var audio = SystemAudioCaptureService.StartContinuousCapture(
                segmentMilliseconds: 5_500, overlapMilliseconds: 750);
            await foreach (var segment in audio.ReadSegmentsAsync(session.Token))
            {
                if (session.IsCancellationRequested || await tab.ShouldStopLiveAudioTranslationAsync()) break;
                try
                {
                    var speech = await NexusFabricRuntime.TranscribeSpeechDetailedAsync(segment.Wav, session.Token);
                    var transcript = RemoveTranscriptOverlap(lastTranscript, speech.Text);
                    if (string.IsNullOrWhiteSpace(transcript)) continue;
                    lastTranscript = speech.Text;

                    // Whisper только распознаёт исходный язык. В отличие от -tr,
                    // который умеет переводить лишь на английский, Nexus OPUS
                    // выполняет отдельный маршрут исходный язык -> русский.
                    var text = await LocalIntelligenceService.TranslateToRussianAsync(
                        transcript, session.Token, speech.Language);
                    if (!string.IsNullOrWhiteSpace(text) &&
                        !text.Equals(lastSubtitle, StringComparison.OrdinalIgnoreCase))
                    {
                        lastSubtitle = text;
                        await tab.ShowLiveVideoSubtitleAsync(text);
                    }
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    if (consecutiveErrors >= 3)
                        await tab.UpdateLiveAudioTranslationStatusAsync(
                            "Продолжаю слушать · последняя ошибка: " +
                            ex.Message[..Math.Min(ex.Message.Length, 140)]);
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
            Interlocked.CompareExchange(ref _videoCancellation, null, session);
            session.Dispose();
            try { await tab.EndLiveAudioTranslationAsync(finalStatus); } catch { }
        }
    }

    public void StopVideoTranslation() => Interlocked.Exchange(ref _videoCancellation, null)?.Cancel();

    private static string RemoveTranscriptOverlap(string previous, string current)
    {
        current = System.Text.RegularExpressions.Regex.Replace(current ?? string.Empty, @"\s+", " ").Trim();
        previous = System.Text.RegularExpressions.Regex.Replace(previous ?? string.Empty, @"\s+", " ").Trim();
        if (current.Length == 0 || previous.Length == 0) return current;
        if (previous.Equals(current, StringComparison.OrdinalIgnoreCase) ||
            previous.Contains(current, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var oldWords = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var newWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maximum = Math.Min(Math.Min(oldWords.Length, newWords.Length), 12);
        for (var overlap = maximum; overlap >= 2; overlap--)
        {
            var matches = true;
            for (var index = 0; index < overlap; index++)
                if (!oldWords[oldWords.Length - overlap + index].Equals(
                        newWords[index], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            if (matches) return string.Join(' ', newWords.Skip(overlap));
        }
        return current;
    }

    public async Task PrepareShoppingAgentAsync(BrowserTab tab)
    {
        _showingBackgroundResearch = false;
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "NEXUS СЛЕДОПЫТ";
        TestLocalAiButton.Visibility = Visibility.Collapsed;
        DeveloperAnalyzeButton.Visibility = Visibility.Collapsed;
        DeveloperGuidePanel.Visibility = Visibility.Collapsed;
        ShoppingAgentPanel.Visibility = Visibility.Visible;
        ShoppingQueryBox.Text = "Что нужно найти?";
        _shoppingImagePath = null;
        ShoppingImageNameText.Text = "Фото не выбрано";
        if (_backgroundResearch.TryGetValue(tab.CurrentUrl, out var research))
            ShowTextResult(FormatBackgroundResearch(research) +
                "\n\nНиже можно отдельно найти товары в каталоге этого сайта.");
        else
            ShowTextResult("Введите описание товара или выберите фотографию, затем нажмите «Начать поиск». Nexus просмотрит до пяти страниц текущего сайта и покажет 3–5 наиболее подходящих карточек. Корзина, вход и оформление заказа не затрагиваются.");
        StatusText.Text = NexusFabricRuntime.IsAvailable
            ? "Nexus Intelligence Fabric готов к поиску."
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
        DeveloperAnalyzeButton.Visibility = Visibility.Collapsed;
        DeveloperGuidePanel.Visibility = Visibility.Collapsed;
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
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White,
            FontSize = 13.5, Margin = new Thickness(2, 0, 2, 12)
        });
        foreach (var item in report.Items.Take(6))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = item.Title, TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
            });
            content.Children.Add(new TextBlock
            {
                Text = item.Answer, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(200, 205, 213))
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
                Background = new SolidColorBrush(Color.FromArgb(105, 16, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 9), Child = content
            });
        }
        ShoppingCardsScroll.ScrollToHome();
    }

    public async Task AnalyzeDeveloperContextAsync(BrowserTab tab)
    {
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "DEVTOOLS AI";
        TestLocalAiButton.Visibility = Visibility.Visible;
        ShoppingAgentPanel.Visibility = Visibility.Collapsed;
        DeveloperAnalyzeButton.Visibility = Visibility.Visible;
        DeveloperGuidePanel.Visibility = Visibility.Visible;
        await RunDeveloperAnalysisAsync();
    }

    public async Task ShowSearchFollowUpAsync(BrowserTab tab, string query)
    {
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "NEXUS · ИССЛЕДОВАТЕЛЬ";
        DeveloperAnalyzeButton.Visibility = Visibility.Collapsed;
        DeveloperGuidePanel.Visibility = Visibility.Collapsed;
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
        if (_tab is null || UrlService.IsInternal(_tab.CurrentUrl))
        { StatusText.Text = "Открой сайт магазина или каталога."; return; }
        var query = ShoppingQueryBox.Text.Trim();
        if (query.StartsWith("Что нужно", StringComparison.Ordinal)) query = string.Empty;
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(_shoppingImagePath))
        { StatusText.Text = "Введите описание товара или выберите фотографию."; return; }
        if (_model is null) { await EnsureModelAsync(); if (_model is null) return; }
        _cancellation?.Cancel(); _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true; ResultBox.Clear();
        var stopwatch = Stopwatch.StartNew();
        var rawCount = 0;
        var pagesViewed = 0;
        SledopytDiagnosticsService.Record("shopping", "started", "success");
        CrashReportService.AddBreadcrumb("sledopyt", "shopping-started");
        try
        {
            if (!string.IsNullOrWhiteSpace(_shoppingImagePath))
            {
                StatusText.Text = "Nexus Vision локально распознаёт товар на фото…";
                var imageInfo = new FileInfo(_shoppingImagePath);
                if (!imageInfo.Exists || imageInfo.Length > 20 * 1024 * 1024)
                    throw new InvalidOperationException("Изображение не найдено или превышает безопасный лимит 20 МБ.");
                var imageAnswer = await NexusFabricRuntime.UnderstandImageAsync(
                    await File.ReadAllBytesAsync(_shoppingImagePath, _cancellation.Token), _cancellation.Token);
                using var imageDocument = JsonDocument.Parse(LocalIntelligenceService.ExtractJson(imageAnswer));
                var imageQuery = imageDocument.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
                if (string.IsNullOrWhiteSpace(imageQuery))
                    throw new InvalidOperationException("Nexus Vision не смог составить запрос по фотографии.");
                query = string.IsNullOrWhiteSpace(query) ? imageQuery : query + ". По фотографии: " + imageQuery;
                ShoppingQueryBox.Text = query;
            }
            StatusText.Text = "Nexus Следопыт находит каталог и ждёт обновления DOM…";
            var searched = await _tab.SearchCurrentSiteForAgentAsync(query, _cancellation.Token);
            if (!searched)
                StatusText.Text = "Поле поиска не найдено — анализирую открытый каталог и его страницы…";
            var rawItems = new List<string>();
            var itemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _tab.CurrentUrl };
            var pages = 0;
            var previewCount = 0;
            ShoppingReport? preview = null;
            void UpdatePreview()
            {
                if (rawItems.Count == 0 || rawItems.Count == previewCount) return;
                var json = "[" + string.Join(",", rawItems) + "]";
                var candidate = LocalIntelligenceService.BuildShoppingPreview(query, json);
                if (candidate.Items.Count == 0) return;
                preview = candidate;
                previewCount = rawItems.Count;
                ShowShoppingCards(candidate);
                StatusText.Text = $"Уже найдено карточек: {candidate.Items.Count} · продолжаю обход каталога…";
            }
            for (var page = 1; page <= 5 && rawItems.Count < 150; page++)
            {
                pages = page;
                pagesViewed = page;
                StatusText.Text = $"Сбор результатов: страница {page} из 5…";
                async Task AppendCurrentPageAsync()
                {
                    var json = await _tab.ExtractShoppingCardsAsync();
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
                await AppendCurrentPageAsync();
                UpdatePreview();
                for (var scrollRound = 0; scrollRound < 6 && rawItems.Count < 150; scrollRound++)
                {
                    var catalogChanged = await _tab.ScrollShoppingResultsAsync();
                    await AppendCurrentPageAsync();
                    UpdatePreview();
                    if (!catalogChanged) break;
                }
                var next = await _tab.GetNextShoppingPageUrlAsync();
                if (page == 5) break;
                if (string.IsNullOrWhiteSpace(next))
                {
                    StatusText.Text = $"Поиск кнопки следующей страницы после {page}…";
                    if (!await _tab.TryClickNextShoppingPageAsync()) break;
                    continue;
                }
                if (!visited.Add(next)) break;
                StatusText.Text = $"Переход к странице {page + 1}…";
                if (!await _tab.NavigateAndWaitAsync(next, TimeSpan.FromSeconds(20))) break;
                await Task.Delay(1200, _cancellation.Token);
            }
            var count = rawItems.Count;
            rawCount = count;
            if (count == 0)
            {
                StatusText.Text = "Локальный AI определяет, что показал сайт…";
                var visibleText = await _tab.GetReadablePageTextAsync();
                var diagnosis = await LocalIntelligenceService.DiagnoseAgentPageAsync(
                    query, _tab.Title, _tab.CurrentUrl, visibleText, _cancellation.Token);
                throw new InvalidOperationException("Карточки товаров не извлечены. " + diagnosis);
            }
            var cards = "[" + string.Join(",", rawItems) + "]";
            preview ??= LocalIntelligenceService.BuildShoppingPreview(query, cards);
            if (preview.Items.Count > 0) ShowShoppingCards(preview);
            StatusText.Text = $"Карточки готовы · уточняю итог среди {count} вариантов локально…";
            ShoppingReport report = preview;
            using (var rankingBudget = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
            {
                rankingBudget.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    report = await LocalIntelligenceService.AnalyzeShoppingResultsAsync(
                        query, _tab.CurrentHost, cards, rankingBudget.Token);
                }
                catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
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
            if (!_tab.IsPrivate)
                await KnowledgeGraphService.RecordShoppingResearchAsync(report, _cancellation.Token);
            ShowShoppingCards(report);
            StatusText.Text = $"Готово. Просмотрено страниц: {pages}; вариантов в выводе: {report.Items.Count}.";
            SledopytDiagnosticsService.Record("shopping", "completed", "success",
                stopwatch.ElapsedMilliseconds, rawCount, report.Items.Count, $"pages-{pagesViewed}");
            CrashReportService.AddBreadcrumb("sledopyt", "shopping-completed");
        }
        catch (OperationCanceledException)
        {
            SledopytDiagnosticsService.Record("shopping", "cancelled", "partial",
                stopwatch.ElapsedMilliseconds, rawCount, code: "user-or-timeout");
            StatusText.Text = "Сбор остановлен.";
        }
        catch (Exception ex)
        {
            SledopytDiagnosticsService.Record("shopping", "failed", "failed",
                stopwatch.ElapsedMilliseconds, rawCount, code: ClassifyShoppingFailure(ex));
            CrashReportService.AddBreadcrumb("sledopyt", "shopping-failed");
            ResultBox.Text = ex.Message;
            StatusText.Text = "Nexus Следопыт не собрал сравнение.";
        }
        finally { CancelButton.IsEnabled = false; }
    }

    private static string ClassifyShoppingFailure(Exception ex) => ex switch
    {
        TimeoutException => "timeout",
        JsonException => "invalid-response",
        InvalidOperationException => "catalog-unavailable",
        _ => "operation-error"
    };

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
            Foreground = Brushes.White,
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
            content.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            content.Children.Add(new TextBlock { Text = $"Цена: {item.Price}   Рейтинг: {item.Rating}", Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(127, 245, 231)) });
            content.Children.Add(new TextBlock { Text = "Купили/отзывы: " + item.Buyers, Margin = new Thickness(0, 3, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(200, 205, 213)) });
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                var open = new Button { Content = "Открыть товар", Tag = item.Url, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
                open.Click += ShoppingProductOpen_Click;
                content.Children.Add(open);
            }
            ShoppingCardsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(105, 16, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), Child = content
            });
        }
        ShoppingCardsPanel.Children.Add(new TextBlock
        {
            Text = "ВЫВОД NEXUS AI\n" + report.Recommendation +
                   (string.IsNullOrWhiteSpace(report.Caveat) ? string.Empty : "\n\nОграничение: " + report.Caveat),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(3, 5, 3, 12)
        });
        ShoppingCardsScroll.ScrollToHome();
    }

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
            !Uri.TryCreate(_tab.CurrentUrl, UriKind.Absolute, out var current) ||
            !(target.Host.Equals(current.Host, StringComparison.OrdinalIgnoreCase) ||
              target.Host.EndsWith('.' + current.Host, StringComparison.OrdinalIgnoreCase) ||
              current.Host.EndsWith('.' + target.Host, StringComparison.OrdinalIgnoreCase))) return;
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

    private async void DeveloperAnalyze_Click(object sender, RoutedEventArgs e) => await RunDeveloperAnalysisAsync();

    private void DevTopic_MouseEnter(object sender, MouseEventArgs e)
    {
        _pendingDevHelp = (sender as FrameworkElement)?.Tag as string;
        _devHelpTimer.Stop();
        _devHelpTimer.Start();
    }

    private void DevTopic_MouseLeave(object sender, MouseEventArgs e)
    {
        _devHelpTimer.Stop();
        _pendingDevHelp = null;
    }

    private void DevHelpTimer_Tick(object? sender, EventArgs e)
    {
        _devHelpTimer.Stop();
        if (string.IsNullOrWhiteSpace(_pendingDevHelp)) return;
        DevHelpText.Text = _pendingDevHelp;
        DevHelpPopup.IsOpen = true;
    }

    private async void DeveloperQuestion_Click(object sender, RoutedEventArgs e) => await RunDeveloperQuestionAsync();

    private async void DeveloperQuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunDeveloperQuestionAsync();
    }

    private async Task RunDeveloperQuestionAsync()
    {
        if (_tab is null || string.IsNullOrWhiteSpace(DeveloperQuestionBox.Text)) return;
        if (_model is null) { await EnsureModelAsync(); if (_model is null) return; }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        try
        {
            StatusText.Text = "Nexus Fast Intelligence готовит маршрут по DevTools…";
            var context = await _tab.GetDeveloperContextAsync();
            var answer = await LocalIntelligenceService.AnswerDeveloperQuestionAsync(
                DeveloperQuestionBox.Text.Trim(), context, _cancellation.Token);
            var highlighted = await _tab.HighlightDeveloperSelectorsAsync(answer.Highlights);
            ResultBox.Text = answer.Summary + "\n\n" +
                             string.Join("\n", answer.Suggestions.Select((x, i) => $"{i + 1}. {x}")) +
                             (highlighted > 0 ? $"\n\nНа самой странице подсвечено элементов: {highlighted}." : string.Empty);
            ResultBox.ScrollToHome();
            StatusText.Text = "Подсказка готова. Nexus ничего не нажимал.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Подсказка остановлена."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Не удалось подготовить подсказку."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private async Task RunDeveloperAnalysisAsync()
    {
        if (_tab is null || UrlService.IsInternal(_tab.CurrentUrl))
        { StatusText.Text = "Открой обычную веб-страницу."; return; }
        if (_model is null) { await EnsureModelAsync(); if (_model is null) return; }
        _cancellation?.Cancel(); _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true; ResultBox.Clear();
        try
        {
            StatusText.Text = "Сбор безопасного DOM, консоли и сетевой сводки…";
            var context = await _tab.GetDeveloperContextAsync();
            if (string.IsNullOrWhiteSpace(context)) throw new InvalidOperationException("Нет DevTools-контекста.");
            StatusText.Text = "Nexus Fast Intelligence анализирует проблемы…";
            var analysis = await LocalIntelligenceService.AnalyzeDeveloperContextAsync(context, _cancellation.Token);
            var highlighted = await _tab.HighlightDeveloperSelectorsAsync(analysis.Highlights);
            ResultBox.Text = analysis.Summary + "\n\n" +
                             string.Join("\n", analysis.Suggestions.Select((x, i) => $"{i + 1}. {x}")) +
                             (analysis.Highlights.Count == 0 ? string.Empty : "\n\nПодсветка:\n" +
                              string.Join("\n", analysis.Highlights.Select(x => $"{x.Selector} — {x.Reason}")));
            ResultBox.ScrollToHome();
            StatusText.Text = $"Анализ готов. Подсвечено элементов: {highlighted}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "DevTools-анализ остановлен."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Ошибка DevTools AI."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _showingBackgroundResearch = false;
        Visibility = Visibility.Collapsed;
    }

}
