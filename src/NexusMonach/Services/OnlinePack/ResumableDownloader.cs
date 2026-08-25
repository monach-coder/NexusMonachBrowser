using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NexusMonach.Services.OnlinePack;

/// <summary>Прогресс загрузки одного файла.</summary>
public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes, string RelativePath);

/// <summary>Результат загрузки файла.</summary>
public enum DownloadOutcome
{
    Completed,
    AlreadyFresh,
    HashMismatch,
    Failed
}

/// <summary>
/// Загрузка файлов поставки с докачкой: недокачанный файл лежит рядом как
/// .part и продолжается Range-запросом с места обрыва. По завершении файл
/// сверяется с SHA-256 из подписанного манифеста — несовпадение стирает
/// загрузку и сообщает об этом, а не подсовывает битый файл.
/// </summary>
public static class ResumableDownloader
{
    private static readonly HttpClientHandler Handler = new()
    {
        AutomaticDecompression = DecompressionMethods.All
    };
    private static readonly HttpClient Client = new(Handler) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Загружает <paramref name="relativePath"/> из <paramref name="baseUrl"/>
    /// в <paramref name="destinationPath"/>. Существующий файл с совпадающим
    /// размером считается готовым (хеш проверит вызывающий по манифесту).
    /// </summary>
    public static async Task<DownloadOutcome> DownloadAsync(
        Uri baseUrl,
        string relativePath,
        string destinationPath,
        ReleaseFile file,
        Action<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        try
        {
            if (File.Exists(destinationPath))
            {
                var current = new FileInfo(destinationPath).Length;
                if (current == file.Length)
                    return DownloadOutcome.AlreadyFresh;
                File.Delete(destinationPath);
            }

            var url = new Uri(baseUrl, relativePath.Replace('\\', '/'));
            long offset = 0;
            if (File.Exists(partPath))
                offset = new FileInfo(partPath).Length;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
                request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Сервер не смог продолжить с этого offset — качаем заново.
                File.Delete(partPath);
                return await DownloadAsync(baseUrl, relativePath, destinationPath, file,
                    progress, cancellationToken);
            }
            if (!response.IsSuccessStatusCode)
                return DownloadOutcome.Failed;

            var total = offset + (response.Content.Headers.ContentLength ?? 0);
            await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var local = new FileStream(partPath,
                offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long received = offset;
            int read;
            while ((read = await remote.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Invoke(new DownloadProgress(received, total, relativePath));
            }

            if (file.Length > 0 && received != file.Length)
                return DownloadOutcome.Failed; // обрыв сети — .part останется для докачки

            local.Close();
            var hash = ReleaseManifestVerifier.ComputeSha256(partPath);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                return DownloadOutcome.HashMismatch;
            }
            File.Move(partPath, destinationPath, overwrite: true);
            return DownloadOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            throw; // .part сохраняется для докачки при следующей попытке
        }
        catch
        {
            return DownloadOutcome.Failed; // .part сохраняется для докачки
        }
    }
}
