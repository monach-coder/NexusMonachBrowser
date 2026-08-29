using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// Мост аннотирования: скрипт внедряется в каждую страницу до загрузки
/// документов. Плавающая панель над выделением (цвета, заметка, Markdown,
/// захват видео), подсветка сохранённых цитат при загрузке, конвертер
/// выделения в Markdown и запись фрагмента играющего видео (MediaRecorder
/// поверх canvas-потока; сайты с CORS-защитой честно отклоняются).
/// </summary>
public static class AnnotationsBridge
{
    public const string Script = """
        (() => {
          const send = m => { try { window.chrome?.webview?.postMessage(m); } catch (_) {} };
          const COLORS = { Yellow:'#ffe066', Green:'#a3e635', Red:'#fca5a5', Blue:'#93c5fd' };
          let audioSources = new WeakMap();

          // ── Подсветка сохранённых цитат при загрузке страницы ──
          const textNodesUnder = root => {
            const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
              acceptNode: n => (n.parentNode.closest('script,style,noscript,mark[data-nexus]'))
                ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
            const list = []; while (walker.nextNode()) list.push(walker.currentNode); return list;
          };
          const findQuote = quote => {
            for (const node of textNodesUnder(document.body)) {
              const index = node.textContent.indexOf(quote);
              if (index < 0) continue;
              const range = document.createRange();
              range.setStart(node, index); range.setEnd(node, index + quote.length);
              return range;
            }
            return null;
          };
          window.nexusApplyHighlights = items => {
            for (const item of items) {
              const range = findQuote(item.quote);
              if (!range) continue;
              try {
                const mark = document.createElement('mark');
                mark.dataset.nexus = item.id;
                mark.style.background = COLORS[item.color] || COLORS.Yellow;
                mark.style.color = '#111';
                if (item.note) mark.title = item.note;
                range.surroundContents(mark);
              } catch (_) {}
            }
          };

          // ── Выделение → Markdown ──
          const inlineMd = node => {
            if (node.nodeType === Node.TEXT_NODE) return node.textContent;
            if (node.nodeType !== Node.ELEMENT_NODE) return '';
            const kids = () => [...node.childNodes].map(inlineMd).join('');
            switch (node.tagName) {
              case 'B': case 'STRONG': return kids() ? '**' + kids() + '**' : '';
              case 'I': case 'EM': return kids() ? '*' + kids() + '*' : '';
              case 'CODE': return '`' + node.textContent + '`';
              case 'A': return '[' + kids() + '](' + (node.href || '') + ')';
              case 'BR': return '\n';
              case 'IMG': return '![' + (node.alt || '') + '](' + (node.src || '') + ')';
              default: return kids();
            }
          };
          window.nexusSelectionMarkdown = () => {
            const sel = window.getSelection();
            if (!sel || sel.isCollapsed || sel.rangeCount === 0) return '';
            const frag = sel.getRangeAt(0).cloneContents();
            const lines = [];
            for (const child of frag.childNodes) {
              if (child.nodeType === Node.TEXT_NODE) { lines.push(child.textContent); continue; }
              if (child.nodeType !== Node.ELEMENT_NODE) continue;
              const tag = child.tagName;
              const inline = inlineMd(child).replace(/\s+/g, ' ').trim();
              if (/^H[1-6]$/.test(tag)) lines.push('#'.repeat(+tag[1]) + ' ' + inline);
              else if (tag === 'LI') lines.push('  - ' + inline);
              else if (tag === 'BLOCKQUOTE') lines.push('> ' + inline);
              else if (tag === 'PRE') lines.push('```\n' + child.textContent + '\n```');
              else if (tag === 'HR') lines.push('---');
              else if (inline) lines.push(inline);
            }
            return lines.join('\n\n').replace(/\n{3,}/g, '\n\n').trim();
          };

          // ── Захват фрагмента играющего видео ──
          const playingVideo = () =>
            [...document.querySelectorAll('video')].find(v => !v.paused && v.readyState > 2 && v.videoWidth > 0);
          async function captureVideo(seconds) {
            const video = playingVideo();
            if (!video) return { error: 'no-video' };
            const canvas = document.createElement('canvas');
            canvas.width = video.videoWidth; canvas.height = video.videoHeight;
            const ctx = canvas.getContext('2d');
            try { ctx.drawImage(video, 0, 0); }
            catch (_) { return { error: 'protected' }; }
            const stream = canvas.captureStream(30);
            let audioCtx = null;
            try {
              audioCtx = new AudioContext();
              let src = audioSources.get(video);
              if (!src) {
                src = audioCtx.createMediaElementSource(video);
                audioSources.set(video, src);
              }
              const dest = audioCtx.createMediaStreamDestination();
              src.connect(dest);
              src.connect(audioCtx.destination);
              dest.stream.getAudioTracks().forEach(t => stream.addTrack(t));
            } catch (_) {}
            const mime = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm']
              .find(m => window.MediaRecorder && MediaRecorder.isTypeSupported(m));
            if (!mime) return { error: 'no-recorder' };
            const rec = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 2000000 });
            const chunks = []; rec.ondataavailable = e => { if (e.data.size) chunks.push(e.data); };
            const stopped = new Promise(res => rec.onstop = res);
            const position = video.currentTime;
            rec.start(500);
            const draw = () => {
              if (rec.state !== 'recording') return;
              try { ctx.drawImage(video, 0, 0); } catch (_) {}
              requestAnimationFrame(draw);
            };
            draw();
            window.nexusRecording = { recorder: rec, left: seconds };
            window.nexusRecordingTimer = setInterval(() => {
              if (window.nexusRecording) window.nexusRecording.left--;
            }, 1000);
            await new Promise(r => setTimeout(r, seconds * 1000));
            rec.stop(); await stopped;
            clearInterval(window.nexusRecordingTimer);
            window.nexusRecording = null;
            stream.getTracks().forEach(t => t.stop());
            if (audioCtx) { try { await audioCtx.close(); } catch (_) {} }
            const blob = new Blob(chunks, { type: 'video/webm' });
            const bytes = new Uint8Array(await blob.arrayBuffer());
            let binary = '';
            for (let i = 0; i < bytes.length; i += 0x8000)
              binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            return { base64: btoa(binary), position, duration: seconds };
          }
          window.nexusCaptureVideo = seconds => captureVideo(seconds).then(result => {
            if (result.error) { send({ type: 'nexus-video-failed', reason: result.error }); return; }
            send({ type: 'nexus-video', base64: result.base64,
              position: result.position, duration: result.duration });
          });

          // ── Плавающая панель над выделением ──
          let bar = null, hideTimer = null;
          const ensureBar = () => {
            if (bar) return bar;
            bar = document.createElement('div');
            bar.id = 'nexus-annotate-bar';
            bar.style.cssText = 'position:fixed;z-index:2147483647;display:none;gap:2px;' +
              'background:#111827;border:1px solid #374151;border-radius:10px;padding:4px;' +
              'box-shadow:0 6px 18px rgba(0,0,0,.35);font:12px system-ui';
            const button = (label, title, fn, color) => {
              const b = document.createElement('button');
              b.textContent = label; b.title = title;
              b.style.cssText = 'border:0;background:transparent;color:' + (color || '#d1d5db') +
                ';padding:4px 7px;border-radius:6px;cursor:pointer;font:inherit';
              b.onmouseenter = () => b.style.background = '#1f2937';
              b.onmouseleave = () => b.style.background = 'transparent';
              b.onclick = fn;
              return b;
            };
            const sel = () => String(window.getSelection() || '');
            for (const [name, color] of Object.entries(COLORS))
              bar.appendChild(button('\u25cf', 'Выделить ' + name.toLowerCase(), () => {
                send({ type: 'nexus-annotate', quote: sel(), color: name });
                applyOne(sel(), name);
              }, color));
            bar.appendChild(button('📝', 'Заметка к выделению', () => {
              const note = prompt('Заметка к выделению:');
              if (note) send({ type: 'nexus-note', quote: sel(), note });
            }));
            bar.appendChild(button('⧉', 'Копировать как Markdown', () => {
              const md = window.nexusSelectionMarkdown();
              if (md) send({ type: 'nexus-copy-md', markdown: md });
            }));
            bar.appendChild(button('🎬', 'Записать 30 с видео', () => {
              window.nexusCaptureVideo(30);
            }, '#fbbf24'));
            document.documentElement.appendChild(bar);
            return bar;
          };
          const applyOne = (quote, color) => {
            const range = quote ? findQuote(quote) : null;
            if (!range) return;
            try {
              const mark = document.createElement('mark');
              mark.dataset.nexus = 'new';
              mark.style.background = COLORS[color] || COLORS.Yellow;
              mark.style.color = '#111';
              range.surroundContents(mark);
            } catch (_) {}
          };
          document.addEventListener('selectionchange', () => {
            clearTimeout(hideTimer);
            hideTimer = setTimeout(() => { if (bar) bar.style.display = 'none'; }, 4000);
          });
          document.addEventListener('mouseup', () => {
            setTimeout(() => {
              const sel = window.getSelection();
              if (!sel || sel.isCollapsed) return;
              const rect = sel.getRangeAt(0).getBoundingClientRect();
              if (!rect || (!rect.width && !rect.height)) return;
              const b = ensureBar();
              b.style.display = 'flex';
              b.style.left = Math.max(8, Math.min(window.innerWidth - 240, rect.left)) + 'px';
              b.style.top = Math.max(8, rect.top - 42) + 'px';
            }, 60);
          });
        })();
        """;

    /// <summary>JSON-упаковка подсветок для nexusApplyHighlights.</summary>
    public static string HighlightsScript(IReadOnlyList<PageAnnotation> highlights)
    {
        var payload = JsonSerializer.Serialize(highlights
            .Where(a => a.Kind == AnnotationKind.Highlight || a.Kind == AnnotationKind.Note)
            .Select(a => new
            {
                id = a.Id.ToString(),
                quote = a.Quote,
                color = a.Color.ToString(),
                note = a.Note
            }), new JsonSerializerOptions
            {
                // Кириллица без \u-экранирования: JSON встраивается в JS,
                // юникод безопасен и читаем.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        return "window.nexusApplyHighlights?.(" + payload + ");";
    }
}
