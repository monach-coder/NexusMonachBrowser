using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusMonach.Services;

/// <summary>
/// Нормативные русские ударения для локального Silero-голоса. Словарь ruaccent
/// (MIT) хранится одной отсортированной строкой «слово⇥номер ударной гласной»,
/// поэтому поиск — бинарный внутри единственного байтового блоба, без хеш-таблиц
/// на миллионы словоформ. Маркер «+» ставится только в текст нейроголоса:
/// Silero читает его как ручное ударение, а SAPI и Piper произнесли бы «плюс».
/// </summary>
internal static partial class RussianStressDictionary
{
    private const int MaximumWordLength = 64;
    private static readonly object Sync = new();
    private static byte[]? _blob;
    private static bool _loadAttempted;

    public static bool IsReady => _blob is not null;

    /// <summary>Заранее разворачивает словарь в память (вызов из фонового прогрева).</summary>
    public static void WarmUp()
    {
        _ = EnsureLoaded();
    }

    /// <summary>
    /// Возвращает текст с маркерами «+» перед ударной гласной каждого знакомого
    /// слова. Незнакомые слова, слова с «ё» и односложные остаются как есть.
    /// </summary>
    public static string ApplyStress(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var blob = EnsureLoaded();
        if (blob is null || blob.Length == 0) return text;
        return WordPattern().Replace(text, match =>
        {
            var word = match.Value;
            if (word.Length > MaximumWordLength || word.Contains('ё') || word.Contains('Ё'))
                return word;
            var stressed = TryStressWord(blob, word);
            return stressed ?? word;
        });
    }

    internal static string? TryStressWord(string word)
    {
        var blob = EnsureLoaded();
        return blob is null ? null : TryStressWord(blob, word);
    }

    private static string? TryStressWord(byte[] blob, string word)
    {
        var lower = word.ToLowerInvariant();
        if (!HasAtLeastTwoVowels(lower)) return null;
        var needle = Encoding.UTF8.GetBytes(lower);
        var stressIndex = FindStressIndex(blob, needle);
        if (stressIndex < 0) return null;

        var builder = new StringBuilder(word.Length + 1);
        var vowels = 0;
        foreach (var symbol in word)
        {
            if (vowels <= stressIndex && IsVowel(symbol))
            {
                if (vowels == stressIndex) builder.Append('+');
                vowels++;
            }
            builder.Append(symbol);
        }
        return builder.ToString();
    }

    private static bool HasAtLeastTwoVowels(string word)
    {
        var count = 0;
        foreach (var symbol in word)
        {
            if (!IsVowel(symbol)) continue;
            count++;
            if (count >= 2) return true;
        }
        return false;
    }

    internal static bool IsVowel(char symbol) =>
        "аеёиоуыэюяАЕЁИОУЫЭЮЯ".IndexOf(symbol) >= 0;

    private static byte[]? EnsureLoaded()
    {
        if (_blob is not null) return _blob;
        lock (Sync)
        {
            if (_blob is not null) return _blob;
            if (_loadAttempted) return null;
            _loadAttempted = true;
            try
            {
                var path = AiModelCatalog.StressDictionary;
                if (!AiModelCatalog.StressDictionaryReady) return null;
                var blob = ReadBlob(path);
                if (blob is null) return null;
                _blob = blob;
                return _blob;
            }
            catch
            {
                // Словарь — улучшение качества речи, а не обязательный компонент:
                // без него синтез продолжает работать на предсказании Silero.
                return null;
            }
        }
    }

    /// <summary>Тестовая точка входа: загрузить словарь из явного пути.</summary>
    internal static void LoadFrom(string path)
    {
        lock (Sync)
        {
            var blob = ReadBlob(path);
            if (blob is not null)
            {
                _blob = blob;
                _loadAttempted = true;
            }
        }
    }

    private static byte[]? ReadBlob(string path)
    {
        using var compressed = File.OpenRead(path);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        gzip.CopyTo(unpacked);
        // Пустой или битый файл не должен оставить «пустой, но готовый» словарь.
        return unpacked.Length < 1024 ? null : unpacked.ToArray();
    }

    /// <summary>
    /// Бинарный поиск строки «слово⇥индекс» в отсортированном блобе. UTF-8
    /// сохраняет порядок кодовых точек, поэтому побайтовое сравнение совпадает
    /// с алфавитной сортировкой файла.
    /// </summary>
    private static int FindStressIndex(byte[] blob, byte[] needle)
    {
        long low = 0;
        long high = blob.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            long start = mid;
            while (start > low && blob[start - 1] != (byte)'\n') start--;
            long cursor = start;
            int compared = 0;
            int needleIndex = 0;
            while (cursor < blob.Length && blob[cursor] != (byte)'\t')
            {
                var lineByte = blob[cursor];
                if (compared == 0)
                {
                    if (needleIndex >= needle.Length) { compared = 1; }
                    else
                    {
                        var difference = lineByte.CompareTo(needle[needleIndex]);
                        if (difference != 0) compared = difference < 0 ? -1 : 1;
                        needleIndex++;
                    }
                }
                cursor++;
            }
            if (compared == 0 && needleIndex < needle.Length)
                compared = (byte)'\t' < needle[needleIndex] ? -1 : 1;
            if (compared == 0)
            {
                // Строка совпала: за табом идёт десятичный индекс гласной.
                cursor++;
                var value = 0;
                while (cursor < blob.Length && blob[cursor] >= (byte)'0' && blob[cursor] <= (byte)'9')
                {
                    value = value * 10 + (blob[cursor] - (byte)'0');
                    cursor++;
                }
                return value;
            }
            if (compared < 0)
            {
                while (cursor < blob.Length && blob[cursor] != (byte)'\n') cursor++;
                low = cursor + 1;
            }
            else
            {
                high = start;
            }
        }
        return -1;
    }

    [GeneratedRegex("[А-Яа-яЁё]+")]
    private static partial Regex WordPattern();
}
