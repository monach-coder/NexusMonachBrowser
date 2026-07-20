using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexusMonach.Services;
using NexusMonach.Views;

namespace NexusMonach.Models;

public sealed class UrlRequestedEventArgs(string value) : EventArgs
{
    public string Value { get; } = value;
    public bool OpenInNewTab { get; init; }
}

public sealed class SearchResultRequestedEventArgs(string query, string url) : EventArgs
{
    public string Query { get; } = query;
    public string Url { get; } = url;
}

public sealed record TabNetworkSnapshot(
    IReadOnlyList<string> ContactedHosts,
    IReadOnlyList<string> ThirdPartyHosts,
    IReadOnlyList<string> BlockedTrackerHosts,
    IReadOnlyList<int> ObservedPorts,
    int RequestCount);

public sealed class BrowserTab : INotifyPropertyChanged, IDisposable
{
    private readonly bool _isPrivate;
    private readonly bool _navigateOnInitialize;
    private readonly TaskCompletionSource<bool> _firstNavigation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializationTask;
    private string _title = "Новая вкладка";
    private string _address = string.Empty;
    private bool _isLoading;
    private int _blockedCount;
    private bool _disposed;
    private bool _isSuspended;
    private PhishingRiskLevel _phishingRisk;
    private string _securityWarning = string.Empty;
    private double _visualOpacity = 1;
    private readonly object _networkLock = new();
    private readonly HashSet<string> _contactedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thirdPartyHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedTrackerHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _observedPorts = [];
    private int _requestCount;
    private string _networkTopHost = string.Empty;
    private string _agentDomToken = string.Empty;
    private readonly object _developerLock = new();
    private readonly Queue<string> _developerEvents = new();
    private CoreWebView2DevToolsProtocolEventReceiver? _consoleReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private SecureRestartTabState? _pendingRestartState;
    private bool _restartStateRestoreRunning;

    public BrowserTab(string initialUrl, bool isPrivate, bool navigateOnInitialize = true)
    {
        InitialUrl = initialUrl;
        _isPrivate = isPrivate;
        _navigateOnInitialize = navigateOnInitialize;
        View = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 16, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    public string InitialUrl { get; }
    public WebView2 View { get; }
    public CoreWebView2? Core => View.CoreWebView2;
    public bool IsInitialized => Core is not null;
    public bool IsPrivate => _isPrivate;
    public DateTime LastActivatedUtc { get; private set; } = DateTime.UtcNow;
    public bool IsSuspended => _isSuspended;
    public double VisualOpacity
    {
        get => _visualOpacity;
        private set { _visualOpacity = value; OnPropertyChanged(); }
    }

    public string Title
    {
        get => _title;
        private set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortTitle)); }
    }

    public string ShortTitle => Title.Length <= 26 ? Title : Title[..25] + "…";

    public string Address
    {
        get => _address;
        private set { _address = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public int BlockedCount
    {
        get => _blockedCount;
        private set { _blockedCount = value; OnPropertyChanged(); }
    }

    public bool CanGoBack => Core?.CanGoBack == true;
    public bool CanGoForward => Core?.CanGoForward == true;
    public string CurrentUrl => Core?.Source ??
        (!string.IsNullOrWhiteSpace(Address) ? Address : InitialUrl);
    public string CurrentHost => Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    public bool IsSecureConnection => CurrentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public PhishingRiskLevel PhishingRisk
    {
        get => _phishingRisk;
        private set { _phishingRisk = value; OnPropertyChanged(); }
    }
    public string SecurityWarning
    {
        get => _securityWarning;
        private set { _securityWarning = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? NavigationSucceeded;
    public event EventHandler<UrlRequestedEventArgs>? OpenUrlRequested;
    public event EventHandler<UrlRequestedEventArgs>? NexusSearchRequested;
    public event EventHandler<SearchResultRequestedEventArgs>? SearchResultRequested;
    public event EventHandler? SettingsRequested;
    public event Action<string>? StatusMessageRequested;
    public Func<string, Task<CoreWebView2?>>? CreatePopupAsync { get; set; }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    public async Task WaitForFirstPageAsync(TimeSpan timeout)
    {
        await InitializeAsync();
        await Task.WhenAny(_firstNavigation.Task, Task.Delay(timeout));
    }

    public void Navigate(string url)
    {
        if (Core is null)
            return;
        Core.Settings.IsWebMessageEnabled = UrlService.IsInternal(url);
        Core.Navigate(url);
    }

    public void GoBack()
    {
        if (Core?.CanGoBack == true) Core.GoBack();
    }

    public void GoForward()
    {
        if (Core?.CanGoForward == true) Core.GoForward();
    }

    public void ReloadOrStop()
    {
        if (Core is null) return;
        if (IsLoading) Core.Stop(); else Core.Reload();
    }

    public void Reload() => Core?.Reload();

    public async Task ApplySettingsAsync()
    {
        if (Core is null) return;
        var settings = SettingsService.Current;
        Core.Settings.AreDevToolsEnabled = true;
        Core.Settings.IsPasswordAutosaveEnabled = settings.EnablePasswordAutosave;
        Core.Settings.IsGeneralAutofillEnabled = settings.EnableGeneralAutofill;
        BrowserEnvironment.ApplyPrivacyLevel(Core.Profile, settings.PrivacyLevel);
        await Task.CompletedTask;
    }

    public void MarkActive()
    {
        LastActivatedUtc = DateTime.UtcNow;
        VisualOpacity = 1;
        if (_isSuspended && Core is not null)
        {
            Core.Resume();
            _isSuspended = false;
        }
    }

    public void UpdateVisualDecay(bool isActive, DateTime nowUtc)
    {
        if (isActive) { VisualOpacity = 1; return; }
        var idleMinutes = Math.Max(0, (nowUtc - LastActivatedUtc).TotalMinutes);
        if (idleMinutes < 5) { VisualOpacity = 1; return; }

        // После пяти минут вкладка мягко затухает до 42% за следующие 115 минут.
        VisualOpacity = Math.Clamp(1 - ((idleMinutes - 5) / 115 * 0.58), 0.42, 1);
    }

    public async Task TrySuspendAsync()
    {
        if (Core is null || _isSuspended || IsLoading || Core.IsDocumentPlayingAudio)
            return;
        try { _isSuspended = await Core.TrySuspendAsync(); }
        catch { _isSuspended = false; }
    }

    public async Task<bool> ClearCurrentSiteDataAsync()
    {
        if (Core is null || !Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || UrlService.IsInternal(CurrentUrl))
            return false;

        try
        {
            var origin = uri.GetLeftPart(UriPartial.Authority);
            var cookies = await Core.CookieManager.GetCookiesAsync(origin);
            foreach (var cookie in cookies)
                Core.CookieManager.DeleteCookie(cookie);

            await Core.ExecuteScriptAsync("""
                (async () => {
                  try { localStorage.clear(); } catch (_) {}
                  try { sessionStorage.clear(); } catch (_) {}
                  try {
                    const keys = await caches.keys();
                    await Promise.all(keys.map(key => caches.delete(key)));
                  } catch (_) {}
                  try {
                    if (indexedDB.databases) {
                      const databases = await indexedDB.databases();
                      for (const database of databases) {
                        if (database.name) indexedDB.deleteDatabase(database.name);
                      }
                    }
                  } catch (_) {}
                  return true;
                })();
                """);
            Core.Reload();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetPendingRestartState(SecureRestartTabState state) => _pendingRestartState = state;

    public async Task<SecureRestartTabState> CaptureSecureRestartStateAsync()
    {
        var fallback = SecureRestartSessionService.UrlOnly(CurrentUrl);
        if (_isPrivate || Core is null || UrlService.IsInternal(CurrentUrl)) return fallback;
        try
        {
            var json = await Core.ExecuteScriptAsync("""
                (() => {
                  const result={url:location.href,scrollX:scrollX||0,scrollY:scrollY||0,fields:[]};
                  const pageKey=(location.hostname+location.pathname).toLowerCase();
                  if(/(?:^|[.\/_-])(login|signin|sign-in|oauth|authorize|auth|checkout|payment|billing|bank)(?:[.\/_-]|$)/.test(pageKey))return result;
                  const sensitiveKey=/pass|pwd|secret|token|auth|otp|one.?time|verification|2fa|mfa|card|credit|debit|cvv|cvc|iban|account|login|username|e.?mail/i;
                  const sensitiveAutocomplete=/password|username|email|one-time-code|cc-|transaction|webauthn/i;
                  const pathFor=element=>{
                    if(element.id)return '#'+CSS.escape(element.id);
                    const parts=[];let node=element;
                    while(node&&node.nodeType===1&&node!==document.documentElement&&parts.length<7){
                      let part=node.tagName.toLowerCase();
                      const siblings=node.parentElement?[...node.parentElement.children].filter(x=>x.tagName===node.tagName):[];
                      if(siblings.length>1)part+=':nth-of-type('+(siblings.indexOf(node)+1)+')';
                      parts.unshift(part);node=node.parentElement;
                    }
                    return parts.join('>');
                  };
                  const nodes=[...document.querySelectorAll('input,textarea,select,[contenteditable="true"]')];
                  let total=0;
                  for(const element of nodes){
                    if(result.fields.length>=80||total>=65536)break;
                    const type=(element.type||'').toLowerCase();
                    const autocomplete=(element.autocomplete||'').toLowerCase();
                    const key=[element.name,element.id,element.placeholder,element.getAttribute('aria-label')].filter(Boolean).join(' ');
                    if(['password','hidden','file','email','tel'].includes(type)||sensitiveAutocomplete.test(autocomplete)||sensitiveKey.test(key))continue;
                    const selector=pathFor(element);if(!selector||selector.length>500)continue;
                    let kind='text',value='',checked=null;
                    if(type==='checkbox'||type==='radio'){kind='checkbox';checked=Boolean(element.checked)}
                    else if(element.tagName==='SELECT'){kind='select';value=String(element.value||'')}
                    else if(element.isContentEditable){kind='editable';value=String(element.innerText||'')}
                    else value=String(element.value||'');
                    if(value.length>4000)value=value.slice(0,4000);total+=value.length;
                    if(!value&&checked===null)continue;
                    result.fields.push({selector,kind,value,checked});
                  }
                  return result;
                })();
                """);
            return JsonSerializer.Deserialize<SecureRestartTabState>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async Task TryRestoreSecureRestartStateAsync()
    {
        if (_restartStateRestoreRunning || _pendingRestartState is null || Core is null || _isPrivate) return;
        _restartStateRestoreRunning = true;
        try
        {
            await Task.Delay(350);
            var state = _pendingRestartState;
            var script = $$"""
                (()=>{
                  const state={{JsonSerializer.Serialize(state)}};
                  let expected;try{expected=new URL(state.Url)}catch{return -1}
                  if(location.origin!==expected.origin||location.pathname!==expected.pathname)return -1;
                  const pageKey=(location.hostname+location.pathname).toLowerCase();
                  if(/(?:^|[.\/_-])(login|signin|sign-in|oauth|authorize|auth|checkout|payment|billing|bank)(?:[.\/_-]|$)/.test(pageKey))return 0;
                  const sensitiveKey=/pass|pwd|secret|token|auth|otp|one.?time|verification|2fa|mfa|card|credit|debit|cvv|cvc|iban|account|login|username|e.?mail/i;
                  const sensitiveAutocomplete=/password|username|email|one-time-code|cc-|transaction|webauthn/i;
                  let restored=0;
                  for(const field of state.Fields||[]){
                    let element;try{element=document.querySelector(field.Selector)}catch{continue}if(!element)continue;
                    const type=(element.type||'').toLowerCase(),autocomplete=(element.autocomplete||'').toLowerCase();
                    const key=[element.name,element.id,element.placeholder,element.getAttribute('aria-label')].filter(Boolean).join(' ');
                    if(['password','hidden','file','email','tel'].includes(type)||sensitiveAutocomplete.test(autocomplete)||sensitiveKey.test(key))continue;
                    if(field.Kind==='checkbox'&&(type==='checkbox'||type==='radio')){
                      const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'checked')?.set;
                      if(setter)setter.call(element,Boolean(field.Checked));else element.checked=Boolean(field.Checked);
                    }else if(field.Kind==='select'&&element.tagName==='SELECT')element.value=field.Value||'';
                    else if(field.Kind==='editable'&&element.isContentEditable)element.textContent=field.Value||'';
                    else if(['INPUT','TEXTAREA'].includes(element.tagName)){
                      const prototype=element.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;
                      const setter=Object.getOwnPropertyDescriptor(prototype,'value')?.set;
                      if(setter)setter.call(element,field.Value||'');else element.value=field.Value||'';
                    }else continue;
                    element.dispatchEvent(new Event('input',{bubbles:true}));
                    element.dispatchEvent(new Event('change',{bubbles:true}));restored++;
                  }
                  scrollTo({left:state.ScrollX||0,top:state.ScrollY||0,behavior:'instant'});return restored;
                })();
                """;
            var result = await Core.ExecuteScriptAsync(script);
            if (int.TryParse(result, out var restored) && restored >= 0)
            {
                await Task.Delay(700);
                if (Core is not null) await Core.ExecuteScriptAsync(script);
                _pendingRestartState = null;
            }
        }
        catch { /* Неподдерживаемая страница открывается без восстановления полей. */ }
        finally { _restartStateRestoreRunning = false; }
    }

    public TabNetworkSnapshot GetNetworkSnapshot()
    {
        lock (_networkLock)
        {
            return new TabNetworkSnapshot(
                _contactedHosts.OrderBy(x => x).ToArray(),
                _thirdPartyHosts.OrderBy(x => x).ToArray(),
                _blockedTrackerHosts.OrderBy(x => x).ToArray(),
                _observedPorts.OrderBy(x => x).ToArray(),
                _requestCount);
        }
    }

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

    public async Task<IReadOnlyList<TranslationSegment>> CaptureTranslationSegmentsAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return [];
        var json = await Core.ExecuteScriptAsync("""
            (() => {
              const previous=window.__nexusPageTranslation;
              if(previous?.originals) for(const entry of previous.originals.values())
                if(entry.node?.isConnected) entry.node.nodeValue=entry.original;
              const state={nodes:new Map(),originals:new Map()};window.__nexusPageTranslation=state;
              const root=document.body; if(!root) return [];
              const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);
              const nodes=[]; let node,total=0;
              while((node=walker.nextNode()) && nodes.length<100 && total<5500){
                const parent=node.parentElement, raw=node.nodeValue||'', text=raw.trim();
                if(!parent||text.length<2||parent.closest('script,style,noscript,textarea,code,pre,svg,canvas,[contenteditable="true"],[data-nexus-translation-ui]')) continue;
                const style=getComputedStyle(parent); if(style.display==='none'||style.visibility==='hidden'||style.opacity==='0'||parent.getClientRects().length===0) continue;
                nodes.push({node,raw,text}); total+=text.length;
              }
              return nodes.map((item,index)=>{
                const id='n'+(index+1);state.nodes.set(id,item.node);state.originals.set(id,{node:item.node,original:item.raw,text:item.text});
                const language=(item.node.parentElement?.closest('[lang]')?.getAttribute('lang')||document.documentElement.lang||'').trim();
                return {Id:id,Text:item.text,Language:language};
              });
            })();
            """);
        try { return JsonSerializer.Deserialize<List<TranslationSegment>>(json) ?? []; }
        catch { return []; }
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
              const status=document.createElement('span'); status.id='nexus-translation-status'; status.textContent='Перевод: 0 / '+{{total}};
              const restore=document.createElement('button'); restore.textContent='Вернуть оригинал';
              restore.style.cssText='border:1px solid #66ffffff;border-radius:8px;background:#26ffffff;color:#fff;padding:6px 9px;cursor:pointer;';
              restore.onclick=()=>{const state=window.__nexusPageTranslation;if(state?.originals)for(const entry of state.originals.values())if(entry.node?.isConnected)entry.node.nodeValue=entry.original;window.__nexusPageTranslation=null;box.remove();};
              const close=document.createElement('button'); close.textContent='×'; close.title='Скрыть панель, оставив перевод';
              close.style.cssText='border:0;background:transparent;color:#fff;font-size:18px;cursor:pointer;padding:2px 5px;'; close.onclick=()=>box.remove();
              box.append(status,restore,close); document.documentElement.append(box);
            })();
            """);
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

    public async Task<AudioCaptureResult> CaptureActiveVideoAudioAsync(int seconds,
        CancellationToken cancellationToken = default)
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl))
            return new AudioCaptureResult { Error = "Открой страницу с видео." };
        var script = """
            (async () => {
              try {
                const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
                  .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
                const video=videos.find(v=>!v.paused&&!v.ended)||videos[0];
                if(!video) return {Success:false,Error:'Активное HTML5-видео не найдено.',WavBase64:''};
                const capture=video.captureStream||video.mozCaptureStream;
                if(!capture) return {Success:false,Error:'Этот проигрыватель не разрешает захват звукового потока.',WavBase64:''};
                const stream=capture.call(video),tracks=stream.getAudioTracks();
                if(!tracks.length) return {Success:false,Error:'В видеопотоке нет доступной аудиодорожки или она защищена DRM.',WavBase64:''};
                const context=new AudioContext(),source=context.createMediaStreamSource(stream);
                const processor=context.createScriptProcessor(4096,1,1),silent=context.createGain();silent.gain.value=0;
                const chunks=[];processor.onaudioprocess=e=>chunks.push(new Float32Array(e.inputBuffer.getChannelData(0)));
                source.connect(processor);processor.connect(silent);silent.connect(context.destination);await context.resume();
                await new Promise(resolve=>setTimeout(resolve,__MILLISECONDS__));
                processor.disconnect();source.disconnect();silent.disconnect();tracks.forEach(t=>t.stop());await context.close();
                const length=chunks.reduce((n,x)=>n+x.length,0),input=new Float32Array(length);let offset=0;
                for(const chunk of chunks){input.set(chunk,offset);offset+=chunk.length}
                if(!input.length) return {Success:false,Error:'Браузер не получил аудиосэмплы.',WavBase64:''};
                let energy=0;for(let i=0;i<input.length;i+=32)energy+=input[i]*input[i];
                if(Math.sqrt(energy/Math.max(1,input.length/32))<0.0001)return {Success:false,Error:'Получен пустой звук. Возможна защита DRM или ограничение cross-origin.',WavBase64:''};
                const outRate=16000,ratio=context.sampleRate/outRate,outLength=Math.floor(input.length/ratio),pcm=new Int16Array(outLength);
                for(let i=0;i<outLength;i++){const start=Math.floor(i*ratio),end=Math.min(input.length,Math.floor((i+1)*ratio));let sum=0;for(let j=start;j<end;j++)sum+=input[j];const value=Math.max(-1,Math.min(1,sum/Math.max(1,end-start)));pcm[i]=value<0?value*32768:value*32767}
                const bytes=new Uint8Array(44+pcm.length*2),view=new DataView(bytes.buffer),write=(p,s)=>{for(let i=0;i<s.length;i++)view.setUint8(p+i,s.charCodeAt(i))};
                write(0,'RIFF');view.setUint32(4,36+pcm.length*2,true);write(8,'WAVE');write(12,'fmt ');view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);view.setUint32(24,outRate,true);view.setUint32(28,outRate*2,true);view.setUint16(32,2,true);view.setUint16(34,16,true);write(36,'data');view.setUint32(40,pcm.length*2,true);for(let i=0;i<pcm.length;i++)view.setInt16(44+i*2,pcm[i],true);
                let binary='';for(let i=0;i<bytes.length;i+=32768)binary+=String.fromCharCode(...bytes.subarray(i,i+32768));
                return {Success:true,Error:'',WavBase64:btoa(binary)};
              } catch(error) { return {Success:false,Error:error?.message||String(error),WavBase64:''}; }
            })();
            """.Replace("__MILLISECONDS__", Math.Clamp(seconds, 3, 15).ToString() + "000", StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
        try { return JsonSerializer.Deserialize<AudioCaptureResult>(json) ?? new AudioCaptureResult { Error = "Пустой результат захвата." }; }
        catch { return new AudioCaptureResult { Error = "Не удалось прочитать аудиопоток страницы." }; }
    }

    public async Task BeginLiveAudioTranslationAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{window.__nexusStopAudioTranslation=false;
              const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=videos.find(v=>!v.paused&&!v.ended)||videos[0];window.__nexusSourceCaptionModes=[];
              if(video)for(const track of video.textTracks)if(track.label!=='Nexus Live RU'&&track.label!=='Nexus RU'){window.__nexusSourceCaptionModes.push({track,mode:track.mode});track.mode='disabled'}
              document.getElementById('nexus-hide-native-captions')?.remove();
              const nativeStyle=document.createElement('style');nativeStyle.id='nexus-hide-native-captions';nativeStyle.dataset.nexusTranslationUi='true';
              nativeStyle.textContent='video::cue{color:transparent!important;background:transparent!important;text-shadow:none!important}.ytp-caption-window-container,.ytp-caption-segment,.vp-captions,[data-purpose="captions-cue-text"],[class*="subtitle" i]:not(#nexus-live-subtitle-overlay),[class*="captions" i]:not(#nexus-live-subtitle-overlay){visibility:hidden!important}';document.documentElement.append(nativeStyle);
              let overlay=document.getElementById('nexus-live-subtitle-overlay');
              if(!overlay){overlay=document.createElement('div');overlay.id='nexus-live-subtitle-overlay';overlay.dataset.nexusTranslationUi='true';overlay.style.cssText='position:fixed;z-index:2147483647;padding:8px 12px;border-radius:8px;background:#d0000000;color:#fff;text-align:center;font:600 clamp(15px,1.8vw,23px)/1.35 Segoe UI,sans-serif;text-shadow:0 1px 3px #000;pointer-events:none;box-sizing:border-box';document.documentElement.append(overlay)}
              let stop=document.getElementById('nexus-live-translation-stop');
              if(!stop){stop=document.createElement('button');stop.id='nexus-live-translation-stop';stop.dataset.nexusTranslationUi='true';stop.textContent='Стоп';stop.style.cssText='position:fixed;z-index:2147483647;border:1px solid #66ffffff;border-radius:7px;background:#d0000000;color:#fff;padding:4px 8px;cursor:pointer;font:600 12px Segoe UI,sans-serif';stop.onclick=()=>{window.__nexusStopAudioTranslation=true;overlay.textContent='Остановка…'};document.documentElement.append(stop)}
              const place=()=>{const v=[...document.querySelectorAll('video')].filter(x=>x.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight))[0];if(!v)return;const r=v.getBoundingClientRect();overlay.style.left=Math.max(8,r.left+r.width*.08)+'px';overlay.style.width=Math.max(180,r.width*.84)+'px';overlay.style.bottom=Math.max(8,innerHeight-r.bottom+r.height*.055)+'px';stop.style.right=Math.max(8,innerWidth-r.right+8)+'px';stop.style.top=Math.max(8,r.top+8)+'px'};
              if(window.__nexusPlaceLiveTranslation){removeEventListener('resize',window.__nexusPlaceLiveTranslation);removeEventListener('scroll',window.__nexusPlaceLiveTranslation)}
              window.__nexusPlaceLiveTranslation=place;place();addEventListener('resize',place,{passive:true});addEventListener('scroll',place,{passive:true});overlay.textContent='Подготовка перевода звука…';})();
            """);
    }

    public async Task UpdateLiveAudioTranslationStatusAsync(string text)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("(()=>{const e=document.getElementById('nexus-live-subtitle-overlay');if(e)e.textContent=" +
                                      JsonSerializer.Serialize(text) + "})()");
    }

    public async Task<bool> ShouldStopLiveAudioTranslationAsync()
    {
        if (Core is null) return true;
        var json = await Core.ExecuteScriptAsync("Boolean(window.__nexusStopAudioTranslation)");
        return bool.TryParse(json, out var stopped) && stopped;
    }

    public async Task ShowLiveVideoSubtitleAsync(string translatedText)
    {
        if (Core is null || string.IsNullOrWhiteSpace(translatedText)) return;
        await Core.ExecuteScriptAsync("""
            ((text)=>{const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));const video=videos.find(v=>!v.paused&&!v.ended)||videos[0];if(!video)return;
              for(const track of video.textTracks)if(track.label==='Nexus Live RU')track.mode='disabled';
              let overlay=document.getElementById('nexus-live-subtitle-overlay');
              if(!overlay)return;window.__nexusPlaceLiveTranslation?.();
              overlay.textContent='RU · '+text;clearTimeout(window.__nexusSubtitleTimer);window.__nexusSubtitleTimer=setTimeout(()=>overlay.textContent='',11000);
            })(__TEXT__);
            """.Replace("__TEXT__", JsonSerializer.Serialize(translatedText), StringComparison.Ordinal));
    }

    public async Task EndLiveAudioTranslationAsync(string status)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            ((status)=>{for(const entry of window.__nexusSourceCaptionModes||[])try{entry.track.mode=entry.mode}catch{}
              window.__nexusSourceCaptionModes=[];window.__nexusStopAudioTranslation=true;
              document.getElementById('nexus-hide-native-captions')?.remove();
              const overlay=document.getElementById('nexus-live-subtitle-overlay');if(overlay){overlay.textContent=status;setTimeout(()=>overlay.remove(),1800)}
              document.getElementById('nexus-live-translation-stop')?.remove();
              if(window.__nexusPlaceLiveTranslation){removeEventListener('resize',window.__nexusPlaceLiveTranslation);removeEventListener('scroll',window.__nexusPlaceLiveTranslation)}
              window.__nexusPlaceLiveTranslation=null})(__STATUS__)
            """.Replace("__STATUS__", JsonSerializer.Serialize(status), StringComparison.Ordinal));
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

    public async Task<string> GetDeveloperContextAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return string.Empty;
        var dom = await Core.ExecuteScriptAsync("""
            (() => {
              const root=document.documentElement.cloneNode(true);
              root.querySelectorAll('script,style,noscript,iframe,canvas,svg').forEach(e=>e.remove());
              root.querySelectorAll('input,textarea,select').forEach(e=>{e.removeAttribute('value');e.textContent='';});
              root.querySelectorAll('*').forEach(e=>[...e.attributes].forEach(a=>{
                if(/^on/i.test(a.name)||/token|secret|password|nonce|auth|cookie|session/i.test(a.name)) e.removeAttribute(a.name);
              }));
              return root.outerHTML.slice(0,18000);
            })();
            """);
        string domText;
        try { domText = JsonSerializer.Deserialize<string>(dom) ?? string.Empty; }
        catch { domText = string.Empty; }
        string events;
        lock (_developerLock) events = string.Join("\n", _developerEvents.TakeLast(80));
        var network = GetNetworkSnapshot();
        return $"URL: {StripSensitiveUrl(CurrentUrl)}\nЗаголовок: {Title}\n" +
               $"Консоль и исключения:\n{(string.IsNullOrWhiteSpace(events) ? "—" : events)}\n\n" +
               $"Сеть: {network.RequestCount} запросов; порты {string.Join(", ", network.ObservedPorts)}; " +
               $"сторонние узлы {string.Join(", ", network.ThirdPartyHosts.Take(40))}; " +
               $"заблокированы {string.Join(", ", network.BlockedTrackerHosts.Take(30))}.\n\n" +
               $"Безопасный DOM:\n{domText}";
    }

    public async Task<int> HighlightDeveloperSelectorsAsync(IReadOnlyList<DeveloperHighlight> highlights)
    {
        if (Core is null || highlights.Count == 0) return 0;
        var safe = highlights.Select(x => new { selector = x.Selector, reason = x.Reason }).ToArray();
        var json = await Core.ExecuteScriptAsync($$"""
            (() => {
              document.querySelectorAll('[data-nexus-devtools-highlight]').forEach(e=>{
                e.style.removeProperty('outline'); e.removeAttribute('data-nexus-devtools-highlight');
              });
              const items={{JsonSerializer.Serialize(safe)}}; let count=0;
              for(const item of items){
                let found=[]; try { found=[...document.querySelectorAll(item.selector)].slice(0,8); } catch { continue; }
                for(const e of found){ e.style.outline='3px solid #dab96a'; e.dataset.nexusDevtoolsHighlight=item.reason||'Проверить';
                  e.title=(e.title?e.title+'\n':'')+'Nexus AI: '+(item.reason||'Проверить'); count++; }
              }
              document.querySelector('[data-nexus-devtools-highlight]')?.scrollIntoView({behavior:'smooth',block:'center'});
              return count;
            })();
            """);
        return int.TryParse(json, out var count) ? count : 0;
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
        catch (JsonException) { }
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
            catch (Exception) { }
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
                const imageSource=image?.currentSrc||image?.src||image?.getAttribute('data-src')||image?.getAttribute('data-original')||image?.getAttribute('data-lazy-src')||'';
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
                  const imageSource=image?.currentSrc||image?.src||image?.getAttribute('data-src')||image?.getAttribute('data-original')||image?.getAttribute('data-lazy-src')||'';
                  add(name,text,link.href,'','','','image + price',imageSource);if(result.length>=80)break;
                }
              }
              return result.slice(0,80);
            })();
            """);
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

    private void ResetNetworkSnapshot(string topLevelUrl)
    {
        lock (_networkLock)
        {
            _contactedHosts.Clear();
            _thirdPartyHosts.Clear();
            _blockedTrackerHosts.Clear();
            _observedPorts.Clear();
            _requestCount = 0;
            _networkTopHost = Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var top) ? top.Host : string.Empty;
        }
    }

    private void RecordNetworkRequest(string requestUrl, bool blocked)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) ||
            request.Scheme is not ("http" or "https"))
            return;

        lock (_networkLock)
        {
            _requestCount++;
            _contactedHosts.Add(request.Host);
            var port = request.IsDefaultPort
                ? request.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : request.Port;
            if (port is > 0 and <= 65535)
                _observedPorts.Add(port);

            if (!string.IsNullOrWhiteSpace(_networkTopHost) && !IsSameSite(request.Host, _networkTopHost))
                _thirdPartyHosts.Add(request.Host);
            if (blocked)
                _blockedTrackerHosts.Add(request.Host);
        }
    }

    private static bool IsSameSite(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
        right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        View.Dispose();
    }

    private async Task InitializeCoreAsync()
    {
        if (_disposed) return;
        var controllerOptions = BrowserEnvironment.CreateControllerOptions(_isPrivate);
        await View.EnsureCoreWebView2Async(BrowserEnvironment.Current, controllerOptions);

        var core = Core!;
        BrowserEnvironment.RegisterProfile(core.Profile);
        BrowserEnvironment.ApplyPrivacyLevel(core.Profile, SettingsService.Current.PrivacyLevel);
        if (!_isPrivate)
            await ExtensionService.EnsureInstalledAsync(core.Profile);

        if (Directory.Exists(AppPaths.WebAssets))
        {
            core.SetVirtualHostNameToFolderMapping(
                "nexus.local",
                AppPaths.WebAssets,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        Address = _navigateOnInitialize ? UrlService.Resolve(InitialUrl) : "about:blank";
        ConfigureSettings(core.Settings, UrlService.IsInternal(Address));
        AttachEvents(core);
        await AttachDeveloperDiagnosticsAsync(core);
        TrackingProtectionService.Attach(core, () => core.Source, () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                BlockedCount++;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }, RecordNetworkRequest);

        if (_navigateOnInitialize)
            core.Navigate(Address);
    }

    private async Task AttachDeveloperDiagnosticsAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            _consoleReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
            _exceptionReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
            _consoleReceiver.DevToolsProtocolEventReceived += (_, e) => CaptureDeveloperEvent("console", e.ParameterObjectAsJson);
            _exceptionReceiver.DevToolsProtocolEventReceived += (_, e) => CaptureDeveloperEvent("exception", e.ParameterObjectAsJson);
        }
        catch { /* DevTools AI покажет DOM и сеть, даже если CDP-журнал недоступен. */ }
    }

    private void CaptureDeveloperEvent(string kind, string json)
    {
        var safe = RedactDeveloperText(json);
        if (safe.Length > 1800) safe = safe[..1800] + "…";
        lock (_developerLock)
        {
            _developerEvents.Enqueue($"[{DateTime.Now:HH:mm:ss}] {kind}: {safe}");
            while (_developerEvents.Count > 200) _developerEvents.Dequeue();
        }
    }

    private static string RedactDeveloperText(string value)
    {
        var redacted = System.Text.RegularExpressions.Regex.Replace(value,
            """(?i)(authorization|cookie|password|passwd|token|secret|session)["']?\s*[:=]\s*["']?[^\s,;"']+""",
            "$1:[REDACTED]");
        return System.Text.RegularExpressions.Regex.Replace(redacted,
            @"eyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}(?:\.[A-Za-z0-9_-]{8,})?", "[JWT REDACTED]");
    }

    private static string StripSensitiveUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private void ConfigureSettings(CoreWebView2Settings settings, bool allowLocalBridge)
    {
        var app = SettingsService.Current;
        settings.IsScriptEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = allowLocalBridge;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsPinchZoomEnabled = true;
        settings.IsBuiltInErrorPageEnabled = true;
        settings.IsReputationCheckingRequired = true;
        settings.IsPasswordAutosaveEnabled = app.EnablePasswordAutosave;
        settings.IsGeneralAutofillEnabled = app.EnableGeneralAutofill;
        // User-Agent намеренно не меняется: стандартный Chromium-отпечаток менее уникален.
    }

    private void AttachEvents(CoreWebView2 core)
    {
        SelectedTextTranslationService.Attach(this, core, message => StatusMessageRequested?.Invoke(message));
        core.NavigationStarting += (_, e) =>
        {
            ResetNetworkSnapshot(e.Uri);
            core.Settings.IsWebMessageEnabled = UrlService.IsInternal(e.Uri);
            var phishing = PhishingProtectionService.Analyze(e.Uri);
            PhishingRisk = phishing.Level;
            SecurityWarning = phishing.Description;
            if (phishing.Level == PhishingRiskLevel.High)
            {
                e.Cancel = true;
                var owner = Window.GetWindow(View);
                // Query strings on sign-in pages can contain opaque session identifiers.
                // They are not useful for the decision and must never be copied into UI/logs.
                var warning = $"Возможная подмена адреса\n\n{phishing.Description}\n\nАдрес: {StripSensitiveUrl(e.Uri)}\n\nВсё равно открыть сайт?";
                var decision = owner is null
                    ? GlassDialogWindow.Show(warning, "Monach Anti-Phishing", MessageBoxButton.YesNo, MessageBoxImage.Stop)
                    : GlassDialogWindow.Show(owner, warning, "Monach Anti-Phishing", MessageBoxButton.YesNo, MessageBoxImage.Stop);
                if (decision == MessageBoxResult.Yes)
                {
                    PhishingProtectionService.TrustForSession(phishing.Host);
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(e.Uri)));
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var cleaned = UrlService.CleanTrackingParameters(e.Uri);
            if (!cleaned.Equals(e.Uri, StringComparison.Ordinal))
            {
                e.Cancel = true;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(cleaned)));
                return;
            }

            IsLoading = true;
            Address = e.Uri;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.SourceChanged += (_, _) =>
        {
            Address = core.Source;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Новая вкладка" : core.DocumentTitle;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.HistoryChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);

        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            Address = core.Source;
            _firstNavigation.TrySetResult(e.IsSuccess);
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (e.IsSuccess)
            {
                NavigationSucceeded?.Invoke(this, EventArgs.Empty);
                _ = TryRestoreSecureRestartStateAsync();
            }
        };

        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                if (CreatePopupAsync is not null)
                    e.NewWindow = await CreatePopupAsync(e.Uri);
            }
            finally
            {
                deferral.Complete();
            }
        };

        core.PermissionRequested += (_, e) => HandlePermission(e);
        core.WebMessageReceived += (_, e) => HandleWebMessage(e);
        core.DownloadStarting += (_, e) => HandleDownload(e);
        core.ProcessFailed += (_, e) =>
        {
            CrashReportService.RecordNonFatal("webview2", "process-" + e.ProcessFailedKind);
            Title = "Вкладка аварийно завершена";
            IsLoading = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void HandlePermission(CoreWebView2PermissionRequestedEventArgs e)
    {
        if (SettingsService.Current.BlockNotifications && e.PermissionKind == CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            return;
        }

        var needsOurPrompt = e.PermissionKind is
            CoreWebView2PermissionKind.Camera or
            CoreWebView2PermissionKind.Microphone or
            CoreWebView2PermissionKind.Geolocation or
            CoreWebView2PermissionKind.ClipboardRead or
            CoreWebView2PermissionKind.FileReadWrite or
            CoreWebView2PermissionKind.OtherSensors or
            CoreWebView2PermissionKind.LocalFonts or
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages or
            CoreWebView2PermissionKind.WindowManagement or
            CoreWebView2PermissionKind.MultipleAutomaticDownloads or
            CoreWebView2PermissionKind.Notifications;
        if (!needsOurPrompt)
            return;

        var origin = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Host : e.Uri;
        var permission = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Camera => "доступ к камере",
            CoreWebView2PermissionKind.Microphone => "доступ к микрофону",
            CoreWebView2PermissionKind.Geolocation => "местоположение",
            CoreWebView2PermissionKind.ClipboardRead => "чтение буфера обмена",
            CoreWebView2PermissionKind.Notifications => "уведомления",
            CoreWebView2PermissionKind.FileReadWrite => "чтение и изменение выбранных файлов",
            CoreWebView2PermissionKind.LocalFonts => "список локальных шрифтов",
            CoreWebView2PermissionKind.OtherSensors => "датчики устройства",
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => "несколько автоматических загрузок",
            _ => e.PermissionKind.ToString()
        };

        var owner = Window.GetWindow(View);
        var result = owner is null
            ? GlassDialogWindow.Show($"Сайт {origin} запрашивает {permission}. Разрешить?", "Разрешение сайта",
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            : GlassDialogWindow.Show(owner, $"Сайт {origin} запрашивает {permission}. Разрешить?", "Разрешение сайта",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
        e.State = result == MessageBoxResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            !source.Host.Equals("nexus.local", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "navigate")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                    OpenUrlRequested?.Invoke(this, new UrlRequestedEventArgs(value) { OpenInNewTab = false });
            }
            else if (type == "search")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                    NexusSearchRequested?.Invoke(this, new UrlRequestedEventArgs(value));
            }
            else if (type == "result-open")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                var query = root.TryGetProperty("query", out var q) ? q.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(query))
                    SearchResultRequested?.Invoke(this, new SearchResultRequestedEventArgs(query, value));
            }
            else if (type == "settings")
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (JsonException)
        {
            // Сообщения неизвестного формата отбрасываются.
        }
    }

    private void HandleDownload(CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var path = operation.ResultFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            path = MakeUniquePath(Path.Combine(downloads, "download.bin"));
            e.ResultFilePath = path;
        }

        var fileName = Path.GetFileName(path);
        var assessment = DownloadSecurityService.Assess(fileName, operation.Uri);
        if (assessment.Level == DownloadRiskLevel.High)
        {
            var owner = Window.GetWindow(View);
            var message = $"Файл: {fileName}\nИсточник: {operation.Uri}\n\n{assessment.Description}.\n\nПродолжить загрузку?";
            var decision = owner is null
                ? GlassDialogWindow.Show(message, "Опасная загрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : GlassDialogWindow.Show(owner, message, "Опасная загрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (decision != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Оставляем стандартную панель загрузок WebView2 видимой для понятного UX.
        e.Handled = false;
        var item = new DownloadItem
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            SourceUrl = operation.Uri,
            BytesReceived = operation.BytesReceived,
            TotalBytes = NormalizeTotalBytes(operation.TotalBytesToReceive)
        };
        DownloadSecurityService.SetAssessment(item, assessment);
        DownloadService.Add(item);

        operation.BytesReceivedChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            item.BytesReceived = operation.BytesReceived;
            item.TotalBytes = NormalizeTotalBytes(operation.TotalBytesToReceive);
        });
        operation.StateChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            item.Status = operation.State switch
            {
                CoreWebView2DownloadState.Completed => "Завершено",
                CoreWebView2DownloadState.Interrupted => "Прервано: " + operation.InterruptReason,
                _ => "Загрузка"
            };
            if (operation.State == CoreWebView2DownloadState.Completed)
                _ = DownloadSecurityService.InspectCompletedAsync(item);
        });
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static long NormalizeTotalBytes(ulong? value)
    {
        if (!value.HasValue) return 0;
        return value.Value > long.MaxValue ? long.MaxValue : (long)value.Value;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
