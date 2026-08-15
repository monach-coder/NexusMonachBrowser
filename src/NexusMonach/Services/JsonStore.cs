using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMonach.Services;

public static class JsonStore
{
    private const int ReplaceAttempts = 8;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T?> ReadAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options);
        }
        catch (JsonException)
        {
            var broken = path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(path, broken, overwrite: true);
            return default;
        }
    }

    public static async Task WriteAsync<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = WriteGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        string? temp = null;
        try
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            temp = Path.Combine(directory,
                $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options);
                await stream.FlushAsync();
            }

            await MoveIntoPlaceAsync(temp, fullPath);
            temp = null;
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch { }
            gate.Release();
        }
    }

    private static async Task MoveIntoPlaceAsync(string temp, string destination)
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)));
            }
    }
}
