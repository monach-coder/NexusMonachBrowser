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
        var wake = text.Contains("нексус", StringComparison.Ordinal) ||
                   text.Contains("nexus", StringComparison.Ordinal);
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
