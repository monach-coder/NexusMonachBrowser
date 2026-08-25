using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexusMonach.Services;
using NexusMonach.Views;

namespace NexusMonach.Models;

/// <summary>
/// Перевод страницы: захват сегментов, применение перевода, статус внутристраничного и озвучиваемого перевода
/// </summary>
public sealed partial class BrowserTab
{
    public async Task<IReadOnlyList<TranslationSegment>> CaptureTranslationSegmentsAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return [];
        var script = """
            (() => {
              const previous=window.__nexusPageTranslation;
              if(previous?.originals) for(const entry of previous.originals.values())
                if(entry.node?.isConnected) entry.node.nodeValue=entry.original;
              const state={nodes:new Map(),originals:new Map()};window.__nexusPageTranslation=state;
              const visible=e=>{const s=getComputedStyle(e),r=e.getBoundingClientRect();return s.display!=='none'&&s.visibility!=='hidden'&&r.width>120&&r.height>80};
              const candidates=[...document.querySelectorAll(__MAIN_CONTENT_SELECTOR__)]
                .filter(visible).map(e=>{const text=(e.innerText||'').trim(),links=[...e.querySelectorAll('a')].reduce((n,a)=>n+(a.innerText||'').length,0);return {e,score:text.length-links*.45}})
                .filter(x=>x.score>120).sort((a,b)=>b.score-a.score);
              const root=candidates[0]?.e||document.querySelector('main,[role="main"]')||document.body;if(!root)return [];
              const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);
              const nodes=[]; let node,total=0;
              while((node=walker.nextNode()) && nodes.length<100 && total<5500){
                const parent=node.parentElement, raw=node.nodeValue||'', text=raw.trim();
                if(!parent||text.length<2||parent.closest(__ARTICLE_EXCLUSION_SELECTOR__)) continue;
                const style=getComputedStyle(parent); if(style.display==='none'||style.visibility==='hidden'||style.opacity==='0'||parent.getClientRects().length===0) continue;
                nodes.push({node,raw,text}); total+=text.length;
              }
              return nodes.map((item,index)=>{
                const id='n'+(index+1);state.nodes.set(id,item.node);state.originals.set(id,{node:item.node,original:item.raw,text:item.text});
                const language=(item.node.parentElement?.closest('[lang]')?.getAttribute('lang')||document.documentElement.lang||'').trim();
                return {Id:id,Text:item.text,Language:language};
              });
            })();
            """
            .Replace("__MAIN_CONTENT_SELECTOR__",
                JsonSerializer.Serialize(PageTranslationPolicy.MainContentSelector), StringComparison.Ordinal)
            .Replace("__ARTICLE_EXCLUSION_SELECTOR__",
                JsonSerializer.Serialize(PageTranslationPolicy.ArticleExclusionSelector), StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script);
        try { return JsonSerializer.Deserialize<List<TranslationSegment>>(json) ?? []; }
        catch { return []; }
    }

    public async Task<IReadOnlyList<TranslationSegment>> CaptureInteractiveTranslationSegmentsAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return [];
        var script = """
            (()=>{
              const previous=window.__nexusInteractiveTranslation;
              if(previous?.entries)for(const entry of previous.entries.values())try{
                if(entry.kind==='text'&&entry.node?.isConnected)entry.node.nodeValue=entry.original;
                else if(entry.element?.isConnected)entry.element.setAttribute(entry.attribute,entry.original);
              }catch{}
              const state={entries:new Map()};window.__nexusInteractiveTranslation=state;
              const result=[],seen=new Set();let total=0,index=0;
              const visible=e=>{const s=getComputedStyle(e),r=e.getBoundingClientRect();return s.display!=='none'&&s.visibility!=='hidden'&&r.width>0&&r.height>0};
              const roots=[document];for(let i=0;i<roots.length;i++)for(const e of roots[i].querySelectorAll('*'))if(e.shadowRoot)roots.push(e.shadowRoot);
              const queryDeep=selector=>roots.flatMap(root=>[...root.querySelectorAll(selector)]);
              const language=e=>(e?.closest?.('[lang]')?.getAttribute('lang')||document.documentElement.lang||'').trim();
              const add=(key,text,entry,e)=>{text=(text||'').replace(/\s+/g,' ').trim();if(!text||text.length<1||text.length>500||seen.has(key)||result.length>=90||total+text.length>4200)return;seen.add(key);const id='f'+(++index);state.entries.set(id,entry);result.push({Id:id,Text:text,Language:language(e)});total+=text.length};
              const addAttribute=(e,attribute)=>{const value=e.getAttribute(attribute)||'';if(value.trim())add('a:'+attribute+':'+index+':'+value,value,{kind:'attribute',element:e,attribute,original:value},e)};
              const addText=(root,force=false)=>{if(!root||(!force&&!visible(root)))return;const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);let node;while((node=walker.nextNode())){const raw=node.nodeValue||'',text=raw.trim(),parent=node.parentElement;if(!parent||!text||parent.closest('script,style,noscript,input,textarea,[contenteditable="true"]'))continue;add('t:'+index+':'+text,text,{kind:'text',node,original:raw},parent)}};
              const translatableInputTypes=__TRANSLATABLE_INPUT_TYPES__;
              const translatableAttributes=__TRANSLATABLE_ATTRIBUTES__;
              const controls=queryDeep(__INTERACTIVE_SELECTOR__).filter(visible).slice(0,140);
              for(const control of controls){
                for(const attribute of translatableAttributes)addAttribute(control,attribute);
                const type=(control.getAttribute('type')||'').toLowerCase();
                if(control.tagName==='INPUT'&&translatableInputTypes.includes(type))addAttribute(control,'value');
                if(control.tagName==='BUTTON'||control.tagName==='A'||control.tagName==='SUMMARY'||['button','menuitem','tab','option'].includes(control.getAttribute('role')||''))addText(control);
                for(const image of control.querySelectorAll?.('img[alt],input[type="image"][alt]')||[])addAttribute(image,'alt');
                for(const label of control.labels||[])addText(label);
                const parentLabel=control.closest('label');if(parentLabel)addText(parentLabel);
                const id=control.id,controlRoot=control.getRootNode?.()||document;if(id)for(const label of controlRoot.querySelectorAll?.('label[for="'+CSS.escape(id)+'"]')||[])addText(label);
                if(control.tagName==='SELECT')for(const option of [...control.options].slice(0,30))addText(option,true);
              }
              for(const item of queryDeep('form legend,[role="form"] legend,form [role="alert"],form [aria-live],form small,form .error,form [class*="hint" i],[role="form"] [role="alert"]'))addText(item);
              return result;
            })()
            """
            .Replace("__INTERACTIVE_SELECTOR__",
                JsonSerializer.Serialize(PageTranslationPolicy.InteractiveSelector), StringComparison.Ordinal)
            .Replace("__TRANSLATABLE_INPUT_TYPES__",
                JsonSerializer.Serialize(PageTranslationPolicy.TranslatableInputValueTypes),
                StringComparison.Ordinal)
            .Replace("__TRANSLATABLE_ATTRIBUTES__",
                JsonSerializer.Serialize(PageTranslationPolicy.TranslatableAttributes),
                StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script);
        try { return JsonSerializer.Deserialize<List<TranslationSegment>>(json) ?? []; }
        catch { return []; }
    }

    public async Task<int> ApplyInteractiveTranslationSegmentsAsync(
        IReadOnlyList<TranslationSegment> translated, int completedBefore, int total)
    {
        if (Core is null || translated.Count == 0) return 0;
        var script = """
            (()=>{const items=__ITEMS__;const state=window.__nexusInteractiveTranslation;if(!state)return 0;let applied=0;
              for(const item of items){const entry=state.entries.get(item.Id);if(!entry)continue;
                try{if(entry.kind==='text'&&entry.node?.isConnected){const lead=entry.original.match(/^\s*/)?.[0]||'',tail=entry.original.match(/\s*$/)?.[0]||'';entry.node.nodeValue=lead+item.Text+tail;applied++}
                else if(entry.element?.isConnected){entry.element.setAttribute(entry.attribute,item.Text);applied++}}catch{}
              }
              const status=document.getElementById('nexus-translation-status');if(status)status.textContent='Озвучивание статьи · переведено элементов интерфейса: '+(__COMPLETED__+applied)+' / '+__TOTAL__;return applied})()
            """
            .Replace("__ITEMS__", JsonSerializer.Serialize(translated), StringComparison.Ordinal)
            .Replace("__COMPLETED__", JsonSerializer.Serialize(completedBefore), StringComparison.Ordinal)
            .Replace("__TOTAL__", JsonSerializer.Serialize(total), StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script);
        return int.TryParse(json, out var applied) ? applied : 0;
    }

    public async Task BeginInPageTranslationAsync(int total)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (() => {
              document.getElementById('nexus-translation-toolbar')?.remove();
              const box=document.createElement('div'); box.id='nexus-translation-toolbar';
              box.dataset.nexusTranslationUi='true';
              box.style.cssText='position:fixed;right:22px;top:22px;z-index:2147483647;display:flex;align-items:center;gap:10px;max-width:440px;padding:11px 13px;border:1px solid #80ffffff;border-radius:14px;background:#b3101010;color:#fff;box-shadow:0 12px 36px #0008;font:600 13px Segoe UI,Arial,sans-serif;backdrop-filter:blur(12px);';
              const status=document.createElement('span'); status.id='nexus-translation-status'; status.textContent='Озвучивание страницы · 0 / '+{{total}};
              const restore=document.createElement('button'); restore.textContent='Вернуть интерфейс';
              restore.style.cssText='border:1px solid #66ffffff;border-radius:8px;background:#26ffffff;color:#fff;padding:6px 9px;cursor:pointer;';
              restore.onclick=()=>{const state=window.__nexusInteractiveTranslation;if(state?.entries)for(const entry of state.entries.values())try{if(entry.kind==='text'&&entry.node?.isConnected)entry.node.nodeValue=entry.original;else if(entry.element?.isConnected)entry.element.setAttribute(entry.attribute,entry.original)}catch{}window.__nexusInteractiveTranslation=null;box.remove();};
              const close=document.createElement('button'); close.textContent='×'; close.title='Скрыть панель, оставив перевод';
              close.style.cssText='border:0;background:transparent;color:#fff;font-size:18px;cursor:pointer;padding:2px 5px;'; close.onclick=()=>box.remove();
              box.append(status,restore,close); document.documentElement.append(box);
            })();
            """);
    }

    public async Task UpdateSpokenPageTranslationStatusAsync(int completed, int total, int translatedControls)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (()=>{const status=document.getElementById('nexus-translation-status');if(status)status.textContent={{JsonSerializer.Serialize($"Озвучено фрагментов статьи: {completed} / {total} · интерфейс: {translatedControls}")}}})()
            """);
    }

    public async Task CompleteSpokenPageTranslationAsync(int spoken, int total, int translatedControls,
        string? error = null)
    {
        if (Core is null) return;
        var message = error is null
            ? $"Статья озвучена: {spoken} из {total} · интерфейс: {translatedControls}"
            : "Озвучивание остановлено: " + error;
        var script = """
            (()=>{const status=document.getElementById('nexus-translation-status');if(status){status.textContent=__MESSAGE__;status.style.color=__COLOR__;}})()
            """
            .Replace("__MESSAGE__", JsonSerializer.Serialize(message), StringComparison.Ordinal)
            .Replace("__COLOR__", JsonSerializer.Serialize(error is null ? "#7ff5e7" : "#ffcb6b"), StringComparison.Ordinal);
        await Core.ExecuteScriptAsync(script);
    }

    public async Task<int> ApplyTranslationSegmentsAsync(IReadOnlyList<TranslationSegment> translated, int completedBefore, int total)
    {
        if (Core is null || translated.Count == 0) return 0;
        var json = await Core.ExecuteScriptAsync($$"""
            (() => {
              const items={{JsonSerializer.Serialize(translated)}};
              const state=window.__nexusPageTranslation;if(!state)return 0;
              let applied=0;
              for(const item of items){
                let node=state.nodes.get(item.Id);const entry=state.originals.get(item.Id);if(!entry)continue;
                // React/Vue pages may replace text nodes while the local model is
                // working. Rebind to the current visible node with the same source
                // text instead of reporting a translation that was never shown.
                if(!node?.isConnected){
                  const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);let candidate;
                  while((candidate=walker.nextNode())) { const parent=candidate.parentElement;if(!parent||parent.closest('[data-nexus-translation-ui],script,style,noscript,textarea,code,pre'))continue;if((candidate.nodeValue||'').trim()===entry.text){node=candidate;state.nodes.set(item.Id,node);entry.node=node;break;} }
                }
                if(!node?.isConnected)continue;
                const original=entry.original||node.nodeValue||'';
                const lead=original.match(/^\s*/)?.[0]||'', tail=original.match(/\s*$/)?.[0]||'';
                node.nodeValue=lead+item.Text+tail;applied++;
              }
              const status=document.getElementById('nexus-translation-status'); if(status) status.textContent='Перевод: '+({{completedBefore}}+applied)+' / '+{{total}};
              return applied;
            })();
            """);
        return int.TryParse(json, out var applied) ? applied : 0;
    }

    public async Task CompleteInPageTranslationAsync(int translated, int total, string? error = null)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (() => { const status=document.getElementById('nexus-translation-status'); if(status){
              status.textContent={{JsonSerializer.Serialize(error is null ? $"Переведено элементов: {translated} из {total}" : "Перевод остановлен: " + error)}};
              status.style.color={{JsonSerializer.Serialize(error is null ? "#7ff5e7" : "#ffcb6b")}};
              }
            })();
            """);
    }
}
