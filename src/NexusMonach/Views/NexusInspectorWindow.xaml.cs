using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Services;

namespace NexusMonach.Views;

public sealed class InspectorNode
{
    public required string Header { get; init; }
    public string Glyph { get; init; } = "◇";
    public string Category { get; init; } = string.Empty;
    public int? DomNodeId { get; init; }
    public string Details { get; set; } = string.Empty;
    public bool HasDeferredChildren { get; set; }
    public bool IsLoadingChildren { get; set; }
    public ObservableCollection<InspectorNode> Children { get; } = [];
}

public partial class NexusInspectorWindow : Window
{
    private const int MaximumDomNodes = 8000;
    private const int MaximumDomDepth = 120;
    private const int MaximumResourceNodes = 3000;
    private readonly CoreWebView2 _core;
    private readonly ObservableCollection<InspectorNode> _roots = [];
    private readonly List<Action> _unsubscribe = [];
    private InspectorNode? _networkRoot;
    private InspectorNode? _consoleRoot;
    private int _searchIndex = -1;

    public CoreWebView2 InspectedCore => _core;

    public NexusInspectorWindow(CoreWebView2 core)
    {
        _core = core;
        InitializeComponent();
        InspectorTree.ItemsSource = _roots;
        Loaded += async (_, _) =>
        {
            WindowAppearanceService.Apply(this, SettingsService.Current.ThemeMode);
            AttachLiveDomains();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            foreach (var unsubscribe in _unsubscribe)
                try { unsubscribe(); } catch { }
            _unsubscribe.Clear();
        };
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = "Читаю структуру страницы…";
        PageText.Text = UrlService.SanitizeForDisplay(_core.Source);
        _roots.Clear();

        var dom = new InspectorNode { Header = "DOM · структура документа", Glyph = "▦", Category = "DOM" };
        var accessibility = new InspectorNode { Header = "Accessibility · роли и имена", Glyph = "◎", Category = "Accessibility" };
        var resources = new InspectorNode { Header = "Resources · фреймы и файлы", Glyph = "▤", Category = "Resources" };
        var storage = new InspectorNode { Header = "Storage · локальные ключи", Glyph = "◫", Category = "Storage" };
        var performance = new InspectorNode { Header = "Performance · метрики страницы", Glyph = "⌁", Category = "Performance" };
        var security = new InspectorNode { Header = "Security · состояние соединения", Glyph = "◆", Category = "Security" };
        _networkRoot = new InspectorNode { Header = "Network · запросы текущего сеанса", Glyph = "⇄", Category = "Network" };
        _consoleRoot = new InspectorNode { Header = "Console · сообщения страницы", Glyph = ">_", Category = "Console" };
        foreach (var root in new[] { dom, accessibility, resources, storage, performance, security, _networkRoot, _consoleRoot })
            _roots.Add(root);

        await EnableDomainAsync("DOM.enable");
        await EnableDomainAsync("CSS.enable");
        await EnableDomainAsync("Accessibility.enable");
        await EnableDomainAsync("Page.enable");
        await EnableDomainAsync("Network.enable");
        await EnableDomainAsync("Runtime.enable");
        await EnableDomainAsync("Log.enable");
        await EnableDomainAsync("Performance.enable");
        await EnableDomainAsync("Security.enable");

        await PopulateDomAsync(dom);
        await PopulateAccessibilityAsync(accessibility);
        await PopulateResourcesAsync(resources);
        await PopulateStorageAsync(storage);
        await PopulateProtocolSnapshotAsync(performance, "Performance.getMetrics");
        await PopulateProtocolSnapshotAsync(security, "Security.getSecurityIsolationStatus");
        StatusText.Text = $"Готово · {CountNodes(_roots)} узлов · значения storage и cookie не читаются";
    }

    private async Task PopulateDomAsync(InspectorNode root)
    {
        var json = await CallAsync("DOM.getDocument", "{\"depth\":5,\"pierce\":true}");
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("root", out var node))
        {
            var remaining = MaximumDomNodes;
            root.Children.Add(CreateDomNode(node, 0, ref remaining));
            if (remaining <= 0)
                root.Children.Add(TruncatedNode($"DOM ограничен {MaximumDomNodes:N0} узлами для защиты памяти. Выберите нужный узел или используйте поиск."));
        }
    }

    private static InspectorNode CreateDomNode(JsonElement node, int depth, ref int remaining)
    {
        remaining--;
        var name = node.TryGetProperty("nodeName", out var nodeName) ? nodeName.GetString() ?? "node" : "node";
        var value = node.TryGetProperty("nodeValue", out var nodeValue) ? nodeValue.GetString() : null;
        var id = node.TryGetProperty("nodeId", out var nodeId) ? nodeId.GetInt32() : (int?)null;
        var attributes = new List<string>();
        if (node.TryGetProperty("attributes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var values = list.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            for (var index = 0; index + 1 < values.Length; index += 2)
                if (values[index] is "id" or "class" or "name" or "role" or "href" or "src")
                {
                    var attributeValue = values[index] is "href" or "src"
                        ? UrlService.SanitizeForDisplay(values[index + 1])
                        : values[index + 1];
                    attributes.Add($"{values[index]}=\"{Short(attributeValue, 80)}\"");
                }
        }

        var header = "<" + name.ToLowerInvariant() + ">";
        if (attributes.Count > 0) header += " " + string.Join(" ", attributes);
        if (!string.IsNullOrWhiteSpace(value)) header += " · " + Short(value, 80);
        var result = new InspectorNode
        {
            Header = header,
            Glyph = name.StartsWith('#') ? "·" : "◇",
            Category = "DOM",
            DomNodeId = id,
            Details = InspectorDataSanitizer.SanitizeDomNode(node.GetRawText())
        };
        if (depth >= MaximumDomDepth)
        {
            result.Children.Add(TruncatedNode($"Достигнута безопасная глубина {MaximumDomDepth}."));
            return result;
        }

        AddDomChildren(result, node, "children", depth, ref remaining);
        AddDomChildren(result, node, "shadowRoots", depth, ref remaining);
        var reportedChildren = node.TryGetProperty("childNodeCount", out var count)
            ? count.GetInt32()
            : 0;
        var loadedChildren = node.TryGetProperty("children", out var children) &&
                             children.ValueKind == JsonValueKind.Array
            ? children.GetArrayLength()
            : 0;
        if (id is not null && reportedChildren > loadedChildren && remaining > 0)
        {
            result.HasDeferredChildren = true;
            result.Children.Add(DeferredNode(reportedChildren - loadedChildren));
        }
        return result;
    }

    private static void AddDomChildren(InspectorNode parent, JsonElement node, string property,
        int depth, ref int remaining)
    {
        if (remaining <= 0 || !node.TryGetProperty(property, out var children) ||
            children.ValueKind != JsonValueKind.Array) return;

        foreach (var child in children.EnumerateArray())
        {
            if (remaining <= 0) break;
            parent.Children.Add(CreateDomNode(child, depth + 1, ref remaining));
        }
    }

    private async Task PopulateAccessibilityAsync(InspectorNode root)
    {
        var json = await CallAsync("Accessibility.getFullAXTree", "{}");
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("nodes", out var nodes)) return;
        foreach (var item in nodes.EnumerateArray().Take(1000))
        {
            var role = NestedValue(item, "role");
            var name = NestedValue(item, "name");
            root.Children.Add(new InspectorNode
            {
                Header = string.IsNullOrWhiteSpace(name) ? role : $"{role} · {Short(name, 100)}",
                Glyph = "○", Category = "Accessibility",
                Details = InspectorDataSanitizer.SanitizeAccessibility(item.GetRawText())
            });
        }
    }

    private async Task PopulateResourcesAsync(InspectorNode root)
    {
        var json = await CallAsync("Page.getResourceTree", "{}");
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("frameTree", out var tree))
        {
            var remaining = MaximumResourceNodes;
            root.Children.Add(CreateResourceFrame(tree, ref remaining));
            if (remaining <= 0)
                root.Children.Add(TruncatedNode($"Список ресурсов ограничен {MaximumResourceNodes:N0} элементами."));
        }
    }

    private static InspectorNode CreateResourceFrame(JsonElement tree, ref int remaining)
    {
        remaining--;
        var frame = tree.GetProperty("frame");
        var url = frame.TryGetProperty("url", out var urlValue)
            ? UrlService.SanitizeForDisplay(urlValue.GetString()) : string.Empty;
        var result = new InspectorNode { Header = "FRAME · " + Short(url, 120), Glyph = "▣", Category = "Resources", Details = InspectorDataSanitizer.SanitizeGeneral(frame.GetRawText()) };
        if (tree.TryGetProperty("resources", out var resources))
            foreach (var resource in resources.EnumerateArray())
            {
                if (remaining-- <= 0) break;
                var type = resource.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "Resource";
                var resourceUrl = resource.TryGetProperty("url", out var resourceUrlValue)
                    ? UrlService.SanitizeForDisplay(resourceUrlValue.GetString()) : string.Empty;
                result.Children.Add(new InspectorNode { Header = $"{type} · {Short(resourceUrl, 130)}", Glyph = "•", Category = "Resources", Details = InspectorDataSanitizer.SanitizeGeneral(resource.GetRawText()) });
            }
        if (tree.TryGetProperty("childFrames", out var children))
            foreach (var child in children.EnumerateArray())
            {
                if (remaining <= 0) break;
                result.Children.Add(CreateResourceFrame(child, ref remaining));
            }
        return result;
    }

    private async Task PopulateProtocolSnapshotAsync(InspectorNode root, string method)
    {
        var json = await CallAsync(method, "{}");
        if (json is null) return;
        root.Details = InspectorDataSanitizer.SanitizeGeneral(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
                root.Children.Add(new InspectorNode
                {
                    Header = property.Name,
                    Glyph = "•",
                    Category = root.Category,
                    Details = InspectorDataSanitizer.SanitizeGeneral(property.Value.GetRawText())
                });
        }
        catch (JsonException)
        {
            // The raw protocol response remains visible in the details panel.
        }
    }

    private async Task PopulateStorageAsync(InspectorNode root)
    {
        const string script = "JSON.stringify({local:Object.keys(localStorage).map(k=>({key:k,length:(localStorage.getItem(k)||'').length})),session:Object.keys(sessionStorage).map(k=>({key:k,length:(sessionStorage.getItem(k)||'').length})),cookies:document.cookie.split(';').map(x=>x.split('=')[0].trim()).filter(Boolean)})";
        try
        {
            var encoded = await _core.ExecuteScriptAsync(script);
            var json = JsonSerializer.Deserialize<string>(encoded) ?? "{}";
            using var document = JsonDocument.Parse(json);
            AddStorageGroup(root, document.RootElement, "local", "localStorage");
            AddStorageGroup(root, document.RootElement, "session", "sessionStorage");
            AddStorageGroup(root, document.RootElement, "cookies", "Cookie names");
        }
        catch (Exception ex)
        {
            root.Details = "Storage недоступен: " + ex.Message;
        }
    }

    private static void AddStorageGroup(InspectorNode root, JsonElement document, string property, string title)
    {
        var group = new InspectorNode { Header = title, Glyph = "▥", Category = "Storage" };
        root.Children.Add(group);
        if (!document.TryGetProperty(property, out var items)) return;
        foreach (var item in items.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : (item.TryGetProperty("key", out var key) ? key.GetString() : string.Empty) +
                  (item.TryGetProperty("length", out var length) ? $" · {length.GetInt32()} символов" : string.Empty);
            group.Children.Add(new InspectorNode { Header = text, Glyph = "•", Category = "Storage", Details = "Значение намеренно не извлекается." });
        }
    }

    private void AttachLiveDomains()
    {
        Subscribe("Network.requestWillBeSent", args => AddLiveNode(_networkRoot, "→",
            InspectorDataSanitizer.SanitizeNetworkEvent(args.ParameterObjectAsJson)));
        Subscribe("Network.responseReceived", args => AddLiveNode(_networkRoot, "←",
            InspectorDataSanitizer.SanitizeNetworkEvent(args.ParameterObjectAsJson)));
        Subscribe("Runtime.consoleAPICalled", args => AddLiveNode(_consoleRoot, ">",
            InspectorDataSanitizer.SanitizeConsoleEvent(args.ParameterObjectAsJson)));
        Subscribe("Log.entryAdded", args => AddLiveNode(_consoleRoot, "!",
            InspectorDataSanitizer.SanitizeConsoleEvent(args.ParameterObjectAsJson)));
    }

    private void Subscribe(string eventName, Action<CoreWebView2DevToolsProtocolEventReceivedEventArgs> action)
    {
        var receiver = _core.GetDevToolsProtocolEventReceiver(eventName);
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) => action(args);
        receiver.DevToolsProtocolEventReceived += handler;
        _unsubscribe.Add(() => receiver.DevToolsProtocolEventReceived -= handler);
    }

    private void AddLiveNode(InspectorNode? root, string glyph, string json)
    {
        if (root is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var summary = SummarizeEvent(json);
            root.Children.Add(new InspectorNode { Header = summary, Glyph = glyph, Category = root.Category, Details = json });
            while (root.Children.Count > 250) root.Children.RemoveAt(0);
        }));
    }

    private async void InspectorTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not InspectorNode node) return;
        SelectionTitle.Text = node.Header;
        DetailsBox.Text = node.Details;
        if (node.DomNodeId is not int nodeId) return;

        StatusText.Text = "Читаю стили, геометрию, события и accessibility…";
        var sections = new List<string> { "NODE\n" + node.Details };
        await AddSectionAsync(sections, "COMPUTED STYLE", "CSS.getComputedStyleForNode", $"{{\"nodeId\":{nodeId}}}");
        await AddSectionAsync(sections, "MATCHED CSS", "CSS.getMatchedStylesForNode", $"{{\"nodeId\":{nodeId}}}");
        await AddSectionAsync(sections, "BOX MODEL", "DOM.getBoxModel", $"{{\"nodeId\":{nodeId}}}");
        await AddSectionAsync(sections, "ACCESSIBILITY", "Accessibility.getPartialAXTree", $"{{\"nodeId\":{nodeId},\"fetchRelatives\":true}}");

        var resolved = await CallAsync("DOM.resolveNode", $"{{\"nodeId\":{nodeId}}}");
        if (resolved is not null)
        {
            using var document = JsonDocument.Parse(resolved);
            if (document.RootElement.TryGetProperty("object", out var remote) && remote.TryGetProperty("objectId", out var objectId))
                await AddSectionAsync(sections, "EVENT LISTENERS", "DOMDebugger.getEventListeners",
                    JsonSerializer.Serialize(new { objectId = objectId.GetString() }));
        }

        DetailsBox.Text = string.Join("\n\n", sections);
        StatusText.Text = "Данные выбранного DOM-узла загружены";
    }

    private async void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: InspectorNode node } ||
            !node.HasDeferredChildren || node.IsLoadingChildren || node.DomNodeId is not int nodeId)
            return;

        node.IsLoadingChildren = true;
        node.Children.Clear();
        node.Children.Add(new InspectorNode
        {
            Header = "Загрузка ветви…",
            Glyph = "⌛",
            Category = "Loading"
        });
        try
        {
            var json = await CallAsync("DOM.describeNode",
                $"{{\"nodeId\":{nodeId},\"depth\":5,\"pierce\":true}}");
            node.Children.Clear();
            if (json is null) return;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("node", out var described)) return;
            var remaining = MaximumDomNodes;
            var loaded = CreateDomNode(described, 0, ref remaining);
            foreach (var child in loaded.Children)
                node.Children.Add(child);
            node.Details = loaded.Details;
            node.HasDeferredChildren = loaded.HasDeferredChildren;
            if (node.Children.Count == 0)
                node.Children.Add(new InspectorNode
                {
                    Header = "Дочерних DOM-узлов нет",
                    Glyph = "·",
                    Category = "DOM"
                });
        }
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new InspectorNode
            {
                Header = "Не удалось загрузить ветвь",
                Glyph = "!",
                Category = "Error",
                Details = ex.Message
            });
        }
        finally
        {
            node.IsLoadingChildren = false;
        }
    }

    private async Task AddSectionAsync(List<string> sections, string title, string method, string arguments)
    {
        var result = await CallAsync(method, arguments);
        if (result is not null)
        {
            var safe = title == "ACCESSIBILITY"
                ? InspectorDataSanitizer.SanitizeAccessibility(result)
                : InspectorDataSanitizer.SanitizeGeneral(result);
            sections.Add(title + "\n" + safe);
        }
    }

    private async Task EnableDomainAsync(string method) => _ = await CallAsync(method, "{}");

    private async Task<string?> CallAsync(string method, string arguments)
    {
        try { return await _core.CallDevToolsProtocolMethodAsync(method, arguments); }
        catch (Exception ex)
        {
            StatusText.Text = $"{method}: {ex.Message}";
            return null;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ChromiumDevTools_Click(object sender, RoutedEventArgs e) => _core.OpenDevToolsWindow();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DetailsBox.Text)) Clipboard.SetText(DetailsBox.Text);
    }

    private void Find_Click(object sender, RoutedEventArgs e) => FindNext();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindNext();
        e.Handled = true;
    }

    private void FindNext()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        var matches = Flatten(_roots).Where(x => x.Header.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            StatusText.Text = "Совпадений нет";
            return;
        }
        _searchIndex = (_searchIndex + 1) % matches.Length;
        SelectContainer(InspectorTree, matches[_searchIndex]);
        StatusText.Text = $"Совпадение {_searchIndex + 1} из {matches.Length}";
    }

    private static bool SelectContainer(ItemsControl parent, InspectorNode target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (ReferenceEquals(item, target))
            {
                container.IsSelected = true;
                container.BringIntoView();
                return true;
            }
            container.IsExpanded = true;
            container.UpdateLayout();
            if (SelectContainer(container, target)) return true;
        }
        return false;
    }

    private static IEnumerable<InspectorNode> Flatten(IEnumerable<InspectorNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static int CountNodes(IEnumerable<InspectorNode> nodes) =>
        nodes.Sum(node => 1 + CountNodes(node.Children));

    private static string NestedValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var wrapper) && wrapper.TryGetProperty("value", out var value)
            ? value.ToString()
            : property;

    private static string SummarizeEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("request", out var request) && request.TryGetProperty("url", out var requestUrl))
                return Short(requestUrl.GetString(), 140);
            if (root.TryGetProperty("response", out var response) && response.TryGetProperty("url", out var responseUrl))
            {
                var status = response.TryGetProperty("status", out var statusValue) ? statusValue.ToString() + " · " : string.Empty;
                return status + Short(responseUrl.GetString(), 130);
            }
            if (root.TryGetProperty("type", out var type)) return type + " · " + Short(json, 120);
            return Short(json, 140);
        }
        catch { return Short(json, 140); }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    private static InspectorNode TruncatedNode(string details) => new()
    {
        Header = "… продолжение скрыто для защиты памяти",
        Glyph = "…",
        Category = "Limit",
        Details = details
    };

    private static InspectorNode DeferredNode(int count) => new()
    {
        Header = $"… загрузить ещё {count:N0} узлов при раскрытии",
        Glyph = "+",
        Category = "Deferred",
        Details = "Ветка загружается по требованию, чтобы большая страница не исчерпала память."
    };

    private static string Short(string? value, int length)
    {
        var text = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Length <= length ? text : text[..length] + "…";
    }
}
