using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NexusMonach.Services;

/// <summary>
/// Один сегмент перехваченной аудиодорожки: WAV 16к моно и его точное
/// положение на таймлайне видео — таймкод дорожки совпадает с таймлайном
/// видео, поэтому якорь абсолютный и не требует никаких вычислений.
/// </summary>
internal sealed record InterceptedAudioSegment(byte[] Wav, double StartSeconds, double EndSeconds);

/// <summary>
/// Профессиональный путь захвата: URL отдельной аудиодорожки (YouTube
/// отдаёт звук независимым адаптивным потоком) берётся из страницы,
 /// дорожка докачивается независимо от плеера («теневая загрузка», плеер
/// не затрагивается вовсе) и декодируется ffmpeg в 16к моно. Это даёт
/// идеальное качество сигнала и абсолютные таймкоды — без captureStream,
/// WebAudio и системного захвата.
/// </summary>
internal sealed class OnlineAudioTrackTap : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _trackFile;
    private readonly string _decodedFile;
    private long _downloadedBytes;
    private long _totalBytes = -1;
    private double _decodedThroughSeconds;
    private bool _disposed;
    private bool _downloadFinished;

    private OnlineAudioTrackTap(string url, string referer)
    {
        // Privacy-браузер уважает прокси пользователя: shadow-скачивание
        // идёт через тот же сетевой путь, что и сама страница.
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        // Referer берётся со страницы: в заголовки допускается только
        // строго валидный http(s)-адрес — никакого произвольного текста.
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) &&
            (refererUri.Scheme == Uri.UriSchemeHttp || refererUri.Scheme == Uri.UriSchemeHttps))
        {
            _client.DefaultRequestHeaders.Referrer = refererUri;
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", refererUri.GetLeftPart(UriPartial.Authority));
        }
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _trackFile = Path.Combine(Path.GetTempPath(), "nexus-audio-track-" + Guid.NewGuid().ToString("N") + ".bin");
        _decodedFile = Path.Combine(Path.GetTempPath(), "nexus-audio-decoded-" + Guid.NewGuid().ToString("N") + ".wav");
        Url = url;
    }

    /// <summary>
    /// Допускает только публичные https/http-адреса: host не должен быть
    /// loopback, private или reserved — защита от SSRF через страницу.
    /// </summary>
    private static bool IsSafePublicUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var hostOk = uri.HostNameType switch
        {
            UriHostNameType.Dns => uri.Host.Contains('.'),
            UriHostNameType.IPv4 or UriHostNameType.IPv6 => true,
            _ => false
        };
        if (!hostOk)
            return false;
        if (uri.IsLoopback)
            return false;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!System.Net.IPAddress.TryParse(uri.Host, out var address))
                return false;
            if (!System.Net.IPAddress.IsLoopback(address))
            {
                // Приватные и reserved диапазоны запрещены.
                var bytes = address.GetAddressBytes();
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    if (bytes[0] == 10 || bytes[0] == 127 ||
                        (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                        (bytes[0] == 192 && bytes[1] == 168) ||
                        bytes[0] == 169 && bytes[1] == 254)
                        return false;
                }
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    public string Url { get; }
    public long DownloadedBytes => Volatile.Read(ref _downloadedBytes);
    public long TotalBytes => Volatile.Read(ref _totalBytes);
    public bool DownloadFinished => Volatile.Read(ref _downloadFinished);

    /// <summary>
    /// Запускает теневую загрузку дорожки в фоне. Возвращает null, если URL
    /// не подходит или первый чанк не получен — вызывающий остаётся на
    /// страничном захвате.
    /// </summary>
    public static async Task<OnlineAudioTrackTap?> StartAsync(
        string url, string referer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !IsSafePublicUrl(uri))
            return null;
        var tap = new OnlineAudioTrackTap(url, referer);
        try
        {
            // Первый чанк — синхронно: он же доказывает работоспособность URL.
            await tap.DownloadChunkAsync(cancellationToken);
            _ = Task.Run(() => tap.DownloadLoopAsync(cancellationToken), cancellationToken);
            return tap;
        }
        catch (OperationCanceledException) { tap.Dispose(); throw; }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "track-tap-start", ex);
            tap.Dispose();
            return null;
        }
    }

    private async Task DownloadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_downloadFinished)
                await DownloadChunkAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "track-tap-download", ex);
        }
    }

    private async Task DownloadChunkAsync(CancellationToken cancellationToken)
    {
        const long chunk = 2 * 1024 * 1024;
        var from = _downloadedBytes;
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(from, from + chunk - 1);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _downloadFinished = true;
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentRange?.HasRange == true)
        {
            var total = response.Content.Headers.ContentRange.Length;
            if (total is > 0) Interlocked.Exchange(ref _totalBytes, total.Value);
        }
        // Сервер без поддержки диапазонов отдал всё целиком — повторно не качаем.
        if (response.StatusCode == System.Net.HttpStatusCode.OK && from > 0)
        {
            _downloadFinished = true;
            return;
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(_trackFile, FileMode.Append, FileAccess.Write,
            FileShare.Read, 1 << 16, useAsync: true);
        var before = target.Position;
        await source.CopyToAsync(target, 1 << 16, cancellationToken);
        var written = target.Position - before;
        if (written == 0)
        {
            _downloadFinished = true;
            return;
        }
        Interlocked.Add(ref _downloadedBytes, written);
        if (_totalBytes > 0 && _downloadedBytes >= _totalBytes)
            _downloadFinished = true;
    }

    /// <summary>
    /// Декодирует скачанный префикс дорожки в WAV 16к моно встроенным
    /// декодером Windows (MediaFoundation, внутри процесса — без внешних
    /// утилит) и возвращает только новые куски, нарезанные ≤ 20 с.
    /// </summary>
    public async Task<List<InterceptedAudioSegment>> DecodeNewSegmentsAsync(
        CancellationToken cancellationToken)
    {
        var segments = new List<InterceptedAudioSegment>();
        if (_disposed || _downloadedBytes < 64 * 1024) return segments;
        try
        {
            var wav = await Task.Run(() => DecodeCore(), cancellationToken);
            if (wav is null) return segments;
            var totalSeconds = AudioRateRestore.PcmDurationSeconds(wav);
            if (totalSeconds <= _decodedThroughSeconds + 0.5) return segments;
            for (var from = _decodedThroughSeconds; from < totalSeconds; from += 20)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var to = Math.Min(from + 20, totalSeconds);
                var slice = AudioRateRestore.SliceByTime(wav, from, to);
                if (slice.Length <= 44) break;
                segments.Add(new InterceptedAudioSegment(slice, from, to));
            }
            _decodedThroughSeconds = totalSeconds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "track-tap-decode", ex);
            return segments;
        }
        return segments;
    }

    /// <summary>
    /// Декодирует весь докачанный префикс дорожки в WAV 16к моно встроенным
    /// декодером Windows — композер нарежет нужное окно по времени.
    /// Таймаут 15 с + одна повторная попытка: MediaFoundation иногда
    /// блокируется на битых контейнерах, и без таймаута сеанс зависает.
    /// </summary>
    public async Task<byte[]?> DecodeAllAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _downloadedBytes < 64 * 1024) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var decode = Task.Run(DecodeCore, cancellationToken);
                var finished = await Task.WhenAny(decode,
                    Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));
                if (finished == decode)
                    return await decode;
                // Таймаут: контейнер, вероятно, битый или формат не поддерживается.
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (attempt == 1) return null;
            }
        }
        return null;
    }

    private byte[]? DecodeCore()
    {
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(_trackFile);
            using var resampler = new NAudio.Wave.MediaFoundationResampler(
                reader, new NAudio.Wave.WaveFormat(16000, 16, 1));
            using var memory = new MemoryStream();
            NAudio.Wave.WaveFileWriter.WriteWavFileToStream(memory, resampler);
            return memory.ToArray();
        }
        catch
        {
            // Контейнер ещё не готов (докачивается) или формат не поддерживается
            // декодером Windows — вызывающий откатится на страничный захват.
            return null;
        }
    }

    /// <summary>Скачанные байты дорожки (сколько успело докачаться).</summary>
    public async Task<byte[]?> ReadDownloadedBytesAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _downloadedBytes <= 0) return null;
        try
        {
            return await File.ReadAllBytesAsync(_trackFile, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "track-tap-read", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.Dispose(); } catch { }
        try { File.Delete(_trackFile); } catch { }
    }
}
