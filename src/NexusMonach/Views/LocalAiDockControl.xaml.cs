using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class LocalAiDockControl : UserControl
{
    private BrowserTab? _tab;
    private string? _model;
    private string? _pageUrl;
    private CancellationTokenSource? _cancellation;
    private readonly DispatcherTimer _devHelpTimer;
    private string? _pendingDevHelp;

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
        _ = ShowForTabAsync(tab);
    }

    public async Task TranslateCurrentPageAsync(BrowserTab tab)
    {
        Visibility = Visibility.Collapsed;
        _tab = tab;
        _pageUrl = tab.CurrentUrl;
        await EnsureModelAsync();
        if (_model is null)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTextRuntimeMessage,
                "Локальный перевод", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var completed = 0;
        IReadOnlyList<TranslationSegment> segments = [];
        try
        {
            segments = await tab.CaptureTranslationSegmentsAsync();
            if (segments.Count == 0) throw new InvalidOperationException("На странице нет видимого текста для перевода.");
            await tab.BeginInPageTranslationAsync(segments.Count);
            foreach (var batch in segments.Chunk(12))
            {
                var translated = await LocalIntelligenceService.TranslateSegmentsAsync(batch, _cancellation.Token);
                completed += translated.Count;
                await tab.ApplyTranslationSegmentsAsync(translated, completed, segments.Count);
            }
            await tab.CompleteInPageTranslationAsync(completed, segments.Count);
        }
        catch (OperationCanceledException)
        { await tab.CompleteInPageTranslationAsync(completed, segments.Count, "остановлен пользователем"); }
        catch (Exception ex)
        { await tab.CompleteInPageTranslationAsync(completed, segments.Count, ex.Message); }
    }

    public async Task TranslateVideoSubtitlesAsync(BrowserTab tab)
    {
        Visibility = Visibility.Collapsed;
        _tab = tab;
        await EnsureModelAsync();
        if (_model is null)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTextRuntimeMessage,
                "Перевод видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        try
        {
            await tab.ShowVideoTranslationStatusAsync("Nexus ищет дорожку субтитров…");
            var source = await tab.CaptureVideoCaptionSegmentsAsync();
            if (source.Count == 0)
                throw new InvalidOperationException("У активного видео нет доступной дорожки субтитров. Для роликов без субтитров потребуется локальный Whisper.");
            var translatedById = new Dictionary<string, string>(StringComparer.Ordinal);
            var processed = 0;
            foreach (var batch in source.Chunk(80))
            {
                foreach (var item in await LocalIntelligenceService.TranslateSegmentsAsync(batch, _cancellation.Token))
                    translatedById[item.Id] = item.Text;
                processed += batch.Length;
                await tab.ShowVideoTranslationStatusAsync($"Перевод субтитров: {Math.Min(processed, source.Count)} / {source.Count}");
            }
            var captions = source.Where(x => translatedById.ContainsKey(x.Id)).Select(x => new VideoCaptionSegment
            {
                Id = x.Id, Text = translatedById[x.Id], Start = x.Start, End = x.End
            }).ToArray();
            if (captions.Length == 0) throw new InvalidOperationException("Локальная модель не вернула пригодные реплики.");
            await tab.ApplyRussianVideoCaptionsAsync(captions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tab.ShowVideoTranslationStatusAsync(ex.Message, warning: true);
            GlassDialogWindow.Show(ex.Message, "Перевод видео", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public async Task TranslateVideoAudioAsync(BrowserTab tab)
    {
        Visibility = Visibility.Collapsed;
        _tab = tab;
        await EnsureModelAsync();
        if (_model is null)
        {
            GlassDialogWindow.Show(AiModelCatalog.MissingTextRuntimeMessage,
                "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        await tab.BeginLiveAudioTranslationAsync();
        try
        {
            var progress = new Progress<string>(value => _ = tab.UpdateLiveAudioTranslationStatusAsync(value));
            var preparation = WhisperService.EnsureInstalledAsync(progress, _cancellation.Token);
            while (!preparation.IsCompleted)
            {
                await tab.UpdateLiveAudioTranslationStatusAsync(WhisperService.Status);
                await Task.Delay(1000, _cancellation.Token);
            }
            await preparation;
            var useSystemLoopback = false;
            while (!_cancellation.IsCancellationRequested && !await tab.ShouldStopLiveAudioTranslationAsync())
            {
                byte[] wav;
                if (!useSystemLoopback)
                {
                    await tab.UpdateLiveAudioTranslationStatusAsync("Слушаю аудиодорожку текущего видео…");
                    var capture = await tab.CaptureActiveVideoAudioAsync(7, _cancellation.Token);
                    if (capture.Success)
                        wav = Convert.FromBase64String(capture.WavBase64);
                    else
                    {
                        useSystemLoopback = true;
                        await tab.UpdateLiveAudioTranslationStatusAsync(
                            "Плеер не отдал дорожку. Слушаю системный выход Windows 7 секунд…");
                        wav = await SystemAudioCaptureService.CaptureWavAsync(7, _cancellation.Token);
                    }
                }
                else
                {
                    await tab.UpdateLiveAudioTranslationStatusAsync("Слушаю системный выход Windows 7 секунд…");
                    wav = await SystemAudioCaptureService.CaptureWavAsync(7, _cancellation.Token);
                }
                await tab.UpdateLiveAudioTranslationStatusAsync("Whisper распознаёт речь локально…");
                var transcript = await WhisperService.TranscribeAsync(wav, _cancellation.Token);
                if (string.IsNullOrWhiteSpace(transcript)) continue;
                await tab.UpdateLiveAudioTranslationStatusAsync("Nexus Fast Intelligence переводит реплику…");
                var translated = await LocalIntelligenceService.TranslateSegmentsAsync(
                    [new TranslationSegment { Id = "speech", Text = transcript }], _cancellation.Token);
                var text = translated.FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    await tab.ShowLiveVideoSubtitleAsync(text);
            }
            await tab.EndLiveAudioTranslationAsync("Перевод звука остановлен.");
        }
        catch (OperationCanceledException)
        { await tab.EndLiveAudioTranslationAsync("Перевод звука остановлен."); }
        catch (Exception ex)
        {
            await tab.EndLiveAudioTranslationAsync("Ошибка: " + ex.Message);
            GlassDialogWindow.Show(ex.Message, "Перевод звука видео", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public async Task PrepareShoppingAgentAsync(BrowserTab tab)
    {
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "AI · СРАВНЕНИЕ ТОВАРОВ";
        DeveloperAnalyzeButton.Visibility = Visibility.Collapsed;
        DeveloperGuidePanel.Visibility = Visibility.Collapsed;
        ShoppingAgentPanel.Visibility = Visibility.Visible;
        ShoppingQueryBox.Text = "Что ищем на этом сайте?";
        ResultBox.Text = "Агент использует поиск текущего сайта, соберёт видимые цены, рейтинги и число покупок. Корзина и оформление заказа не затрагиваются.";
        StatusText.Text = "Введи товар и нажми «Собрать».";
        ShoppingQueryBox.Focus();
        ShoppingQueryBox.SelectAll();
    }

    public async Task AnalyzeDeveloperContextAsync(BrowserTab tab)
    {
        await ShowForTabAsync(tab);
        ModeTitleText.Text = "DEVTOOLS AI";
        ShoppingAgentPanel.Visibility = Visibility.Collapsed;
        DeveloperAnalyzeButton.Visibility = Visibility.Visible;
        DeveloperGuidePanel.Visibility = Visibility.Visible;
        await RunDeveloperAnalysisAsync();
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
                "Автономный AI готов · " + AiModelCatalog.ReadinessSummary;
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
            var answer = await LocalAiService.AskAsync(_model,
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

    private async void ShoppingImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите фотографию или рисунок товара",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        if (_model is null) { await EnsureModelAsync(); if (_model is null) return; }
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        try
        {
            StatusText.Text = "Nexus Vision анализирует изображение…";
            var answer = await LocalAiService.DescribeImageForSearchAsync(
                _model, await File.ReadAllBytesAsync(dialog.FileName, _cancellation.Token), _cancellation.Token);
            using var document = JsonDocument.Parse(LocalIntelligenceService.ExtractJson(answer));
            var query = document.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
            var details = document.RootElement.TryGetProperty("details", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Модель не составила поисковый запрос.");
            ShoppingQueryBox.Text = query;
            ResultBox.Text = "РАСПОЗНАНО ПО ИЗОБРАЖЕНИЮ:\n" + (details ?? query) +
                             "\n\nЗапрос можно уточнить вручную, затем нажать «Собрать».";
            StatusText.Text = "Изображение распознано локально.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Распознавание остановлено."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Не удалось распознать изображение."; }
        finally { CancelButton.IsEnabled = false; }
    }

    private async void ShoppingQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunShoppingAgentAsync();
    }

    private void ShoppingQueryBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ShoppingQueryBox.Text.StartsWith("Что ищем", StringComparison.Ordinal)) ShoppingQueryBox.Clear();
    }

    private async Task RunShoppingAgentAsync()
    {
        if (_tab is null || UrlService.IsInternal(_tab.CurrentUrl))
        { StatusText.Text = "Открой сайт магазина или каталога."; return; }
        var query = ShoppingQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.StartsWith("Что ищем", StringComparison.Ordinal))
        { StatusText.Text = "Напиши, что нужно найти."; return; }
        if (_model is null) { await EnsureModelAsync(); if (_model is null) return; }
        _cancellation?.Cancel(); _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true; ResultBox.Clear();
        try
        {
            StatusText.Text = "Поиск на текущем сайте…";
            var searched = await _tab.SearchCurrentSiteForAgentAsync(query);
            if (searched) await Task.Delay(TimeSpan.FromSeconds(5), _cancellation.Token);
            var rawItems = new List<string>();
            var itemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _tab.CurrentUrl };
            var pages = 0;
            for (var page = 1; page <= 5 && rawItems.Count < 150; page++)
            {
                pages = page;
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
                for (var scrollRound = 0; scrollRound < 3 && rawItems.Count < 150; scrollRound++)
                {
                    await _tab.ScrollShoppingResultsAsync();
                    await AppendCurrentPageAsync();
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
            if (count == 0)
            {
                StatusText.Text = "Локальный AI определяет, что показал сайт…";
                var visibleText = await _tab.GetReadablePageTextAsync();
                var diagnosis = await LocalIntelligenceService.DiagnoseAgentPageAsync(
                    query, _tab.Title, _tab.CurrentUrl, visibleText, _cancellation.Token);
                throw new InvalidOperationException("Карточки товаров не извлечены. " + diagnosis);
            }
            var cards = "[" + string.Join(",", rawItems) + "]";
            StatusText.Text = $"Nexus Fast Intelligence сравнивает {count} результатов с {pages} страниц…";
            var report = await LocalIntelligenceService.AnalyzeShoppingResultsAsync(
                query, _tab.CurrentHost, cards, _cancellation.Token);
            var lines = new List<string>();
            for (var i = 0; i < report.Items.Count; i++)
            {
                var item = report.Items[i];
                lines.Add($"{i + 1}. {item.Name}\n   Цена: {item.Price} · Рейтинг: {item.Rating} · Купили/отзывы: {item.Buyers}\n" +
                          $"   Плюсы: {item.Strengths}\n   Минусы: {item.Weaknesses}\n   Оценка: {item.Score:0.0}/10" +
                          (string.IsNullOrWhiteSpace(item.Url) ? string.Empty : $"\n   {item.Url}"));
            }
            ResultBox.Text = string.Join("\n\n", lines) + "\n\nВЫВОД NEXUS AI:\n" + report.Recommendation +
                             (string.IsNullOrWhiteSpace(report.Caveat) ? string.Empty : "\n\nОграничение: " + report.Caveat);
            ResultBox.ScrollToHome();
            StatusText.Text = $"Готово. Просмотрено страниц: {pages}; вариантов в выводе: {report.Items.Count}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Сбор остановлен."; }
        catch (Exception ex) { ResultBox.Text = ex.Message; StatusText.Text = "Агент не собрал сравнение."; }
        finally { CancelButton.IsEnabled = false; }
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
    private void Close_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); Visibility = Visibility.Collapsed; }

}
