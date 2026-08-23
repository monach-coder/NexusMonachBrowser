using System.Text.RegularExpressions;

namespace NexusMonach.Services;

internal enum VoiceCommandKind
{
    None,
    StopVoice,
    Back,
    Forward,
    Reload,
    Home,
    NewTab,
    CloseTab,
    OpenSettings,
    OpenGuardian,
    TranslatePage,
    TranslateVideo,
    OpenSledopyt,
    Search,
    DisableHandsFree
}

internal sealed record VoiceCommand(VoiceCommandKind Kind, string Argument = "", bool WakeWordPresent = false);

internal static partial class VoiceCommandRouter
{
    public static VoiceCommand Parse(string? transcript, bool requireWakeWord)
    {
        var text = Normalize(transcript);
        if (text.Length == 0) return new VoiceCommand(VoiceCommandKind.None);
        var wake = ContainsWakeWord(text);
        if (requireWakeWord && !wake) return new VoiceCommand(VoiceCommandKind.None);
        text = WakePattern().Replace(text, " ");
        text = SpacePattern().Replace(text, " ").Trim();

        if (Has(text, "стоп", "замолчи", "прекрати говорить")) return New(VoiceCommandKind.StopVoice, wake);
        if (Has(text, "выключи свободные руки", "отключи свободные руки")) return New(VoiceCommandKind.DisableHandsFree, wake);
        if (Has(text, "назад", "вернись назад")) return New(VoiceCommandKind.Back, wake);
        if (Has(text, "вперёд", "вперед")) return New(VoiceCommandKind.Forward, wake);
        if (Has(text, "обнови", "перезагрузи страницу")) return New(VoiceCommandKind.Reload, wake);
        if (Has(text, "домой", "открой главную")) return New(VoiceCommandKind.Home, wake);
        if (Has(text, "новая вкладка", "открой вкладку")) return New(VoiceCommandKind.NewTab, wake);
        if (Has(text, "закрой вкладку", "закрыть вкладку")) return New(VoiceCommandKind.CloseTab, wake);
        if (Has(text, "открой настройки", "настройки")) return New(VoiceCommandKind.OpenSettings, wake);
        if (Has(text, "открой гардиан", "центр гардиан", "guardian")) return New(VoiceCommandKind.OpenGuardian, wake);
        if (Has(text, "переведи страницу", "перевод страницы")) return New(VoiceCommandKind.TranslatePage, wake);
        if (Has(text, "переведи видео", "перевод видео", "переводи видео")) return New(VoiceCommandKind.TranslateVideo, wake);
        if (Has(text, "исследуй страницу", "проанализируй страницу", "открой следопыт"))
            return New(VoiceCommandKind.OpenSledopyt, wake);

        var match = SearchPattern().Match(text);
        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return new VoiceCommand(VoiceCommandKind.Search, match.Groups[1].Value.Trim(), wake);
        return new VoiceCommand(VoiceCommandKind.None, string.Empty, wake);
    }

    private static VoiceCommand New(VoiceCommandKind kind, bool wake) => new(kind, string.Empty, wake);
    private static bool Has(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.Ordinal));

    /// <summary>
    /// Локальный whisper на живой речи слышит слово-пароль по-разному:
    /// «нексус», «нэксус», «нексис», «некст», «nexus». Точное сравнение
    /// роняло почти каждую попытку — браузер «слушал и молчал». Допускаем
    /// до двух правок на слово против известных написаний.
    /// </summary>
    private static bool ContainsWakeWord(string text)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length is < 4 or > 12) continue;
            if (CloseTo(word, "нексус", 2) || CloseTo(word, "nexus", 1) ||
                CloseTo(word, "некст", 1))
                return true;
        }
        return text.Contains("нексус", StringComparison.Ordinal) ||
               text.Contains("nexus", StringComparison.Ordinal);
    }

    private static bool CloseTo(string word, string target, int tolerance) =>
        word.Length >= target.Length - tolerance &&
        EditDistance(word, target) <= tolerance;

    private static int EditDistance(string left, string right)
    {
        var spans = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) spans[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            var previous = spans[0];
            spans[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var buffer = spans[j];
                spans[j] = Math.Min(Math.Min(spans[j] + 1, spans[j - 1] + 1),
                    previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = buffer;
            }
        }
        return spans[right.Length];
    }
    private static string Normalize(string? value) => SpacePattern().Replace(
        PunctuationPattern().Replace((value ?? string.Empty).ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"\b(?:нексус(?:\s+монах)?|nexus(?:\s+monach)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WakePattern();
    [GeneratedRegex(@"^(?:найди в сети|найди|поищи|поиск|ищи)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SearchPattern();
    [GeneratedRegex(@"[^\p{L}\p{N}\s\-]+")]
    private static partial Regex PunctuationPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
