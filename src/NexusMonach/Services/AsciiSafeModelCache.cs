namespace NexusMonach.Services;

/// <summary>
/// Локальные TTS-воркеры открывают файл модели через ANSI fopen: кириллица
/// в пути установки превращается в мусор в системной кодировке Windows, и
/// синтез молча падает на резервный SAPI-голос. Пути с не-ASCII символами
/// заранее зеркалируются в ASCII-безопасный кэш с копированием в один поток.
/// </summary>
internal static class AsciiSafeModelCache
{
    public static string EnsureAsciiSafePath(string modelPath)
    {
        if (IsAscii(modelPath)) return modelPath;
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
            // Промежуточный файл + Move: параллельные дорожки речи могут
            // зеркалить модель одновременно, а читатель никогда не видит
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
