using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SelectedTextTranslationService
{
    private sealed record PreparedSelection(string Id, string Text);

    public static void Attach(BrowserTab tab, CoreWebView2 core, Action<string> reportStatus)
    {
        core.ContextMenuRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            try
            {
                var location = args.Location;
                var translatedId = await FindTranslationAtAsync(core, location.X, location.Y);

                if (!string.IsNullOrWhiteSpace(args.ContextMenuTarget.SelectionText))
                {
                    var translate = core.Environment.CreateContextMenuItem(
                        "Перевести выделенное на русский", null, CoreWebView2ContextMenuItemKind.Command);
                    translate.CustomItemSelected += async (_, _) =>
                        await TranslateSelectionAsync(tab, core, reportStatus);
                    args.MenuItems.Insert(0, translate);
                }

                if (!string.IsNullOrWhiteSpace(translatedId))
                {
                    var restore = core.Environment.CreateContextMenuItem(
                        "Вернуть оригинал", null, CoreWebView2ContextMenuItemKind.Command);
                    restore.CustomItemSelected += async (_, _) =>
                    {
                        try
                        {
                            var restored = await RestoreAsync(core, translatedId);
                            reportStatus(restored ? "Оригинальный фрагмент восстановлен." : "Переведённый элемент больше не существует.");
                        }
                        catch (Exception ex) { reportStatus("Не удалось вернуть оригинал: " + ex.Message); }
                    };
                    args.MenuItems.Insert(0, restore);
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
            var translated = await LocalIntelligenceService.TranslateToRussianAsync(prepared.Text);
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

    private static async Task<string> FindTranslationAtAsync(CoreWebView2 core, int x, int y)
    {
        var json = await core.ExecuteScriptAsync($$"""
            (()=>document.elementFromPoint({{x}},{{y}})?.closest('[data-nexus-inline-translation]')?.dataset.nexusInlineTranslation||'')()
            """);
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static async Task<bool> ApplyTranslationAsync(CoreWebView2 core, string id, string translated)
    {
        var json = await core.ExecuteScriptAsync("""
            ((id,text)=>{const state=window.__nexusInlineTranslations,entry=state?.pending?.get(id);
              if(!entry||entry.document!==document||!entry.start.isConnected||!entry.end.isConnected)return false;
              const range=document.createRange();range.setStartAfter(entry.start);range.setEndBefore(entry.end);
              const original=range.cloneContents();range.deleteContents();
              const span=document.createElement('span');span.dataset.nexusInlineTranslation=id;span.title=entry.text;span.textContent=text;
              range.insertNode(span);entry.start.remove();entry.end.remove();state.pending.delete(id);state.originals.set(id,original);return true})(__ID__,__TEXT__)
            """.Replace("__ID__", JsonSerializer.Serialize(id), StringComparison.Ordinal)
                .Replace("__TEXT__", JsonSerializer.Serialize(translated), StringComparison.Ordinal));
        return bool.TryParse(json, out var applied) && applied;
    }

    private static async Task<bool> RestoreAsync(CoreWebView2 core, string id)
    {
        var json = await core.ExecuteScriptAsync("""
            ((id)=>{const state=window.__nexusInlineTranslations,span=document.querySelector('[data-nexus-inline-translation="'+CSS.escape(id)+'"]'),fragment=state?.originals?.get(id);
              if(!span||!fragment)return false;span.replaceWith(fragment);state.originals.delete(id);return true})(__ID__)
            """.Replace("__ID__", JsonSerializer.Serialize(id), StringComparison.Ordinal));
        return bool.TryParse(json, out var restored) && restored;
    }

    private static async Task CancelPendingAsync(CoreWebView2 core, string id) =>
        await core.ExecuteScriptAsync("""
            ((id)=>{const state=window.__nexusInlineTranslations,entry=state?.pending?.get(id);if(!entry)return;
              entry.start?.remove();entry.end?.remove();state.pending.delete(id)})(__ID__)
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
          for(const element of document.querySelectorAll(forbidden))if(range.intersectsNode(element))return null;
          const id=crypto.randomUUID(),start=document.createComment('nexus-inline-start:'+id),end=document.createComment('nexus-inline-end:'+id);
          const endRange=range.cloneRange();endRange.collapse(false);endRange.insertNode(end);
          const startRange=range.cloneRange();startRange.collapse(true);startRange.insertNode(start);
          window.__nexusInlineTranslations??={pending:new Map(),originals:new Map()};
          window.__nexusInlineTranslations.pending.set(id,{document,start,end,text});selection.removeAllRanges();
          return {id,text};})()
        """;
}
