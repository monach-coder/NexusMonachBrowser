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
/// Медиа вкладки: видеосостояние, ускорение, буферизация, захват аудио, живой дубляж, аудиодорожка
/// </summary>
public sealed partial class BrowserTab
{
    /// <summary>
    /// Состояние активного видео для предперевода: позиция, длительность,
    /// пауза и скорость — основа адаптивного ускорения анализа и точного
    /// расписания готового дубляжа.
    /// </summary>
    public async Task<VideoPlaybackState?> GetVideoStateAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return null;
        try
        {
            var json = await Core.ExecuteScriptAsync("""
                (()=>{const videos=[...document.querySelectorAll('video')]
                  .filter(video=>video.getClientRects().length>0)
                  .sort((left,right)=>(right.clientWidth*right.clientHeight)-(left.clientWidth*left.clientHeight));
                  const video=videos.find(item=>!item.paused&&!item.ended)||videos[0];
                  if(!video||!Number.isFinite(video.duration))return null;
                  return {position:video.currentTime,duration:video.duration,paused:video.paused,ended:video.ended,rate:video.playbackRate}})()
                """);
            var state = JsonSerializer.Deserialize<VideoPlaybackState>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return state is { Duration: > 0 } ? state : null;
        }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "GetVideoStateAsync", swallowed);
            return null;
        }
    }

    /// <summary>Устанавливает скорость анализа (ускоренный прогон фильма).</summary>
    public async Task SetVideoAnalysisRateAsync(double rate)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (()=>{const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
              .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=videos.find(v=>!v.paused&&!v.ended)||videos[0];if(!video)return;
              if(!window.__nexusAnalysisRate)window.__nexusAnalysisRate={video,rate:video.playbackRate};
              video.playbackRate={{rate.ToString(CultureInfo.InvariantCulture)}};video.play().catch(()=>{})})()
            """);
    }

    /// <summary>
    /// Готовит видео к ускоренному прогону анализа: ставит на паузу и
    /// запоминает исходную скорость. Позиция не трогается — анализ всегда
    /// начинается с текущего места просмотра.
    /// </summary>
    public async Task PrepareVideoForAnalysisAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
              .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=videos[0];if(!video)return;
              if(!window.__nexusAnalysisRate)window.__nexusAnalysisRate={video,rate:video.playbackRate};
              video.pause()})()
            """);
    }

    /// <summary>
    /// «Невидимая буферизация»: кадр видео замирает (замороженный снимок поверх
    /// плеера), звук для зрителя глушится, а под этим ускоренно идёт прогон
    /// анализа. Зритель видит паузу и компактную карточку-статус — не сам прогон.
    /// </summary>
    public async Task SetBufferingVeilAsync(bool visible, string text)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$$"""
            ((visible,text)=>{
              if(!window.__nexusApplyMix)window.__nexusApplyMix=()=>{
                const cap=window.__nexusLiveAudioCapture;
                const duck=window.__nexusDuckOriginal;
                const veiled=window.__nexusBufferingVeil;
                const loopback=window.__nexusLoopbackCapture===true;
                const video=cap&&cap.video?cap.video:(veiled?veiled.video:null);
                if(!video)return;
                // Громкость элемента неприкосновенна: любое её изменение режет
                // сигнал тапа до нуля. Всё управление слышимостью — только
                // через узел speaker (или мьют, пока граф не построен). В
                // режиме системного захвата звук остаётся слышимым — конвейер
                // слушает выход страницы.
                if(cap&&cap.speaker&&cap.mode==='element'){try{cap.speaker.gain.value=(veiled&&!loopback)?0:(duck!=null?(loopback?Math.max(duck,0.3):duck):1)}catch{}return}
                if(veiled&&!loopback){try{video.muted=true}catch{}return}
              };
              let veil=window.__nexusBufferingVeil;
              if(!visible){
                if(veil){try{veil.video.muted=veil.wasMuted}catch{}try{veil.frame.remove()}catch{}try{veil.card.remove()}catch{}try{removeEventListener('resize',veil.reposition)}catch{}}
                window.__nexusBufferingVeil=null;
                window.__nexusApplyMix();return}
              const saved=window.__nexusAnalysisRate;
              const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
                .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=(saved?.video?.isConnected?saved.video:null)||videos[0];if(!video)return;
              if(!veil||veil.video!==video||!veil.frame.isConnected){
                if(veil){try{veil.video.muted=veil.wasMuted}catch{}try{veil.frame.remove()}catch{}try{veil.card.remove()}catch{}try{removeEventListener('resize',veil.reposition)}catch{}}
                const frame=document.createElement('img');frame.dataset.nexusTranslationUi='true';
                try{const canvas=document.createElement('canvas');
                  canvas.width=video.videoWidth||1280;canvas.height=video.videoHeight||720;
                  canvas.getContext('2d').drawImage(video,0,0,canvas.width,canvas.height);
                  frame.src=canvas.toDataURL('image/jpeg',0.82)}
                catch{frame.src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='}
                frame.style.cssText='position:fixed;z-index:2147483646;background:#000;object-fit:contain';
                const card=document.createElement('div');card.dataset.nexusTranslationUi='true';
                card.style.cssText='position:fixed;z-index:2147483647;background:#050a0ff2;border:1px solid #55d8cc66;border-radius:10px;color:#eafffc;font:600 12.5px Segoe UI,sans-serif;line-height:1.5;white-space:pre-line;padding:10px 16px;pointer-events:none;text-align:left;box-shadow:0 6px 20px rgba(0,0,0,.35);backdrop-filter:blur(6px)';
                const wasMuted=video.muted;
                veil={video,frame,card,wasMuted,reposition:null};
                const position=()=>{
                  const r=video.getBoundingClientRect();
                  frame.style.left=r.left+'px';frame.style.top=r.top+'px';
                  frame.style.width=r.width+'px';frame.style.height=r.height+'px';
                  card.style.left=Math.max(8,r.left+16)+'px';card.style.top=Math.max(8,r.top+16)+'px';
                  card.style.maxWidth=Math.max(160,Math.min(420,r.width-32))+'px'};
                position();veil.reposition=position;addEventListener('resize',position);
                window.__nexusBufferingVeil=veil;
                document.documentElement.append(frame);document.documentElement.append(card)}
              window.__nexusApplyMix();
              veil.card.textContent=text})((
            {{{(visible ? "true" : "false")}}},{{{JsonSerializer.Serialize(text)}}})
            """);
    }

    /// <summary>
    /// Сбрасывает накопленный буфер захвата: после перемотки хвост прошлого
    /// материала не должен подмешиваться в новые сегменты стока.
    /// </summary>
    public async Task FlushAudioCaptureBufferAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const state=window.__nexusLiveAudioCapture;
              if(state){state.chunks=[];state.total=0;state.minimum=0}
              window.__nexusLiveAudioOverlap=null})()
            """);
    }

    /// <summary>
    /// Ставит видео анализа на паузу, не трогая сохранённую скорость: медиа
    /// замирает, пока распознавание думает — речь не проскакивает мимо.
    /// </summary>
    public async Task PauseActiveVideoAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const saved=window.__nexusAnalysisRate;
              const video=saved?.video?.isConnected?saved.video:[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
                .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*b.clientHeight))[0];
              if(video)video.pause()})()
            """);
    }

    /// <summary>
    /// Восстанавливает исходную скорость видео, если анализ не сделал этого
    /// сам. Безобиден при повторном вызове — страховка на любом исходе.
    /// </summary>
    public async Task RestoreVideoRateAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const saved=window.__nexusAnalysisRate;if(!saved)return;window.__nexusAnalysisRate=null;
              if(saved.video?.isConnected)saved.video.playbackRate=saved.rate??1})()
            """);
    }

    /// <summary>
    /// Включает режим системного захвата: тишина вуали и приглушение не
    /// должны глушить страницу, когда конвейер слушает её системный выход.
    /// Подготовка становится слышимой — карточка предупреждает об этом.
    /// </summary>
    public async Task SetLoopbackCaptureModeAsync(bool enabled)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync(
            $$"""(()=>{window.__nexusLoopbackCapture={{(enabled ? "true" : "false")}};if(window.__nexusApplyMix)window.__nexusApplyMix()})()""");
    }

    private string? _lastAudioTrackUrl;
    private TaskCompletionSource<string>? _audioTrackUrlReady;

    /// <summary>
    /// Включает наблюдение за сетевыми запросами страницы: как только плеер
    /// запросит очередной кусок отдельной аудиодорожки (YouTube грузит звук
    /// независимо от видео), URL запоминается. Наблюдение ничего не
    /// перехватывает и не меняет — плеер работает как раньше.
    /// </summary>
    public void EnableAudioTrackWatch()
    {
        var core = Core;
        if (core is null) return;
        try
        {
            _audioTrackUrlReady = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebResourceRequested -= OnWebResourceRequested;
            core.AddWebResourceRequestedFilter("*",
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "audio-watch", ex);
        }
    }

    private void OnWebResourceRequested(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var url = args.Request?.Uri?.ToString();
            if (string.IsNullOrEmpty(url) || !url.Contains("videoplayback", StringComparison.Ordinal))
                return;
            if (!Uri.UnescapeDataString(url).Contains("mime=audio", StringComparison.Ordinal))
                return;
            _lastAudioTrackUrl = url;
            _audioTrackUrlReady?.TrySetResult(url);
        }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "OnWebResourceRequested", swallowed);
        }
    }

    /// <summary>Отключает наблюдение за сетевыми запросами (конец сеанса).</summary>
    public void DisableAudioTrackWatch()
    {
        var core = Core;
        if (core is null) return;
        try
        {
            core.WebResourceRequested -= OnWebResourceRequested;
            core.RemoveWebResourceRequestedFilter("*",
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "DisableAudioTrackWatch", swallowed);
        }
        _audioTrackUrlReady = null;
    }

    /// <summary>
    /// URL отдельной аудиодорожки адаптивного потока. Сначала — уже
    /// подсмотренный в сети (надёжно: performance-записи страницы вытесняются
    /// из буфера тысячами других записей), затем — короткое ожидание нового
    /// запроса плеера, и лишь потом — performance-записи.
    /// </summary>
    public async Task<(string? Url, string Referer)> GetVideoAudioTrackUrlAsync(
        int waitForNetworkMs = 8_000)
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return (null, CurrentUrl);
        if (!string.IsNullOrEmpty(_lastAudioTrackUrl))
            return (_lastAudioTrackUrl, CurrentUrl);
        if (_audioTrackUrlReady is not null && waitForNetworkMs > 0)
        {
            var ready = await Task.WhenAny(_audioTrackUrlReady.Task, Task.Delay(waitForNetworkMs));
            if (ready == _audioTrackUrlReady.Task && !string.IsNullOrEmpty(_lastAudioTrackUrl))
                return (_lastAudioTrackUrl, CurrentUrl);
        }
        try
        {
            var json = await Core.ExecuteScriptAsync("""
                (()=>{try{
                  const entries=performance.getEntriesByType('resource')
                    .filter(e=>e.name&&e.name.indexOf('videoplayback')>=0&&decodeURIComponent(e.name).indexOf('mime=audio')>=0);
                  entries.sort((a,b)=>b.transferSize-a.transferSize);
                  const best=entries[0];
                  return best?JSON.stringify({url:best.name,referrer:location.href}):null}catch{return null}})()
                """);
            var payload = JsonSerializer.Deserialize<AudioTrackProbe>(json ?? "null",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (payload?.Url, payload?.Referrer ?? CurrentUrl);
        }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "GetVideoAudioTrackUrlAsync", swallowed);
            return (null, CurrentUrl);
        }
    }

    private sealed record AudioTrackProbe(string Url, string Referrer);

    /// <summary>Перемещает видео на позицию (для прогона анализа от границы).</summary>
    public async Task SeekVideoAsync(double positionSeconds)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (()=>{const saved=window.__nexusAnalysisRate;
              const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
              .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=saved?.video?.isConnected?saved.video:(videos[0]||null);if(!video)return;
              video.currentTime={{positionSeconds.ToString(CultureInfo.InvariantCulture)}}})()
            """);
    }

    /// <summary>
    /// Возвращает видео к показу с обычной скоростью с указанной позиции.
    /// </summary>
    public async Task ResumeVideoFromAsync(double positionSeconds)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync($$"""
            (()=>{const saved=window.__nexusAnalysisRate;window.__nexusAnalysisRate=null;
              const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0)
              .sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=saved?.video?.isConnected?saved.video:(videos[0]||null);if(!video)return;
              video.playbackRate=saved?saved.rate:1;
              video.currentTime={{positionSeconds.ToString(CultureInfo.InvariantCulture)}};video.play().catch(()=>{})})()
            """);
    }

    public async Task<double?> GetActiveVideoDurationAsync()
    {
        if (Core is null || UrlService.IsInternal(CurrentUrl)) return null;
        try
        {
            var json = await Core.ExecuteScriptAsync("""
                (()=>{const videos=[...document.querySelectorAll('video')]
                  .filter(video=>video.getClientRects().length>0)
                  .sort((left,right)=>(right.clientWidth*right.clientHeight)-(left.clientWidth*left.clientHeight));
                  const video=videos.find(item=>!item.paused&&!item.ended)||videos[0];
                  return video&&Number.isFinite(video.duration)&&video.duration>0?video.duration:null})()
                """);
            return double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var duration) && double.IsFinite(duration) && duration > 0
                ? duration
                : null;
        }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "GetActiveVideoDurationAsync", swallowed);
            return null;
        }
    }

    /// <summary>
    /// Собирает сегмент звука активного видео. При <paramref name="milliseconds"/>
    /// &gt; 0 ждёт ровно столько wall-времени; при 0 — режим стока: отдаёт всё
    /// накопленное с прошлого раза (не меньше секунды), что вместе с 90-секундным
    /// кольцевым буфером страницы даёт непрерывный перевод без дыр на ×1.
    /// </summary>
    public async Task<AudioCaptureResult> CaptureActiveVideoAudioAsync(int milliseconds,
        CancellationToken cancellationToken = default, int overlapMilliseconds = 0)
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
                const dispose=state=>{if(!state)return;state.closed=true;
                  // Персистентный тап элемента переживает сеанс: контекст и
                  // источник не закрываются, иначе повторный
                  // createMediaElementSource на том же элементе невозможен и
                  // все следующие сеансы упадут в мёртвый captureStream.
                  try{state.processor.disconnect()}catch{}
                  try{state.source.disconnect(state.processor)}catch{}
                  try{state.silent.disconnect()}catch{}
                  window.__nexusLiveAudioOverlap=null};
                if(video.paused||video.ended){dispose(window.__nexusLiveAudioCapture);window.__nexusLiveAudioCapture=null;window.__nexusLiveAudioOverlap=null;return {Success:false,WaitingForPlayback:true,Error:'Видео на паузе.',WavBase64:''}}
                let state=window.__nexusLiveAudioCapture;
                if(!state||state.closed||state.video!==video){
                  dispose(state);
                  let tap=window.__nexusElementTap;
                  if(!tap||tap.video!==video||!tap.context){
                    if(tap){try{tap.context.close()}catch{}}
                    const context=new AudioContext();
                    let source=null,tracks=[],mode='element';
                    // Основной путь — тап звука самого элемента: в WebView2
                    // captureStream часто не отдаёт аудиодорожек вовсе, а
                    // MediaElementSource читает звук независимо от громкости
                    // страницы и позволяет управлять слышимостью отдельным
                    // узлом усиления.
                    try{source=context.createMediaElementSource(video)}
                    catch{mode='stream'}
                    if(!source){
                      const capture=video.captureStream||video.mozCaptureStream;
                      if(!capture){try{context.close()}catch{};return {Success:false,Error:'Этот проигрыватель не разрешает захват звукового потока.',WavBase64:''};}
                      const stream=capture.call(video);tracks=stream.getAudioTracks();
                      if(!tracks.length){tracks.forEach(t=>t.stop());try{context.close()}catch{};return {Success:false,Error:'В видеопотоке нет доступной аудиодорожки или она защищена DRM.',WavBase64:''}}
                      source=context.createMediaStreamSource(stream);
                    }
                    const speaker=context.createGain();speaker.gain.value=1;
                    source.connect(speaker);speaker.connect(context.destination);
                    tap={video,context,source,speaker,tracks,mode};
                    window.__nexusElementTap=tap;
                  }
                  const context=tap.context,source=tap.source,speaker=tap.speaker,tracks=tap.tracks,mode=tap.mode;
                  const processor=context.createScriptProcessor(4096,1,1),silent=context.createGain();silent.gain.value=0;
                  if(mode==='element'){
                    // Приглушение и тишина вуали живут в графе (узел speaker),
                    // поэтому элементу возвращаю честные громкость и мьют —
                    // тап обязан читать полный сигнал, а не 12% приглушения.
                    const dub=window.__nexusDubbingVideoState;
                    if(dub&&dub.video===video&&video.volume<dub.volume){try{video.volume=dub.volume}catch{}}
                    const veiled=window.__nexusBufferingVeil;
                    if(veiled){try{video.muted=!!veiled.wasMuted}catch{}}
                  }
                  state={video,context,source,processor,silent,speaker,tracks,mode,chunks:[],total:0,sampleRate:context.sampleRate,closed:false,waiters:[],minimum:0};
                  processor.onaudioprocess=e=>{
                    if(state.closed)return;
                    const chunk=new Float32Array(e.inputBuffer.getChannelData(0));state.chunks.push(chunk);state.total+=chunk.length;
                    // Кольцевой буфер на 90 секунд: пока конвейер думает над
                    // предыдущим куском, звук накапливается без потерь — на
                    // обычной скорости ×1 это даёт непрерывный перевод без дыр.
                    const maximum=Math.ceil(state.sampleRate*90);
                    while(state.total>maximum&&state.chunks.length>1){const removed=state.chunks.shift();state.total-=removed.length}
                    // Фоновая вкладка троттлит setTimeout до раза в секунду, и опрос
                    // готовности сегмента замедлял конвейер перевода. Аудио-поток
                    // пробуждает ожидающий захват сам — таймер остаётся лишь страховкой.
                    if(state.waiters&&state.waiters.length&&state.total>=state.minimum){
                      const ready=state.waiters;state.waiters=[];ready.forEach(resolve=>resolve());
                    }
                  };
                  source.connect(processor);processor.connect(silent);silent.connect(context.destination);await context.resume();
                  window.__nexusLiveAudioCapture=state;
                  if(window.__nexusApplyMix)window.__nexusApplyMix();
                }
                const drain=__MILLISECONDS__<=0,collectMs=drain?1000:__MILLISECONDS__;
                const minimum=Math.floor(state.sampleRate*collectMs/1000),deadline=Date.now()+collectMs+(drain?1500:2500);
                const positionBefore=video.currentTime;
                state.minimum=minimum;
                while(state.total<minimum&&Date.now()<deadline){
                  if(video.paused||video.ended||state.closed)break;
                  if(!state.waiters)state.waiters=[];
                  await new Promise(resolve=>{
                    const wake=()=>{clearTimeout(guard);resolve()};
                    const guard=setTimeout(wake,500);
                    state.waiters.push(wake);
                  });
                }
                if(video.paused||video.ended){dispose(state);window.__nexusLiveAudioCapture=null;window.__nexusLiveAudioOverlap=null;return {Success:false,WaitingForPlayback:true,Error:'Видео на паузе.',WavBase64:''}}
                const chunks=state.chunks;state.chunks=[];const length=state.total;state.total=0;
                let current=new Float32Array(length);let offset=0;
                for(const chunk of chunks){current.set(chunk,offset);offset+=chunk.length}
                // Сток ограничен ~20 с: гигантский кусовин whisper гоняет по
                // полминуте и подвешивает конвейер; хвост остаётся в буфере.
                if(drain&&current.length>state.sampleRate*20){
                  const limit=Math.floor(state.sampleRate*20);
                  const rest=new Float32Array(current.subarray(limit));
                  current=new Float32Array(current.subarray(0,limit));
                  state.chunks=[rest];state.total=rest.length}
                if(!current.length) return {Success:false,Error:'Браузер не получил аудиосэмплы.',WavBase64:''};
                let energy=0;for(let i=0;i<current.length;i+=32)energy+=current[i]*current[i];
                if(Math.sqrt(energy/Math.max(1,current.length/32))<0.0001){window.__nexusLiveAudioOverlap=null;return {Success:true,Error:'silence',WavBase64:'',VideoPosition:positionBefore,VideoRate:video.playbackRate||1}}
                const overlapSamples=Math.min(current.length,Math.floor(state.sampleRate*__OVERLAP__/1000));
                const previous=window.__nexusLiveAudioOverlap instanceof Float32Array?window.__nexusLiveAudioOverlap:null;
                window.__nexusLiveAudioOverlap=overlapSamples>0?current.slice(current.length-overlapSamples):null;
                const input=previous?.length?new Float32Array(previous.length+current.length):current;
                if(previous?.length){input.set(previous,0);input.set(current,previous.length)}
                const outRate=16000,ratio=state.sampleRate/outRate,outLength=Math.floor(input.length/ratio),pcm=new Int16Array(outLength);
                for(let i=0;i<outLength;i++){const start=Math.floor(i*ratio),end=Math.min(input.length,Math.floor((i+1)*ratio));let sum=0;for(let j=start;j<end;j++)sum+=input[j];const value=Math.max(-1,Math.min(1,sum/Math.max(1,end-start)));pcm[i]=value<0?value*32768:value*32767}
                const bytes=new Uint8Array(44+pcm.length*2),view=new DataView(bytes.buffer),write=(p,s)=>{for(let i=0;i<s.length;i++)view.setUint8(p+i,s.charCodeAt(i))};
                write(0,'RIFF');view.setUint32(4,36+pcm.length*2,true);write(8,'WAVE');write(12,'fmt ');view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);view.setUint32(24,outRate,true);view.setUint32(28,outRate*2,true);view.setUint16(32,2,true);view.setUint16(34,16,true);write(36,'data');view.setUint32(40,pcm.length*2,true);for(let i=0;i<pcm.length;i++)view.setInt16(44+i*2,pcm[i],true);
                let binary='';for(let i=0;i<bytes.length;i+=32768)binary+=String.fromCharCode(...bytes.subarray(i,i+32768));
                return {Success:true,Error:'',WavBase64:btoa(binary),VideoPosition:positionBefore,VideoRate:video.playbackRate||1};
              } catch(error) { return {Success:false,Error:error?.message||String(error),WavBase64:''}; }
            })();
            """.Replace("__MILLISECONDS__",
                milliseconds <= 0 ? "0" : Math.Clamp(milliseconds, 1_200, 4_000).ToString(),
                StringComparison.Ordinal)
            .Replace("__OVERLAP__", Math.Clamp(overlapMilliseconds, 0, 1_200).ToString(),
                StringComparison.Ordinal);
        var json = await Core.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
        try { return JsonSerializer.Deserialize<AudioCaptureResult>(json) ?? new AudioCaptureResult { Error = "Пустой результат захвата." }; }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "CaptureActiveVideoAudioAsync", swallowed);
            return new AudioCaptureResult { Error = "Не удалось прочитать аудиопоток страницы." };
        }
    }

    public async Task BeginLiveAudioTranslationAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const capture=window.__nexusLiveAudioCapture;if(capture){capture.closed=true;try{capture.processor.disconnect()}catch{}try{capture.source.disconnect()}catch{}try{capture.silent.disconnect()}catch{}try{capture.tracks.forEach(t=>t.stop())}catch{}try{capture.context.close()}catch{}}
              window.__nexusLiveAudioCapture=null;window.__nexusStopAudioTranslation=false;window.__nexusLiveAudioOverlap=null;
              const sessionVideos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              window.__nexusLiveVideoSession={href:location.href,video:sessionVideos.find(v=>!v.paused&&!v.ended)||sessionVideos[0]||null};
              const previousCaptions=window.__nexusCaptionSuppression;
              if(previousCaptions){try{previousCaptions.observer.disconnect()}catch{}try{previousCaptions.entries.forEach(x=>{if(x.track)x.track.mode=x.mode})}catch{}try{previousCaptions.style.remove()}catch{}}
              const captionEntries=[];
              const disableCaptions=()=>{for(const video of document.querySelectorAll('video')){try{for(const track of video.textTracks){if(!captionEntries.some(x=>x.track===track))captionEntries.push({track,mode:track.mode});track.mode='disabled'}}catch{}}};
              const captionStyle=document.createElement('style');captionStyle.id='nexus-hide-video-captions';captionStyle.dataset.nexusTranslationUi='true';captionStyle.textContent='video::cue{color:transparent!important;background:transparent!important;opacity:0!important}.ytp-caption-window-container,.ytp-caption-segment,.ytp-caption-window-bottom,.jw-text-track-container,.vjs-text-track-display,.plyr__captions,.shaka-text-container,.dplayer-subtitle,.art-subtitle,.fp-captions{display:none!important;visibility:hidden!important;opacity:0!important}';document.documentElement.append(captionStyle);
              disableCaptions();const captionObserver=new MutationObserver(disableCaptions);captionObserver.observe(document.documentElement,{childList:true,subtree:true});window.__nexusCaptionSuppression={entries:captionEntries,observer:captionObserver,style:captionStyle};
              let overlay=document.getElementById('nexus-live-voice-status');
              if(!overlay){overlay=document.createElement('div');overlay.id='nexus-live-voice-status';overlay.dataset.nexusTranslationUi='true';overlay.style.cssText='position:fixed;z-index:2147483647;padding:6px 10px;border:1px solid #55d8cc;border-radius:8px;background:#d0101820;color:#eafffc;font:600 12px Segoe UI,sans-serif;pointer-events:none;box-sizing:border-box';document.documentElement.append(overlay)}
              let stop=document.getElementById('nexus-live-translation-stop');
              if(!stop){stop=document.createElement('button');stop.id='nexus-live-translation-stop';stop.dataset.nexusTranslationUi='true';stop.textContent='Стоп';stop.style.cssText='position:fixed;z-index:2147483647;border:1px solid #66ffffff;border-radius:7px;background:#d0000000;color:#fff;padding:4px 8px;cursor:pointer;font:600 12px Segoe UI,sans-serif';stop.onclick=()=>{window.__nexusStopAudioTranslation=true;overlay.textContent='Остановка…'};document.documentElement.append(stop)}
              const place=()=>{const v=[...document.querySelectorAll('video')].filter(x=>x.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight))[0];if(!v)return;const r=v.getBoundingClientRect();overlay.style.right=Math.max(8,innerWidth-r.right+8)+'px';overlay.style.top=Math.max(8,r.top+8)+'px';stop.style.right=Math.max(8,innerWidth-r.right+8)+'px';stop.style.top=Math.max(8,r.top+44)+'px'};
              if(window.__nexusPlaceLiveTranslation){removeEventListener('resize',window.__nexusPlaceLiveTranslation);removeEventListener('scroll',window.__nexusPlaceLiveTranslation)}
              window.__nexusPlaceLiveTranslation=place;place();addEventListener('resize',place,{passive:true});addEventListener('scroll',place,{passive:true});overlay.textContent='Nexus Voice · подготовка…';})();
            """);
    }

    public async Task UpdateLiveAudioTranslationStatusAsync(string text)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("(()=>{const e=document.getElementById('nexus-live-voice-status');if(e)e.textContent=" +
                                      JsonSerializer.Serialize(text) + "})()");
    }

    public async Task<bool> ShouldStopLiveAudioTranslationAsync()
    {
        if (Core is null) return true;
        var json = await Core.ExecuteScriptAsync("""
            (()=>{if(window.__nexusStopAudioTranslation)return true;
              const session=window.__nexusLiveVideoSession;if(!session)return false;
              // Плееры дописывают в адрес параметры вроде &t=90 при перемотках:
              // сессия рвётся только при реальном уходе со страницы или смене
              // видео, а не от смены параметров воспроизведения.
              const key=href=>{try{const u=new URL(href);
                return u.origin+u.pathname+(u.searchParams.get('v')?'|v='+u.searchParams.get('v'):'')}catch{return href}};
              if(key(session.href)!==key(location.href))return true;
              const video=session.video;
              return Boolean(video&&(!video.isConnected||video.ended))})()
            """);
        return bool.TryParse(json, out var stopped) && stopped;
    }

    public Task PrepareVideoForSpokenTranslationAsync(bool pausePlayback) => Task.CompletedTask;

    public async Task EnableVideoDubbingMixAsync()
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            (()=>{const videos=[...document.querySelectorAll('video')].filter(v=>v.getClientRects().length>0).sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight));
              const video=videos.find(v=>!v.paused&&!v.ended)||videos[0];if(!video)return false;
              if(!window.__nexusDubbingVideoState)window.__nexusDubbingVideoState={video,muted:video.muted,volume:video.volume};
              // Приглушаем оригинал одним узлом микширования: при тапе через
              // MediaElementSource это выходной узел (конвейер читает полный
              // сигнал), иначе — громкость элемента как раньше.
              window.__nexusDuckOriginal=__ORIGINAL_VOLUME__;
              if(!window.__nexusApplyMix)window.__nexusApplyMix=()=>{
                const cap=window.__nexusLiveAudioCapture;
                const duck=window.__nexusDuckOriginal;
                const veiled=window.__nexusBufferingVeil;
                const loopback=window.__nexusLoopbackCapture===true;
                const target=cap&&cap.video?cap.video:(veiled?veiled.video:null);
                if(!target)return;
                if(cap&&cap.speaker&&cap.mode==='element'){try{cap.speaker.gain.value=(veiled&&!loopback)?0:(duck!=null?(loopback?Math.max(duck,0.3):duck):1)}catch{}return}
                if(veiled&&!loopback){try{target.muted=true}catch{}return}
              };
              window.__nexusApplyMix();return true;})()
            """.Replace("__ORIGINAL_VOLUME__",
                VideoDubbingPolicy.OriginalVolume.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
    }

    public Task ResumeVideoAfterSpokenTranslationAsync() => Task.CompletedTask;

    public async Task EndLiveAudioTranslationAsync(string status)
    {
        if (Core is null) return;
        await Core.ExecuteScriptAsync("""
            ((status)=>{window.__nexusStopAudioTranslation=true;window.__nexusLiveAudioOverlap=null;window.__nexusLiveVideoSession=null;window.__nexusDuckOriginal=null;
              // Тап элемента (__nexusElementTap) намеренно переживает сеанс:
              // контекст и источник нельзя закрывать, иначе повторный захват
              // того же видео на этой вкладке станет невозможен.
              const capture=window.__nexusLiveAudioCapture;window.__nexusLiveAudioCapture=null;
              if(capture){capture.closed=true;try{capture.processor.disconnect()}catch{}try{capture.source.disconnect(capture.processor)}catch{}try{capture.silent.disconnect()}catch{}}
              const tap=window.__nexusElementTap;if(tap&&tap.speaker)try{tap.speaker.gain.value=1}catch{}
              window.__nexusSpokenVideoState=null;
              const dubbing=window.__nexusDubbingVideoState;window.__nexusDubbingVideoState=null;if(dubbing?.video?.isConnected){dubbing.video.muted=dubbing.muted;dubbing.video.volume=dubbing.volume}
              const captions=window.__nexusCaptionSuppression;window.__nexusCaptionSuppression=null;if(captions){try{captions.observer.disconnect()}catch{}try{captions.entries.forEach(x=>{if(x.track)x.track.mode=x.mode})}catch{}try{captions.style.remove()}catch{}}
              const overlay=document.getElementById('nexus-live-voice-status');if(overlay){overlay.textContent=status;setTimeout(()=>overlay.remove(),1800)}
              document.getElementById('nexus-live-translation-stop')?.remove();
              if(window.__nexusPlaceLiveTranslation){removeEventListener('resize',window.__nexusPlaceLiveTranslation);removeEventListener('scroll',window.__nexusPlaceLiveTranslation)}
              window.__nexusPlaceLiveTranslation=null})(__STATUS__)
            """.Replace("__STATUS__", JsonSerializer.Serialize(status), StringComparison.Ordinal));
    }
}
