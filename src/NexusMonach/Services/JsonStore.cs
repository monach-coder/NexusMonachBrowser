using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMonach.Services;

public static class JsonStore
{
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
            await using var stream = File.OpenRead(path);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options);
            await stream.FlushAsync();
        }
        File.Move(temp, path, overwrite: true);
    }
}
