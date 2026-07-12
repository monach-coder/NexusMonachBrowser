const $=id=>document.getElementById(id);
let lastSelector='',selectionTimer=0,lastContext=null;
const endpoint='http://127.0.0.1:28471/analyze';

async function inspected(expression){
  return new Promise((resolve,reject)=>chrome.devtools.inspectedWindow.eval(expression,(value,error)=>error?reject(new Error(error.description||error.value||'Ошибка inspectedWindow')):resolve(value)));
}

async function selectedContext(){
  return inspected(`(()=>{const e=$0;if(!e)return null;const c=e.cloneNode(true);c.querySelectorAll?.('input,textarea,select').forEach(x=>{x.removeAttribute('value');x.textContent='' });
    const esc=v=>CSS.escape(v),selector=e.id?'#'+esc(e.id):e.tagName.toLowerCase()+[...e.classList].slice(0,3).map(x=>'.'+esc(x)).join('');
    const s=getComputedStyle(e),r=e.getBoundingClientRect();return{selector,tag:e.tagName.toLowerCase(),role:e.getAttribute('role')||'',text:(e.innerText||'').trim().slice(0,900),
    html:(c.outerHTML||'').slice(0,3500),box:{x:Math.round(r.x),y:Math.round(r.y),width:Math.round(r.width),height:Math.round(r.height)},
    style:{display:s.display,position:s.position,color:s.color,background:s.backgroundColor,font:s.font,margin:s.margin,padding:s.padding,overflow:s.overflow,zIndex:s.zIndex}}})()`);
}

async function harContext(){
  return new Promise(resolve=>chrome.devtools.network.getHAR(har=>resolve((har.entries||[]).slice(-120).map(entry=>{
    let url='';try{const u=new URL(entry.request.url);url=u.origin+u.pathname}catch{}
    return{method:entry.request.method,url,status:entry.response.status,mime:entry.response.content?.mimeType||'',time:Math.round(entry.time||0),size:entry.response.content?.size||0};
  }))));
}

async function ask(kind,context,question=''){
  $('status').textContent='Встроенный Nexus AI анализирует локально…';
  const clean={kind,diagnostics:context};
  const response=await fetch(endpoint,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({question:question||'Найди проблемы и объясни следующие безопасные шаги.',context:JSON.stringify(clean).slice(0,50000)})});
  const text=await response.text();let data;try{data=JSON.parse(text)}catch{data={error:text}}
  if(!response.ok)throw new Error(data.error||`Nexus AI ${response.status}`);
  $('model').textContent='Локально: '+(data.model||'Nexus Fast Intelligence');
  lastSelector=typeof data.selector==='string'?data.selector:'';$('highlight').disabled=!lastSelector;
  $('result').textContent=(data.answer||'Нет ответа')+'\n\n'+(data.steps||[]).map((x,i)=>`${i+1}. ${x}`).join('\n')+(data.reason?'\n\nПодсветка: '+data.reason:'');
  $('status').textContent='Готово · Nexus ничего не изменил';
}

async function analyzeElement(auto=false){
  try{lastContext=await selectedContext();if(!lastContext){$('status').textContent='Сначала выбери элемент во вкладке Elements.';return}await ask('выбранный DOM-элемент',lastContext,auto?'Кратко объясни назначение, возможные проблемы и где искать связанные стили/обработчики.':'')}
  catch(e){$('status').textContent=e.message;$('result').textContent=e.stack||e.message}
}

$('element').onclick=()=>analyzeElement(false);
$('network').onclick=async()=>{try{const har=await harContext();await ask('Network HAR',har,$('query').value)}catch(e){$('status').textContent=e.message}};
$('ask').onclick=async()=>{try{lastContext=await selectedContext()||await harContext();await ask('вопрос пользователя',lastContext,$('query').value)}catch(e){$('status').textContent=e.message}};
$('query').onkeydown=e=>{if(e.key==='Enter')$('ask').click()};
$('highlight').onclick=async()=>{if(!lastSelector)return;try{await inspected(`(()=>{const e=document.querySelector(${JSON.stringify(lastSelector)});if(!e)return false;e.style.outline='3px solid #dab96a';e.scrollIntoView({behavior:'smooth',block:'center'});return true})()`);$('status').textContent='Элемент подсвечен на странице. Других действий не выполнено.'}catch(e){$('status').textContent=e.message}};
chrome.devtools.panels.elements.onSelectionChanged.addListener(()=>{if(!$('auto').checked)return;clearTimeout(selectionTimer);$('status').textContent='Выбран новый элемент · анализ через 1,5 секунды…';selectionTimer=setTimeout(()=>analyzeElement(true),1500)});
$('model').textContent='Nexus Fast Intelligence · автономно';
