using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class InspectorDataSanitizerTests
{
    [Fact]
    public void NetworkEventOmitsHeadersBodiesCookiesAndSecretUrlParts()
    {
        const string json = """
            {
              "request": {
                "url": "https://user:pass@example.com/api/items?token=query-secret#part",
                "method": "POST",
                "headers": { "Authorization": "Bearer header-secret", "Cookie": "sid=cookie-secret" },
                "postData": "password=body-secret"
              },
              "associatedCookies": [{ "cookie": { "value": "cookie-secret" } }]
            }
            """;

        var safe = InspectorDataSanitizer.SanitizeNetworkEvent(json);

        Assert.Contains("https://example.com/api/items", safe, StringComparison.Ordinal);
        Assert.Contains("POST", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("body-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("postData", safe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomNodeKeepsSafeAttributesButDropsFormAndDataValues()
    {
        const string json = """
            {
              "nodeName":"INPUT",
              "attributes":[
                "id","login",
                "class","field primary",
                "value","top-secret",
                "data-token","token-secret",
                "href","https://example.com/path?session=secret"
              ]
            }
            """;

        var safe = InspectorDataSanitizer.SanitizeDomNode(json);

        Assert.Contains("login", safe, StringComparison.Ordinal);
        Assert.Contains("field primary", safe, StringComparison.Ordinal);
        Assert.Contains("https://example.com/path", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("session=secret", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleEventDoesNotRetainRemoteObjectValues()
    {
        const string json = """
            {"type":"log","args":[{"type":"string","value":"password=secret-value","objectId":"remote-1"}]}
            """;

        var safe = InspectorDataSanitizer.SanitizeConsoleEvent(json);

        Assert.Contains("log", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-1", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralSnapshotCleansStringArraysWithoutMutatingEnumeration()
    {
        const string json = """{"values":["ordinary","token=secret-value","https://example.com/path?q=private#part"]}""";

        var safe = InspectorDataSanitizer.SanitizeGeneral(json);

        Assert.Contains("ordinary", safe, StringComparison.Ordinal);
        Assert.Contains("https://example.com/path", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("q=private", safe, StringComparison.Ordinal);
    }
}
