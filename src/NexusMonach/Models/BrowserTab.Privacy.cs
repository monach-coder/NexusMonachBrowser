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
/// Приватность вкладки: очистка данных сайта, защищённый рестарт, сетевой учёт хостов и трекеров
/// </summary>
public sealed partial class BrowserTab
{
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

    private void ResetNetworkSnapshot(string topLevelUrl)
    {
        lock (_networkLock)
        {
            _contactedHosts.Clear();
            _thirdPartyHosts.Clear();
            _blockedTrackerHosts.Clear();
            _networkRecipients.Clear();
            _observedPorts.Clear();
            _requestCount = 0;
            _networkSnapshotTruncated = false;
            _networkTopHost = Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var top) ? top.Host : string.Empty;
        }
    }

    private void RecordNetworkRequest(WebRequestObservation observation)
    {
        if (!Uri.TryCreate(observation.Url, UriKind.Absolute, out var request) ||
            request.Scheme is not ("http" or "https"))
            return;

        lock (_networkLock)
        {
            if (_requestCount < int.MaxValue) _requestCount++;
            var port = request.IsDefaultPort
                ? request.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : request.Port;
            if (port is > 0 and <= 65535)
            {
                if (_observedPorts.Count < 128 || _observedPorts.Contains(port))
                    _observedPorts.Add(port);
                else
                    _networkSnapshotTruncated = true;
            }

            var thirdParty = !string.IsNullOrWhiteSpace(_networkTopHost) &&
                             !IsSameSite(request.Host, _networkTopHost);

            var knownHost = _networkRecipients.ContainsKey(request.Host);
            if (!knownHost && _networkRecipients.Count >= MaxObservedNetworkHosts)
            {
                _networkSnapshotTruncated = true;
                return;
            }

            _contactedHosts.Add(request.Host);
            if (thirdParty) _thirdPartyHosts.Add(request.Host);
            if (observation.Blocked) _blockedTrackerHosts.Add(request.Host);

            if (!_networkRecipients.TryGetValue(request.Host, out var recipient))
            {
                recipient = new NetworkRecipientAccumulator(request.Host);
                _networkRecipients.Add(request.Host, recipient);
            }
            recipient.Observe(observation, thirdParty);
        }
    }

    private sealed class NetworkRecipientAccumulator(string host)
    {
        private readonly HashSet<string> _resourceKinds = new(StringComparer.OrdinalIgnoreCase);

        public string Host { get; } = host;
        public int RequestCount { get; private set; }
        public bool IsThirdParty { get; private set; }
        public bool IsKnownTracker { get; private set; }
        public bool WasBlocked { get; private set; }
        public bool SentCookies { get; private set; }
        public bool SentReferrer { get; private set; }
        public bool SentOrigin { get; private set; }

        public void Observe(WebRequestObservation observation, bool thirdParty)
        {
            if (RequestCount < int.MaxValue) RequestCount++;
            IsThirdParty |= thirdParty;
            IsKnownTracker |= observation.IsKnownTracker;
            WasBlocked |= observation.Blocked;
            SentCookies |= observation.HasCookieHeader;
            SentReferrer |= observation.HasReferrerHeader;
            SentOrigin |= observation.HasOriginHeader;
            if (!string.IsNullOrWhiteSpace(observation.ResourceKind) &&
                (_resourceKinds.Count < 32 || _resourceKinds.Contains(observation.ResourceKind)))
                _resourceKinds.Add(observation.ResourceKind);
        }

        public NetworkRecipientSnapshot Snapshot() => new(
            Host,
            RequestCount,
            IsThirdParty,
            IsKnownTracker,
            WasBlocked,
            SentCookies,
            SentReferrer,
            SentOrigin,
            _resourceKinds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsSameSite(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
        right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase);
}
