using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SelectedTextTranslationService
{
    private sealed record PreparedSelection(string Id, string Text, string Language);

    public static void Attach(BrowserTab tab, CoreWebView2 core, Action<string> reportStatus)
    {
        core.ContextMenuRequested += (_, args) =>
        {
            using var deferral = args.GetDeferral();
            try
            {
                var location = args.Location;
                if (!string.IsNullOrWhiteSpace(args.ContextMenuTarget.SelectionText))
                {
                    var translate = core.Environment.CreateContextMenuItem(
                        "Перевести выделенное на русский", null, CoreWebView2ContextMenuItemKind.Command);
                    translate.CustomItemSelected += async (_, _) =>
                        await TranslateSelectionAsync(tab, core, reportStatus);
                    args.MenuItems.Insert(0, translate);
                }

                var inspect = core.Environment.CreateContextMenuItem(
                    "Открыть в инструментах разработчика", null, CoreWebView2ContextMenuItemKind.Command);
                inspect.CustomItemSelected += async (_, _) =>
                    await InspectAtAsync(core, location.X, location.Y, reportStatus);
                args.MenuItems.Add(inspect);
            }
            catch (Exception ex) { reportStatus("Контекстное меню: " + ex.Message); }
            finally { deferral.Complete(); }
        };
    }

    private static async Task TranslateSelectionAsync(BrowserTab tab, CoreWebView2 core, Action<string> reportStatus)
    {
        var source = core.Source;
        string? pendingId = null;
        try
        {
            var preparedJson = await core.ExecuteScriptAsync(PrepareSelectionScript);
            var prepared = JsonSerializer.Deserialize<PreparedSelection>(preparedJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (prepared is null || string.IsNullOrWhiteSpace(prepared.Id) || string.IsNullOrWhiteSpace(prepared.Text))
            {
                reportStatus("Это выделение нельзя перевести: поля ввода, редактируемые и интерактивные элементы защищены.");
                return;
            }
            pendingId = prepared.Id;

            reportStatus("Локальный перевод выделения…");
            // Текст страницы — недоверенные данные. Сервис перевода получает только текст,
            // а системный prompt LocalIntelligenceService запрещает выполнять инструкции из DOM.
            using var translationBudget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var translated = LocalTranslationDictionary.TryTranslate(prepared.Text, out var instant)
                ? instant
                : await LocalIntelligenceService.TranslateToRussianAsync(
                    prepared.Text, translationBudget.Token, prepared.Language);
            if (!ReferenceEquals(tab.Core, core) || !string.Equals(core.Source, source, StringComparison.Ordinal))
            {
                reportStatus("Перевод отменён: вкладка перешла на другую страницу.");
                return;
            }

            var applied = await ApplyTranslationAsync(core, prepared.Id, translated);
            if (!applied) await CancelPendingAsync(core, prepared.Id);
            reportStatus(applied ? "Выделенный текст переведён локально." : "Перевод отменён: выделение или документ изменились.");
        }
        catch (OperationCanceledException)
        {
            if (pendingId is not null) await CancelPendingAsync(core, pendingId);
            reportStatus("Перевод выделения отменён.");
        }
        catch (Exception ex)
        {
            if (pendingId is not null) try { await CancelPendingAsync(core, pendingId); } catch { }
            reportStatus("Ошибка перевода выделения: " + ex.Message);
        }
    }

    private static async Task<bool> ApplyTranslationAsync(CoreWebView2 core, string id, string translated)
    {
        var json = await core.ExecuteScriptAsync("""
            ((id,text)=>{const state=window.__nexusInlineTranslations,entry=state?.pending?.get(id);
              if(!entry||entry.document!==document||!entry.node?.isConnected||entry.node.nodeValue!==entry.original)return false;
              entry.node.nodeValue=entry.original.slice(0,entry.start)+text+entry.original.slice(entry.end);
              state.pending.delete(id);return true})(__ID__,__TEXT__)
            """.Replace("__ID__", JsonSerializer.Serialize(id), StringComparison.Ordinal)
                .Replace("__TEXT__", JsonSerializer.Serialize(translated), StringComparison.Ordinal));
        return bool.TryParse(json, out var applied) && applied;
    }

    private static async Task CancelPendingAsync(CoreWebView2 core, string id) =>
        await core.ExecuteScriptAsync("""
            ((id)=>{const state=window.__nexusInlineTranslations,entry=state?.pending?.get(id);if(!entry)return;
              state.pending.delete(id)})(__ID__)
            """.Replace("__ID__", JsonSerializer.Serialize(id), StringComparison.Ordinal));

    private static async Task InspectAtAsync(CoreWebView2 core, int x, int y, Action<string> reportStatus)
    {
        try
        {
            core.OpenDevToolsWindow();
            await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0,\"pierce\":true}");
            var nodeJson = await core.CallDevToolsProtocolMethodAsync("DOM.getNodeForLocation",
                JsonSerializer.Serialize(new { x, y, includeUserAgentShadowDOM = true, ignorePointerEventsNone = true }));
            using var document = JsonDocument.Parse(nodeJson);
            if (!document.RootElement.TryGetProperty("nodeId", out var nodeId))
                throw new InvalidOperationException("DOM-узел под курсором не найден.");
            await core.CallDevToolsProtocolMethodAsync("DOM.setInspectedNode",
                JsonSerializer.Serialize(new { nodeId = nodeId.GetInt32() }));
            reportStatus("DevTools открыт; DOM-узел передан во вкладку Elements.");
        }
        catch (Exception ex)
        {
            core.OpenDevToolsWindow();
            reportStatus("DevTools открыт. Точный выбор недоступен в этой версии WebView2; используй встроенную команду Inspect element. " + ex.Message);
        }
    }

    private const string PrepareSelectionScript = """
        (()=>{const selection=getSelection();if(!selection||selection.rangeCount!==1||selection.isCollapsed)return null;
          const range=selection.getRangeAt(0),text=selection.toString().trim().slice(0,12000);if(!text)return null;
          const forbidden='script,style,input,textarea,select,option,[contenteditable]:not([contenteditable="false"]),a,button,[role="button"],[role="link"],[tabindex]';
          const ancestor=node=>(node.nodeType===Node.ELEMENT_NODE?node:node.parentElement)?.closest?.(forbidden);
          if(ancestor(range.startContainer)||ancestor(range.endContainer))return null;
          if(range.startContainer!==range.endContainer||range.startContainer.nodeType!==Node.TEXT_NODE)return null;
          const node=range.startContainer,original=node.nodeValue||'',start=range.startOffset,end=range.endOffset;
          if(start<0||end<=start||end>original.length)return null;
          const id=crypto.randomUUID();window.__nexusInlineTranslations??={pending:new Map()};
          window.__nexusInlineTranslations.pending.set(id,{document,node,original,start,end,text});selection.removeAllRanges();
          const language=(node.parentElement?.closest('[lang]')?.getAttribute('lang')||document.documentElement.lang||'').trim();
          return {id,text,language};})()
        """;
}
