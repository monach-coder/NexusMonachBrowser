using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;

namespace NexusMonach.Services;

/// <summary>
/// Reduces DevTools Protocol payloads to diagnostics that are safe to retain in
/// the managed Inspector tree. Raw headers, request bodies, cookies and remote
/// object values must never become copyable UI text.
/// </summary>
public static partial class InspectorDataSanitizer
{
    private static readonly IReadOnlySet<string> NoRemovedProperties = new HashSet<string>();
    private static readonly HashSet<string> NetworkSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "headers", "requestHeaders", "responseHeaders", "headersText", "requestHeadersText",
        "postData", "postDataEntries", "cookies", "associatedCookies", "authChallenge"
    };

    private static readonly HashSet<string> ConsoleSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "unserializableValue", "objectId", "preview", "customPreview"
    };

    private static readonly HashSet<string> SafeDomAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "class", "name", "role", "href", "src"
    };

    public static string SanitizeNetworkEvent(string json) => Sanitize(json, NetworkSecrets, filterDomAttributes: false);

    public static string SanitizeConsoleEvent(string json) => Sanitize(json, ConsoleSecrets, filterDomAttributes: false);

    public static string SanitizeDomNode(string json) => Sanitize(json, NoRemovedProperties, filterDomAttributes: true);

    public static string SanitizeGeneral(string json) => Sanitize(json, NoRemovedProperties, filterDomAttributes: false);

    public static string SanitizeAccessibility(string json) =>
        Sanitize(json, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "value" }, filterDomAttributes: false);

    private static string Sanitize(string json, IReadOnlySet<string> removedProperties, bool filterDomAttributes)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return string.Empty;
            CleanNode(node, null, removedProperties, filterDomAttributes);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            });
        }
        catch (JsonException)
        {
            return RedactText(json);
        }
    }

    private static void CleanNode(JsonNode node, string? propertyName,
        IReadOnlySet<string> removedProperties, bool filterDomAttributes)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (removedProperties.Contains(pair.Key))
                {
                    obj.Remove(pair.Key);
                    continue;
                }

                if (filterDomAttributes && pair.Key.Equals("attributes", StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is JsonArray attributes)
                {
                    obj[pair.Key] = FilterAttributes(attributes);
                    continue;
                }

                if (pair.Value is not null)
                    CleanNode(pair.Value, pair.Key, removedProperties, filterDomAttributes);
            }
            return;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var item = array[index];
                if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                    array[index] = JsonValue.Create(CleanString(propertyName, itemText));
                else if (item is not null)
                    CleanNode(item, propertyName, removedProperties, filterDomAttributes);
            }
            return;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
        value.ReplaceWith(JsonValue.Create(CleanString(propertyName, text)));
    }

    private static JsonArray FilterAttributes(JsonArray attributes)
    {
        var result = new JsonArray();
        for (var index = 0; index + 1 < attributes.Count; index += 2)
        {
            var name = attributes[index]?.GetValue<string>() ?? string.Empty;
            if (!SafeDomAttributes.Contains(name)) continue;
            var value = attributes[index + 1]?.GetValue<string>() ?? string.Empty;
            result.Add(name);
            result.Add(name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("src", StringComparison.OrdinalIgnoreCase)
                ? UrlService.SanitizeForDisplay(value)
                : RedactText(value));
        }
        return result;
    }

    private static bool IsUrlProperty(string? name) => name is not null &&
        (name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("documentURL", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("requestURL", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("referrer", StringComparison.OrdinalIgnoreCase));

    private static string CleanString(string? propertyName, string value) => IsUrlProperty(propertyName)
        ? UrlService.SanitizeForDisplay(value)
        : RedactText(value);

    private static string RedactText(string value)
    {
        var result = WebUrlPattern().Replace(value, match =>
        {
            var safe = UrlService.SanitizeForDisplay(match.Value);
            return string.IsNullOrWhiteSpace(safe) ? "[скрытый URL]" : safe;
        });
        result = AuthorizationPattern().Replace(result, "$1 скрыто");
        result = SecretPattern().Replace(result, "$1 скрыто");
        return result[..Math.Min(result.Length, 4000)];
    }

    [GeneratedRegex("""(?i)https?://[^\s"'<>),;\]}]+""")]
    private static partial Regex WebUrlPattern();

    [GeneratedRegex(@"(?i)\b(authorization\s*[:=]\s*(?:bearer|basic))\s+[^\s,;]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)\b(token|cookie|password|passwd|secret|пароль|секрет)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex SecretPattern();
}
