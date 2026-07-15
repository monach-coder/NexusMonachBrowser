using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusMonach.Services;

/// <summary>Нормализует недетерминированный текст маленькой локальной модели до передачи в UI.</summary>
public static class LocalModelOutput
{
    public static string ExtractJsonObject(string value)
    {
        value = Regex.Replace(value ?? string.Empty, "```(?:json)?|```", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        string? last = null;
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"') { inString = true; continue; }
            if (character == '{')
            {
                if (depth == 0) start = index;
                depth++;
            }
            else if (character == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                    last = value[start..(index + 1)];
            }
        }

        if (string.IsNullOrWhiteSpace(last)) throw new JsonException("В ответе локальной модели нет JSON-объекта.");
        return EscapeControlCharactersInsideStrings(last);
    }

    public static T? DeserializeJson<T>(string value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(ExtractJsonObject(value), options);

    private static string EscapeControlCharactersInsideStrings(string json)
    {
        var result = new StringBuilder(json.Length + 32);
        var inString = false;
        var escaped = false;
        foreach (var character in json)
        {
            if (!inString)
            {
                result.Append(character);
                if (character == '"') inString = true;
                continue;
            }

            if (escaped)
            {
                result.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                result.Append(character);
                escaped = true;
                continue;
            }
            if (character == '"')
            {
                result.Append(character);
                inString = false;
                continue;
            }

            result.Append(character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                < ' ' => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }
        return result.ToString();
    }
}
