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
/// Исследование страницы: читаемый текст, ссылки, снапшоты DOM, поиск по сайту, Shopping-агент
/// </summary>
public sealed partial class BrowserTab
{
    public async Task<string> GetReadablePageTextAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl))
            return string.Empty;

        var json = await Core.ExecuteScriptAsync("""
            (() => {
              const clone = document.body ? document.body.cloneNode(true) : null;
              if (!clone) return '';
              clone.querySelectorAll('script, style, noscript, svg, canvas, iframe').forEach(node => node.remove());
              const text = (clone.innerText || clone.textContent || '')
                .replace(/\n{3,}/g, '\n\n')
                .replace(/[ \t]{2,}/g, ' ')
                .trim();
              return text.slice(0, 30000);
            })();
            """);
        try { return JsonSerializer.Deserialize<string>(json) ?? string.Empty; }
        catch { return string.Empty; }
    }

    public async Task<IReadOnlyList<string>> GetResearchLinksAsync(string query, int maximum = 12)
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return [];
        maximum = Math.Clamp(maximum, 1, 30);
        var queryJson = JsonSerializer.Serialize(query ?? string.Empty);
        var json = await Core.ExecuteScriptAsync($$"""
            (() => {
              const query = {{queryJson}}.toLocaleLowerCase();
              const maximum = {{maximum}};
              const terms = query.split(/[^\p{L}\p{N}]+/u).filter(x => x.length > 2).slice(0, 12);
              const blocked = /(login|signin|sign-in|account|profile|cart|basket|checkout|payment|pay|order|logout|register|auth)/i;
              const current = new URL(location.href);
              const visible = element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
              };
              const unique = new Map();
              for (const anchor of document.querySelectorAll('a[href]')) {
                if (!visible(anchor)) continue;
                let url;
                try { url = new URL(anchor.href, location.href); } catch (_) { continue; }
                if (!/^https?:$/.test(url.protocol) || url.origin !== current.origin) continue;
                url.hash = '';
                if (url.href === current.href || blocked.test(url.pathname + url.search)) continue;
                const text = ((anchor.innerText || anchor.getAttribute('aria-label') || anchor.title || '') + ' ' +
                  (anchor.closest('article,main,section,li')?.innerText || '')).replace(/\s+/g, ' ').trim().slice(0, 900);
                if (text.length < 8) continue;
                let score = anchor.closest('article,main') ? 3 : 0;
                for (const term of terms) if (text.toLocaleLowerCase().includes(term)) score += 4;
                if (/article|story|news|guide|docs|help|wiki|blog|review|research|report/i.test(url.pathname)) score += 2;
                const existing = unique.get(url.href);
                if (!existing || existing.score < score) unique.set(url.href, { url: url.href, score });
              }
              return [...unique.values()]
                .sort((a, b) => b.score - a.score || a.url.localeCompare(b.url))
                .slice(0, maximum)
                .map(x => x.url);
            })();
            """);
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    public async Task<string> GetAgentDomSnapshotAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return "[]";
        _agentDomToken = Guid.NewGuid().ToString("N");
        return await Core.ExecuteScriptAsync($$"""
            (() => {
              const visible = e => { const r=e.getBoundingClientRect(), s=getComputedStyle(e); return r.width>1 && r.height>1 && s.visibility!=='hidden' && s.display!=='none'; };
              const elements=[...document.querySelectorAll('a,button,input,select,textarea,[role="button"],[tabindex]')].filter(visible).slice(0,120);
              return elements.map((e,i)=>{
                const id='n'+(i+1); e.dataset.nexusAgentId=id; e.dataset.nexusAgentToken={{JsonSerializer.Serialize(_agentDomToken)}};
                let href=''; if(e.tagName==='A'&&e.href){ try { const u=new URL(e.href); href=u.origin+u.pathname; } catch {} }
                return { id, tag:e.tagName.toLowerCase(), type:(e.type||''), text:(e.innerText||e.getAttribute('aria-label')||e.placeholder||'').trim().slice(0,180),
                  placeholder:(e.placeholder||'').slice(0,100), href:href.slice(0,300) };
              });
            })();
            """);
    }

    public async Task<IReadOnlyList<string>> ExecuteAgentPlanAsync(AgentPlan plan)
    {
        if (Core is null) throw new InvalidOperationException("Страница не готова.");
        var results = new List<string>();
        string[] forbidden = ["купить", "оплат", "заказать", "отправить", "удалить", "пароль", "purchase", "pay", "submit", "delete", "password", "login"];
        foreach (var step in plan.Steps)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(step.ElementId ?? string.Empty, @"^n\d+$"))
            {
                results.Add("Пропущено: неверный elementId.");
                continue;
            }
            var combined = (step.Description + " " + step.Value).ToLowerInvariant();
            if (forbidden.Any(word => combined.Contains(word, StringComparison.Ordinal)))
            {
                results.Add("Заблокировано опасное действие: " + step.Description);
                continue;
            }
            var script = $$"""
                (() => {
                  const e=document.querySelector('[data-nexus-agent-token={{JsonSerializer.Serialize(_agentDomToken)}}][data-nexus-agent-id="{{step.ElementId}}"]');
                  if(!e) return 'элемент не найден';
                  const action={{JsonSerializer.Serialize(step.Action)}};
                  if(action==='highlight'){ e.style.outline='3px solid #36d7c4'; e.scrollIntoView({behavior:'smooth',block:'center'}); return 'подсвечено'; }
                  if(action==='scroll'){ e.scrollIntoView({behavior:'smooth',block:'center'}); return 'прокручено'; }
                  if(action==='fill'){
                    if(!['INPUT','TEXTAREA','SELECT'].includes(e.tagName)) return 'не поле ввода';
                    const type=(e.type||'').toLowerCase(), ac=(e.autocomplete||'').toLowerCase();
                    if(['password','file','hidden'].includes(type)||/password|cc-|card|one-time-code/.test(ac)) return 'чувствительное поле заблокировано';
                    e.value={{JsonSerializer.Serialize((step.Value ?? string.Empty)[..Math.Min(step.Value?.Length ?? 0, 500)])}};
                    e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); return 'заполнено';
                  }
                  if(action==='click'){
                    if(!['A','BUTTON'].includes(e.tagName)||(e.type||'').toLowerCase()==='submit') return 'клик заблокирован';
                    const label=(e.innerText||e.getAttribute('aria-label')||'').toLowerCase();
                    if(/купить|оплат|заказать|отправить|удалить|purchase|pay|submit|delete|login/.test(label)) return 'опасный клик заблокирован';
                    e.click(); return 'нажато';
                  }
                  return 'неизвестное действие';
                })();
                """;
            var json = await Core.ExecuteScriptAsync(script);
            string result;
            try { result = JsonSerializer.Deserialize<string>(json) ?? json; }
            catch { result = json; }
            results.Add(step.Description + ": " + result);
            await Task.Delay(250);
        }
        return results;
    }

    public async Task<bool> SearchCurrentSiteForAgentAsync(string query, CancellationToken cancellationToken = default)
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl) || string.IsNullOrWhiteSpace(query)) return false;
        // Prefer the site's own GET search architecture. It is deterministic,
        // keeps the current authentication/cookies in WebView2 and avoids ranking
        // unrelated cards when a JavaScript key event was ignored.
        var searchUrlScript = """
            (()=>{const visible=e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>80&&r.height>15&&s.display!=='none'&&s.visibility!=='hidden'};
              const inputs=[...document.querySelectorAll('form input[name],input[type="search"][name]')].filter(visible).map(e=>{const hint=((e.type||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.getAttribute('aria-label')||'')).toLowerCase();return {e,score:(e.type==='search'?8:0)+(/search|query|поиск|найти|товар|text|q/.test(hint)?6:0)}}).sort((a,b)=>b.score-a.score);
              const input=inputs[0]?.e;if(!input||inputs[0].score<5)return '';
              const form=input.closest('form');if(form&&String(form.method||'get').toLowerCase()==='post')return '';
              try{const target=new URL(form?.action||location.href,location.href);if(target.origin!==location.origin)return '';target.searchParams.set(input.name,__NEXUS_QUERY__);return target.href}catch{return ''}})();
            """.Replace("__NEXUS_QUERY__", JsonSerializer.Serialize(query[..Math.Min(query.Length, 300)]), StringComparison.Ordinal);
        var searchUrlJson = await Core.ExecuteScriptAsync(searchUrlScript);
        try
        {
            var searchUrl = JsonSerializer.Deserialize<string>(searchUrlJson);
            if (!string.IsNullOrWhiteSpace(searchUrl) && !searchUrl.Equals(CurrentUrl, StringComparison.OrdinalIgnoreCase) &&
                await NavigateAndWaitAsync(searchUrl, TimeSpan.FromSeconds(20)))
                return true;
        }
        catch (JsonException swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "SearchCurrentSiteForAgentAsync", swallowed);
        }
        var beforeUrl = CurrentUrl;
        var beforeFingerprint = await GetShoppingCatalogFingerprintAsync();
        var searchScript = $$"""
            (() => {
              const visible=e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>80&&r.height>15&&s.display!=='none'&&s.visibility!=='hidden';};
              const inputs=[...document.querySelectorAll('input')].filter(visible).map(e=>{
                const hint=((e.type||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.getAttribute('aria-label')||'')).toLowerCase();
                let score=(e.type==='search'?8:0)+(/search|поиск|найти|товар/.test(hint)?6:0)-(e.type==='password'?100:0);
                return {e,score};
              }).sort((a,b)=>b.score-a.score);
              const input=inputs[0]; if(!input||input.score<4) return false;
              const e=input.e, value={{JsonSerializer.Serialize(query[..Math.Min(query.Length, 300)])}};
              const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
              if(setter) setter.call(e,value); else e.value=value;
              e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); e.focus();
              const form=e.closest('form');
              if(form){ if(form.requestSubmit) form.requestSubmit(); else form.submit(); }
              else { e.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',bubbles:true}));
                     e.dispatchEvent(new KeyboardEvent('keyup',{key:'Enter',code:'Enter',bubbles:true})); }
              return true;
            })();
            """;
        var json = await Core.ExecuteScriptAsync(searchScript);
        if (!bool.TryParse(json, out var initialFound) || !initialFound)
        {
            // Many stores initially expose only a magnifier button. Opening that
            // control is a reversible UI action; the agent still never purchases,
            // signs in or submits anything except the explicit search query.
            var openedJson = await Core.ExecuteScriptAsync("""
                (()=>{const visible=e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>8&&r.height>8&&s.display!=='none'&&s.visibility!=='hidden'};
                  const controls=[...document.querySelectorAll('button,[role="button"],a')].filter(visible);
                  const search=controls.find(e=>/^(search|поиск|найти|искать)$/i.test(((e.innerText||'')+' '+(e.getAttribute('aria-label')||'')+' '+(e.title||'')).trim()));
                  if(!search)return false;search.click();return true;})()
                """);
            if (bool.TryParse(openedJson, out var opened) && opened)
            {
                await Task.Delay(650, cancellationToken);
                json = await Core.ExecuteScriptAsync(searchScript);
            }
        }
        if (!bool.TryParse(json, out var found) || !found) return false;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(500, cancellationToken);
            if (!CurrentUrl.Equals(beforeUrl, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var current = await GetShoppingCatalogFingerprintAsync();
                if (!string.IsNullOrWhiteSpace(current) && !current.Equals(beforeFingerprint, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception swallowed)
            {
                Services.SwallowLog.Log("browser-tab", "SearchCurrentSiteForAgentAsync", swallowed);
            }
        }
        // A submitted form is not proof that a catalogue was updated. Returning
        // true here made the agent rank unrelated cards from the landing page.
        return false;
    }

    private async Task<string> GetShoppingCatalogFingerprintAsync()
    {
        if (Core is null) return string.Empty;
        var json = await Core.ExecuteScriptAsync("""
            (()=>{const selectors='[itemtype*="Product"],[data-product-id],[data-nm-id],[data-sku],article,li[class*="product" i],[class*="product-card" i],[role="listitem"]';
              const nodes=[...document.querySelectorAll(selectors)].slice(0,120);const sample=nodes.slice(0,12).map(e=>(e.innerText||'').replace(/\s+/g,' ').slice(0,120)).join('|');
              return location.href+'#'+nodes.length+'#'+document.documentElement.scrollHeight+'#'+sample;})()
            """);
        try { return JsonSerializer.Deserialize<string>(json) ?? string.Empty; }
        catch (JsonException) { return string.Empty; }
    }

    public async Task<string> ExtractShoppingCardsAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return "[]";
        return await Core.ExecuteScriptAsync("""
            (() => {
              const result=[], seen=new Set();
              const visible=e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>70&&r.height>25&&s.display!=='none'&&s.visibility!=='hidden'&&s.opacity!=='0';};
              const clean=value=>(value||'').replace(/\s+/g,' ').trim();
              const currency=/(?:\d[\d\s.,]{0,14})\s*(?:₽|руб\.?|RUB|\$|€|£|¥|₸|₴)|(?:₽|руб\.?|RUB|\$|€|£|¥|₸|₴)\s*\d[\d\s.,]{0,14}/i;
              const ratingPattern=/(?:рейтинг|rating|оценка)?\s*[0-5][.,]\d\s*(?:из\s*5)?/i;
              const buyersPattern=/\d[\d\s.,]*\s*(?:купили|купило|покупок|заказов|отзыв(?:а|ов)?|оцен(?:ка|ок)|sold|reviews?|ratings?)/i;
              const sameSite=(a,b)=>a===b||a.endsWith('.'+b)||b.endsWith('.'+a);
              const safeUrl=value=>{try{const u=new URL(value,location.href);if(!/^https?:$/.test(u.protocol)||!sameSite(u.hostname,location.hostname))return '';u.hash='';return u.origin+u.pathname+u.search;}catch{return ''}};
              const safeImage=value=>{try{const u=new URL(value,location.href);if(!/^https?:$/.test(u.protocol))return '';return u.href.slice(0,1600)}catch{return ''}};
              const bestImage=(image,host)=>{
                if(image){
                  const srcset=image.currentSrc||image.getAttribute('srcset')||image.getAttribute('data-srcset')||'';
                  const selected=srcset.includes(',')?srcset.split(',').at(-1).trim().split(/\s+/)[0]:srcset.trim().split(/\s+/)[0];
                  const direct=selected||image.src||image.getAttribute('data-src')||image.getAttribute('data-original')||image.getAttribute('data-lazy-src');
                  if(direct)return direct;
                }
                const picture=host?.querySelector('picture source[srcset],picture source[data-srcset]');
                const pictureSet=picture?.getAttribute('srcset')||picture?.getAttribute('data-srcset')||'';
                if(pictureSet)return pictureSet.split(',').at(-1).trim().split(/\s+/)[0];
                const styled=[host,...(host?[...host.querySelectorAll('*')].slice(0,24):[])].find(x=>x&&/url\(/i.test(getComputedStyle(x).backgroundImage||''));
                const match=styled&&getComputedStyle(styled).backgroundImage.match(/url\(["']?([^"')]+)["']?\)/i);
                return match?.[1]||'';
              };
              const add=(name,text,url,price='',rating='',buyers='',source='DOM',imageUrl='')=>{
                name=clean(name).slice(0,220);text=clean(text).slice(0,1200);url=safeUrl(url);imageUrl=safeImage(imageUrl);
                if(name.length<3)return;const key=(url||name).toLowerCase();if(seen.has(key))return;
                price=clean(price)||(text.match(currency)||[])[0]||'';
                rating=clean(rating)||(text.match(ratingPattern)||[])[0]||'';
                buyers=clean(buyers)||(text.match(buyersPattern)||[])[0]||'';
                if(!price&&!rating&&!buyers&&!/product|товар|catalog|item|offer/i.test((url||'')+' '+source))return;
                seen.add(key);result.push({name,price,rating,buyers,url,imageUrl,text,source});
              };

              // Структурированные данные надёжнее CSS-классов и работают на многих магазинах.
              for(const script of document.querySelectorAll('script[type="application/ld+json"]')){
                try{
                  const root=JSON.parse(script.textContent||'null'), queue=Array.isArray(root)?[...root]:[root];
                  while(queue.length){const item=queue.shift();if(!item||typeof item!=='object')continue;
                    if(Array.isArray(item)){queue.push(...item);continue} if(item['@graph'])queue.push(item['@graph']);
                    const type=Array.isArray(item['@type'])?item['@type'].join(' '):String(item['@type']||'');
                    if(/Product/i.test(type)){
                      const offer=Array.isArray(item.offers)?item.offers[0]:(item.offers||{}), aggregate=item.aggregateRating||{};
                      const price=offer.price?String(offer.price)+' '+String(offer.priceCurrency||''):'';
                      const rating=aggregate.ratingValue?String(aggregate.ratingValue):'';
                      const buyers=aggregate.reviewCount||aggregate.ratingCount?String(aggregate.reviewCount||aggregate.ratingCount)+' отзывов':'';
                      const image=Array.isArray(item.image)?item.image[0]:(item.image?.url||item.image||'');
                      add(item.name||item.headline,item.description||item.name,item.url||offer.url||location.href,price,rating,buyers,'JSON-LD Product',image);
                    }
                    if(/ItemList/i.test(type)&&Array.isArray(item.itemListElement))queue.push(...item.itemListElement.map(x=>x.item||x));
                  }
                }catch{}
              }

              const selectors='[itemtype*="Product"],[itemscope][itemprop="itemListElement"],[data-product-id],[data-nm-id],[data-sku],[data-product],[data-testid*="product" i],article,li[class*="product" i],div[class*="product-card" i],div[class*="productcard" i],div[class*="catalog" i] [class*="card" i],[role="listitem"]';
              const nodes=[...new Set(document.querySelectorAll(selectors))].filter(visible);
              for(const e of nodes){
                const text=clean(e.innerText); if(text.length<12||text.length>1800) continue;
                const heading=e.querySelector('h1,h2,h3,h4,[itemprop="name"],[class*="name" i],[class*="title" i]');
                const link=e.matches('a')?e:e.querySelector('a[href]');
                const name=heading?.innerText||link?.getAttribute('aria-label')||link?.title||text.slice(0,180);
                const image=e.querySelector('img');
                const imageSource=bestImage(image,e);
                add(name,text,link?.href||e.getAttribute('itemid')||'','','','','product DOM',imageSource); if(result.length>=80) break;
              }

              // Универсальный резерв: ссылка с изображением и ценой в ближайшей карточке.
              if(result.length<8){
                for(const link of [...document.querySelectorAll('a[href]')].filter(visible)){
                  if(!link.querySelector('img')&&!link.closest('[class*="product" i],[class*="card" i],[class*="item" i]'))continue;
                  const host=link.closest('article,li,[role="listitem"],[class*="result" i],[class*="item" i],[class*="product" i],[class*="card" i]')||link.parentElement;
                  const text=clean(host?.innerText||link.innerText);if(text.length<8||text.length>1800||!currency.test(text))continue;
                  const image=link.querySelector('img')||host?.querySelector('img');
                  const name=link.innerText||link.getAttribute('aria-label')||image?.alt||link.title||text.slice(0,180);
                  const imageSource=bestImage(image,host);
                  add(name,text,link.href,'','','','image + price',imageSource);if(result.length>=80)break;
                }
              }
              return result.slice(0,80);
            })();
            """);
    }

    public async Task<byte[]?> CaptureShoppingProductImageAsync(string productUrl)
    {
        if (Core is null || !Uri.TryCreate(productUrl, UriKind.Absolute, out _)) return null;
        var script = """
            (() => {
              try {
                const target=new URL(__TARGET__);
                const normalize=value=>{try{const u=new URL(value,location.href);return u.origin+u.pathname.replace(/\/$/,'')}catch{return ''}};
                const wanted=normalize(target.href);
                const anchor=[...document.querySelectorAll('a[href]')].find(a=>normalize(a.href)===wanted);
                const host=anchor?.closest('article,li,[role="listitem"],[class*="product" i],[class*="card" i],[class*="item" i]')||anchor;
                const image=host?.querySelector('img')||anchor?.querySelector('img');
                if(!image||!image.complete||image.naturalWidth<2||image.naturalHeight<2)return '';
                const scale=Math.min(1,320/image.naturalWidth,200/image.naturalHeight);
                const canvas=document.createElement('canvas');
                canvas.width=Math.max(1,Math.round(image.naturalWidth*scale));
                canvas.height=Math.max(1,Math.round(image.naturalHeight*scale));
                canvas.getContext('2d',{alpha:false}).drawImage(image,0,0,canvas.width,canvas.height);
                return canvas.toDataURL('image/jpeg',.78);
              } catch { return ''; }
            })();
            """.Replace("__TARGET__", JsonSerializer.Serialize(productUrl), StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script);
        string? dataUrl;
        try { dataUrl = JsonSerializer.Deserialize<string>(json); }
        catch (JsonException) { return null; }
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return null;
        var separator = dataUrl.IndexOf(',');
        if (separator < 0 || dataUrl.Length - separator > 1_500_000) return null;
        try { return Convert.FromBase64String(dataUrl[(separator + 1)..]); }
        catch (FormatException) { return null; }
    }

    public async Task<string?> GetNextShoppingPageUrlAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return null;
        var json = await Core.ExecuteScriptAsync("""
            (()=>{const direct=document.querySelector('link[rel="next"],a[rel="next"],a[aria-label*="next" i],a[aria-label*="след" i]');if(direct?.href)return direct.href;
              const links=[...document.querySelectorAll('a[href],button')];
              const next=links.find(e=>{const t=((e.innerText||'')+' '+(e.getAttribute('aria-label')||'')+' '+(e.title||'')).trim().toLowerCase();return /^(next|следующ|далее|впер[её]д|›|»|下一页|次へ)/i.test(t)&&!e.disabled});
              if(next?.href)return next.href;
              const current=[...document.querySelectorAll('[aria-current="page"],.active,.current')].find(e=>/^\d+$/.test((e.textContent||'').trim()));
              if(current){const wanted=Number((current.textContent||'').trim())+1;const numbered=[...document.querySelectorAll('a[href]')].find(a=>Number((a.textContent||'').trim())===wanted);if(numbered)return numbered.href}
              const url=new URL(location.href);for(const key of ['page','p','pg'])if(url.searchParams.has(key)){const number=Number(url.searchParams.get(key));if(Number.isFinite(number)){url.searchParams.set(key,String(number+1));return url.href}}
              return null;})();
            """);
        string? value;
        try { value = JsonSerializer.Deserialize<string>(json); }
        catch { return null; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var next) ||
            !Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var current) ||
            !IsSameSite(next.Host, current.Host) ||
            next.Scheme is not ("http" or "https")) return null;
        return next.GetLeftPart(UriPartial.Path) + next.Query;
    }

    public async Task<bool> NavigateAndWaitAsync(string url, TimeSpan timeout)
    {
        if (Core is null || !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            !Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var current) ||
            !IsSameSite(target.Host, current.Host)) return false;
        var source = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs e) => source.TrySetResult(e);
        Core.NavigationCompleted += Handler;
        try { Core.Navigate(url); return (await source.Task.WaitAsync(timeout)).IsSuccess; }
        catch (TimeoutException) { return false; }
        finally { Core.NavigationCompleted -= Handler; }
    }

    public async Task<bool> NavigateInternalAndWaitAsync(string url, TimeSpan timeout)
    {
        if (Core is null || !UrlService.IsInternal(url)) return false;
        var source = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs e) => source.TrySetResult(e);
        Core.NavigationCompleted += Handler;
        try { Core.Navigate(url); return (await source.Task.WaitAsync(timeout)).IsSuccess; }
        catch (TimeoutException) { return false; }
        finally { Core.NavigationCompleted -= Handler; }
    }

    public async Task<bool> TryClickNextShoppingPageAsync()
    {
        if (Core is null) return false;
        var before = await GetShoppingCatalogFingerprintAsync();
        var json = await Core.ExecuteScriptAsync("""
            (()=>{const candidates=[...document.querySelectorAll('button,[role="button"]')];let next=candidates.find(e=>{const t=((e.innerText||'')+' '+(e.getAttribute('aria-label')||'')+' '+(e.title||'')).trim().toLowerCase();return /^(next|следующ|далее|впер[её]д|›|»|下一页|次へ)/i.test(t)&&!e.disabled&&e.getAttribute('aria-disabled')!=='true'});
              if(!next){const current=[...document.querySelectorAll('[aria-current="page"],.active,.current')].find(e=>/^\d+$/.test((e.textContent||'').trim()));if(current){const wanted=Number((current.textContent||'').trim())+1;next=candidates.find(e=>Number((e.textContent||'').trim())===wanted)}}
              if(!next)return false;next.click();return true})();
            """);
        if (!bool.TryParse(json, out var clicked) || !clicked) return false;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await Task.Delay(400);
            var after = await GetShoppingCatalogFingerprintAsync();
            if (!after.Equals(before, StringComparison.Ordinal)) return true;
        }
        return true;
    }

    public async Task<bool> ScrollShoppingResultsAsync()
    {
        if (Core is null) return false;
        var before = await GetShoppingCatalogFingerprintAsync();
        await Core.ExecuteScriptAsync("window.scrollTo({top:document.documentElement.scrollHeight,behavior:'smooth'});true");
        await Task.Delay(1400);
        var after = await GetShoppingCatalogFingerprintAsync();
        return !before.Equals(after, StringComparison.Ordinal);
    }
}
