using NexusMonach.Models;

namespace NexusMonach.Services.Dubbing;

/// <summary>
/// Результат сеанса трек-дубляжа.
/// </summary>
internal enum TrackDubbingOutcome
{
    /// <summary>Сеанс завершился успешно (видео досмотрено или остановлено пользователем).</summary>
    Completed,
    /// <summary>Дорожка не найдена или не декодируется — нужен откат на живой режим.</summary>
    FallbackToLive,
    /// <summary>Сеанс прерван ошибкой.</summary>
    Failed,
}

/// <summary>
/// Сеанс синхронного перевода видео по отдельной аудиодорожке.
/// Плеер не трогается — дорожку докачивает «тень», whisper даёт точные
/// таймкоды реплик, OPUS переводит целыми предложениями, Silero/Piper
/// озвучивает с подгонкой под слот. Реплики встают ровно на речь оригинала.
/// </summary>
internal sealed class TrackDubbingSession : IDisposable
{
    private readonly BrowserTab _tab;
    private CancellationTokenSource? _session;
    private PrecomputedDubbingPlayer? _player;
    private OnlineAudioTrackTap? _tap;
    private readonly List<string> _allWavs = [];

    public TrackDubbingSession(BrowserTab tab)
    {
        _tab = tab;
    }

    /// <summary>
    /// Запускает полный цикл: находит дорожку, переводит задел, запускает
    /// показ с синхронным дубляжом и догружает у границы.
    /// </summary>
    public async Task<TrackDubbingOutcome> RunAsync(
        Func<Task> fallbackToLive, CancellationToken externalToken)
    {
        _session = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _session.Token;
        var finalStatus = "Синхронный перевод остановлен.";
        var watchPosition = 0.0;
        var completedNaturally = false;
        try
        {
            var duration = await _tab.GetActiveVideoDurationAsync()
                         ?? throw new InvalidOperationException(
                                "Не удалось определить длительность видео.");
            watchPosition = await GetPositionAsync(token);
            await _tab.BeginLiveAudioTranslationAsync();
            _tab.EnableAudioTrackWatch();
            await _tab.PrepareVideoForAnalysisAsync();
            await _tab.EnableVideoDubbingMixAsync();
            await _tab.SetBufferingVeilAsync(true, "Ищу отдельную аудиодорожку…");

            // Whisper и OPUS греются ПАРАЛЛЕЛЬНО с поиском и скачиванием
            // дорожки: раньше их холодный старт оплачивался внутри первого
            // окна композиции и съедал половину буферного бюджета.
            var coreWarmup = Task.WhenAll(
                WhisperService.WarmUpAsync(WhisperLane.Dubbing, token),
                TranslationService.WarmUpForLiveVideoAsync(false, token));
            await VideoDubbingVoiceService.WarmUpAsync(token);

            // Ищем URL дорожки: сначала мгновенно, потом короткий прогон.
            var (trackUrl, referer) = await _tab.GetVideoAudioTrackUrlAsync(waitForNetworkMs: 0);
            if (string.IsNullOrWhiteSpace(trackUrl))
            {
                await _tab.SetVideoAnalysisRateAsync(1);
                trackUrl = (await _tab.GetVideoAudioTrackUrlAsync(waitForNetworkMs: 12_000)).Url;
                referer = _tab.CurrentUrl;
                await _tab.PauseActiveVideoAsync();
            }
            _tap = string.IsNullOrWhiteSpace(trackUrl)
                ? null
                : await OnlineAudioTrackTap.StartAsync(trackUrl, referer, token);

            // Дорожка докачивается фрагментами: первый скачанный префикс
            // MediaFoundation часто не может декодировать (moov ещё не полон).
            // Раньше это мгновенно уронило сеанс в живой режим — пользователь
            // видел «не сработало». Ждём дозагрузку до 18 с, пока загрузка
            // реально идёт, и лишь потом откатываемся.
            byte[]? decoded = null;
            if (_tap is not null)
            {
                var downloadedBefore = _tap.DownloadedBytes;
                var lastProgressAt = DateTimeOffset.UtcNow;
                var decodeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(18);
                while (!token.IsCancellationRequested)
                {
                    decoded = await _tap.DecodeAllAsync(token);
                    if (decoded is not null || _tap.DownloadFinished) break;
                    var now = DateTimeOffset.UtcNow;
                    if (_tap.DownloadedBytes != downloadedBefore)
                    {
                        downloadedBefore = _tap.DownloadedBytes;
                        lastProgressAt = now;
                    }
                    if (now >= decodeDeadline ||
                        now - lastProgressAt > TimeSpan.FromSeconds(6)) break;
                    await _tab.SetBufferingVeilAsync(true,
                        "Ищу отдельную аудиодорожку…\nдекодирую скачанное");
                    try { await Task.Delay(1200, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            if (decoded is null)
            {
                await CleanupFallbackAsync();
                await fallbackToLive();
                return TrackDubbingOutcome.FallbackToLive;
            }

            // Модели должны быть готовы к первому окну композиции; сбой
            // прогрева не отменяет сеанс — композер повторит вызовы сам.
            try { await coreWarmup; }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("video-translation", "track-warmup", ex);
            }

            _player = new PrecomputedDubbingPlayer();
            var phrases = new List<TrackDubbedPhrase>();
            var frontier = watchPosition;

            async Task ComposeWindowAsync(double from, double lookahead, TimeSpan budget, string title)
            {
                var deadline = DateTimeOffset.UtcNow + budget;
                while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
                {
                    decoded = await _tap!.DecodeAllAsync(token);
                    if (decoded is null) break;
                    var decodedSeconds = AudioRateRestore.PcmDurationSeconds(decoded);
                    var windowEnd = Math.Min(from + lookahead, decodedSeconds - 0.5);
                    if (windowEnd - from >= 2)
                    {
                        var progress = new Progress<string>(_ => { });
                        var fresh = await TrackDubbingComposer.ComposeAsync(
                            decoded, from, windowEnd, _allWavs, progress, token);
                        lock (phrases) phrases.AddRange(fresh);
                        if (fresh.Count > 0)
                            frontier = Math.Max(frontier, fresh[^1].SlotEndSeconds);
                    }
                    var remaining = Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
                    var ahead = Math.Max(0, frontier - from);
                    var downloaded = _tap.TotalBytes > 0
                        ? (int)(100.0 * _tap.DownloadedBytes / _tap.TotalBytes) : 0;
                    await _tab.SetBufferingVeilAsync(true,
                        $"{title}\nпереведено вперёд: {(int)ahead} с · скачано {downloaded}% · реплик: {phrases.Count}" +
                        $"\nосталось ≤ {(int)remaining} с");
                    if (frontier >= from + lookahead - 1 || decodedSeconds > from + lookahead)
                        break;
                    await Task.Delay(1500, token);
                }
            }

            await ComposeWindowAsync(watchPosition,
                VideoDubbingPolicy.InitialLookaheadSeconds,
                TimeSpan.FromSeconds(VideoDubbingPolicy.InitialBufferWallBudgetSeconds),
                "Синхронный перевод · готовлю озвучку");

            lock (phrases)
                phrases.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            await _tab.SetBufferingVeilAsync(false, string.Empty);
            await _tab.ResumeVideoFromAsync(watchPosition);
            await _tab.UpdateLiveAudioTranslationStatusAsync(
                $"Синхронный дубляж · {phrases.Count} реплик · приятного просмотра");

            // Показ с догрузкой.
            var next = 0;
            var playerPaused = false;
            var lastCatchUpAt = DateTimeOffset.UtcNow;
            while (!token.IsCancellationRequested)
            {
                if (await _tab.ShouldStopLiveAudioTranslationAsync()) break;
                var state = await _tab.GetVideoStateAsync();
                if (state is null || state.Ended) break;
                if (state.Paused)
                {
                    if (!playerPaused)
                    {
                        _player.Pause();
                        playerPaused = true;
                        await _tab.UpdateLiveAudioTranslationStatusAsync("Пауза · дубляж остановлен");
                    }
                    await Task.Delay(250, token);
                    continue;
                }
                if (playerPaused)
                {
                    _player.Resume();
                    playerPaused = false;
                }
                lock (phrases)
                {
                    while (next < phrases.Count && phrases[next].SlotEndSeconds < state.Position - 0.5)
                        next++;
                    if (next < phrases.Count &&
                        phrases[next].StartSeconds <= state.Position + 0.15 &&
                        !_player.IsPlaying)
                    {
                        _player.Enqueue(phrases[next].WavPaths);
                        next++;
                    }
                }
                if (state.Position > frontier - 15 && frontier < duration - 1 &&
                    DateTimeOffset.UtcNow - lastCatchUpAt > TimeSpan.FromSeconds(20))
                {
                    lastCatchUpAt = DateTimeOffset.UtcNow;
                    _player.Pause();
                    playerPaused = true;
                    await _tab.PrepareVideoForAnalysisAsync();
                    await ComposeWindowAsync(frontier,
                        VideoDubbingPolicy.CatchUpLookaheadSeconds,
                        TimeSpan.FromSeconds(VideoDubbingPolicy.CatchUpWallBudgetSeconds),
                        "Догружаю следующую часть");
                    lock (phrases)
                        phrases.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
                    await _tab.SetBufferingVeilAsync(false, string.Empty);
                    await _tab.ResumeVideoFromAsync(state.Position);
                    _player.Resume();
                    playerPaused = false;
                    continue;
                }
                await Task.Delay(120, token);
            }
            completedNaturally = true;
            finalStatus = "Синхронный дубляж завершён.";
            return TrackDubbingOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Синхронный перевод остановлен.";
            return TrackDubbingOutcome.Completed;
        }
        catch (Exception ex)
        {
            finalStatus = "Синхронный перевод прерван: " + ex.Message[..Math.Min(ex.Message.Length, 160)];
            CrashReportService.RecordNonFatal("video-translation", "track-dubbing", ex);
            return TrackDubbingOutcome.Failed;
        }
        finally
        {
            try { await _tab.SetBufferingVeilAsync(false, string.Empty); } catch { }
            try { _tab.DisableAudioTrackWatch(); } catch { }
            try
            {
                if (!completedNaturally)
                    await _tab.ResumeVideoFromAsync(watchPosition);
                else
                    await _tab.RestoreVideoRateAsync();
            }
            catch { }
            try { await _tab.EndLiveAudioTranslationAsync(finalStatus); } catch { }
        }
    }

    private async Task CleanupFallbackAsync()
    {
        _tap?.Dispose();
        _tap = null;
        try { await _tab.SetBufferingVeilAsync(false, string.Empty); } catch { }
        try { await _tab.EndLiveAudioTranslationAsync(string.Empty); } catch { }
        _tab.DisableAudioTrackWatch();
    }

    private async Task<double> GetPositionAsync(CancellationToken ct)
    {
        var state = await _tab.GetVideoStateAsync();
        if (state is not null) return state.Position;
        ct.ThrowIfCancellationRequested();
        return 0;
    }

    public void Dispose()
    {
        _session?.Cancel();
        _session?.Dispose();
        _player?.Dispose();
        _tap?.Dispose();
        foreach (var wav in _allWavs)
            try { File.Delete(wav); } catch { }
        VideoDubbingVoiceService.Stop();
    }
}
