using System.Text;
using System.Text.RegularExpressions;

namespace NexusMonach.Services;

/// <summary>Создаёт короткие связные фразы для локальной озвучки статьи.</summary>
internal static partial class PageNarrationPolicy
{
    public const int MaximumSpeechCharacters = 240;

    public static IReadOnlyList<string> CreateSpeechChunks(IEnumerable<string> fragments,
        int maximumCharacters = MaximumSpeechCharacters)
    {
        maximumCharacters = Math.Clamp(maximumCharacters, 80, 360);
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var value in fragments)
        {
            var text = SpacePattern().Replace(value ?? string.Empty, " ").Trim();
            while (text.Length > 0)
            {
                var available = maximumCharacters - current.Length - (current.Length == 0 ? 0 : 1);
                if (available <= 0)
                {
                    Flush(current, chunks);
                    continue;
                }
                if (text.Length <= available)
                {
                    if (current.Length > 0) current.Append(' ');
                    current.Append(text);
                    break;
                }

                var split = FindNaturalSplit(text, available);
                if (current.Length > 0) current.Append(' ');
                current.Append(text[..split].Trim());
                Flush(current, chunks);
                text = text[split..].TrimStart();
            }
        }
        Flush(current, chunks);
        return chunks;
    }

    private static int FindNaturalSplit(string text, int limit)
    {
        var minimum = Math.Max(1, limit / 2);
        for (var index = Math.Min(limit, text.Length - 1); index >= minimum; index--)
            if (text[index] is '.' or '!' or '?' or '…' or ';' or ':') return index + 1;
        for (var index = Math.Min(limit, text.Length - 1); index >= 1; index--)
            if (char.IsWhiteSpace(text[index])) return index;
        return Math.Min(limit, text.Length);
    }

    private static void Flush(StringBuilder current, ICollection<string> chunks)
    {
        if (current.Length == 0) return;
        chunks.Add(current.ToString().Trim());
        current.Clear();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
