namespace NexusMonach.Services;

/// <summary>
/// Локальные TTS-воркеры (torch) и Whisper (ggml) открывают файл модели через
/// ANSI fopen: кириллица в пути установки превращается в мусор в системной
/// кодировке Windows, и процесс молча умирает на загрузке модели. Пути с
/// не-ASCII символами заранее зеркалируются в ASCII-безопасный кэш с
/// копированием в один поток. Node/OPUS перевод моделей не использует —
/// там пути читаются через Unicode-безопасный fs.
/// </summary>
internal static class AsciiSafeModelCache
{
    // Обе линии речи (ассистент и дубляж) могут зеркалить одну и ту же
    // модель одновременно; без сериализации гонка копий способна подсунуть
    // воркеру битый путь — и он молча умирает на загрузке.
    private static readonly SemaphoreSlim MirrorLock = new(1, 1);

    public static async Task<string> EnsureAsciiSafePathAsync(string modelPath)
    {
        if (IsAscii(modelPath)) return modelPath;
        await MirrorLock.WaitAsync();
        try
        {
            return MirrorLocked(modelPath);
        }
        finally
        {
            MirrorLock.Release();
        }
    }

    public static string EnsureAsciiSafePath(string modelPath)
    {
        if (IsAscii(modelPath)) return modelPath;
        MirrorLock.Wait();
        try
        {
            return MirrorLocked(modelPath);
        }
        finally
        {
            MirrorLock.Release();
        }
    }

    private static string MirrorLocked(string modelPath)
    {
        try
        {
            var source = new FileInfo(modelPath);
            if (!source.Exists) return modelPath;
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NexusMonach", "VoiceCache");
            if (!IsAscii(cacheRoot)) return modelPath;
            var target = Path.Combine(cacheRoot, source.Name);
            var targetInfo = new FileInfo(target);
            if (targetInfo.Exists && targetInfo.Length == source.Length)
                return target;

            Directory.CreateDirectory(cacheRoot);
            // Промежуточный файл + Move: читатель никогда не видит
            // недокопированный файл.
            var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.Copy(modelPath, staging, overwrite: true);
            if (new FileInfo(staging).Length != source.Length)
            {
                File.Delete(staging);
                return modelPath;
            }
            File.Move(staging, target, overwrite: true);
            return target;
        }
        catch
        {
            // Кэш недоступен — воркер попробует исходный путь.
            return modelPath;
        }
    }

    private static bool IsAscii(string value)
    {
        foreach (var symbol in value)
            if (symbol > 127)
                return false;
        return true;
    }
}
