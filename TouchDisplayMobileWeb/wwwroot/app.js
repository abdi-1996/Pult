(() => {
  const $ = s => document.querySelector(s);
  const $$ = s => [...document.querySelectorAll(s)];
  const state = {
    pin: localStorage.getItem('tdweb.pin') || '',
    status: null,
    currentPath: '',
    files: [],
    recent: [],
    video: null,
    control: null,
    mouseMode: localStorage.getItem('tdweb.mouse') !== '0',
    pointers: new Map(),
    frameSize: {w:16,h:9},
    pendingFrame: null,
    decoding: false,
    statsTimer: null,
    latency: null
  };

  const login = $('#login'), app = $('#app'), pinInput = $('#pin'), loginError = $('#loginError');
  pinInput.value = state.pin;

  function api(path) {
    const join = path.includes('?') ? '&' : '?';
    return fetch(path + join + 'pin=' + encodeURIComponent(state.pin), {cache:'no-store'}).then(async r => {
      if (!r.ok) throw new Error((await r.json().catch(()=>({error:'Ошибка подключения'}))).error || 'Ошибка подключения');
      return r.json();
    });
  }

  async function connect() {
    const pin = pinInput.value.replace(/\D/g,'').slice(0,6);
    pinInput.value = pin;
    if (pin.length !== 6) { loginError.textContent = 'Введите 6 цифр'; return; }
    state.pin = pin;
    loginError.textContent = 'Подключение…';
    try {
      const status = await api('/api/status');
      localStorage.setItem('tdweb.pin', pin);
      state.status = status;
      login.classList.add('hidden');
      app.classList.remove('hidden');
      loginError.textContent = '';
      applyStatus(status);
      await loadRecent();
      startStatusLoop();
      go('home');
    } catch (e) {
      loginError.textContent = e.message || 'Не удалось подключиться';
    }
  }

  $('#connect').addEventListener('click', connect);
  pinInput.addEventListener('input', () => pinInput.value = pinInput.value.replace(/\D/g,'').slice(0,6));
  pinInput.addEventListener('keydown', e => { if (e.key === 'Enter') connect(); });
  let shown = false;
  $('#showPin').addEventListener('click', () => { shown = !shown; pinInput.type = shown ? 'text' : 'password'; });

  $('#logout').addEventListener('click', () => {
    stopDesktop();
    clearInterval(state.statsTimer);
    state.statsTimer = null;
    localStorage.removeItem('tdweb.pin');
    state.pin = '';
    pinInput.value = '';
    app.classList.add('hidden');
    login.classList.remove('hidden');
  });

  function applyStatus(s) {
    state.status = s;
    $('#pcName').textContent = s.pc;
    $('#pcSubtitle').textContent = s.pc;
    $('#pcRoute').textContent = s.tailscale ? 'Подключено через Tailscale' : (s.lan ? 'Подключено по LAN' : 'Подключено');
    $('#gpuPercent').textContent = Math.round(s.gpu.percent) + '%';
    $('#gpuName').textContent = s.gpu.name || 'GPU';
    $('#ramPercent').textContent = Math.round(s.ram.percent) + '%';
    $('#ramValue').textContent = s.ram.used + ' / ' + s.ram.total + ' ГБ';
    $('#netDown').textContent = s.network.down;
    $('#netUp').textContent = '↓ ' + s.network.down + '  ↑ ' + s.network.up + ' Мбит/с';
    $('#sysPc').textContent = s.pc;
    $('#sysUser').textContent = s.user;
    $('#sysLan').textContent = s.lan || '—';
    $('#sysTail').textContent = s.tailscale || '—';
    $('#sysResolution').textContent = s.width + ' × ' + s.height;
    $('#sysVersion').textContent = s.version;
    $('#desktopInfo').textContent = s.pc + ' • ' + s.width + ' × ' + s.height;
  }

  function startStatusLoop() {
    clearInterval(state.statsTimer);
    state.statsTimer = setInterval(async () => {
      if (document.hidden) return;
      const start = performance.now();
      try {
        const s = await api('/api/status');
        state.latency = Math.round(performance.now() - start);
        applyStatus(s);
        $('#connectionBadge').textContent = 'Online';
        $('#connectionBadge').className = 'badge online-badge';
        if ($('#pageDesktop').classList.contains('active')) $('#desktopLatency').textContent = state.latency + ' мс';
      } catch {
        $('#connectionBadge').textContent = 'Offline';
        $('#connectionBadge').className = 'badge';
      }
    }, 2500);
  }

  function go(name) {
    $('.page').forEach(p => p.classList.remove('active'));
    $('.nav-btn').forEach(b => b.classList.toggle('active', b.dataset.go === name));
    const page = $('#page' + name[0].toUpperCase() + name.slice(1));
    if (page) page.classList.add('active');

    const desktopMode = name === 'desktop';
    app.classList.toggle('desktop-mode', desktopMode);
    document.documentElement.style.background = desktopMode ? '#000' : '';
    document.body.style.background = desktopMode ? '#000' : '';

    if (name === 'files') loadFiles(state.currentPath || '');
    if (name === 'gallery') loadRecent();
    if (desktopMode) {
      $('#desktopQuickMenu')?.classList.add('hidden');
      startDesktop();
    } else {
      stopDesktop();
    }
  }
  // Reliable delegated navigation for Safari/PWA and nested button elements.
  document.addEventListener('click', (e) => {
    const target = e.target instanceof Element ? e.target.closest('[data-go]') : null;
    if (!target) return;
    const name = target.getAttribute('data-go');
    if (!name) return;
    e.preventDefault();
    e.stopPropagation();
    go(name);
  }, true);

  $('#desktopMenuBtn')?.addEventListener('click', (e) => {
    e.stopPropagation();
    $('#desktopQuickMenu')?.classList.toggle('hidden');
  });
  $('#desktopStage')?.addEventListener('pointerdown', () => {
    $('#desktopQuickMenu')?.classList.add('hidden');
  }, {capture:true});

  function fmtSize(n) {
    if (!n) return '';
    const u=['Б','КБ','МБ','ГБ','ТБ']; let i=0, v=n;
    while(v>=1024 && i<u.length-1){v/=1024;i++}
    return (i ? v.toFixed(v>=10?0:1) : v) + ' ' + u[i];
  }

  async function loadFiles(path) {
    try {
      const q = path ? '&path=' + encodeURIComponent(path) : '';
      const data = await api('/api/files?' + q.slice(1));
      state.currentPath = data.path;
      state.files = data.items;
      $('#filesPathLabel').textContent = data.path;
      renderBreadcrumbs(data.path);
      renderFiles();
      $('#filesBack').onclick = () => data.parent ? loadFiles(data.parent) : go('home');
    } catch (e) {
      $('#fileList').innerHTML = '<div class="file-row"><div>⚠️</div><div><b>Ошибка</b><small>'+escapeHtml(e.message)+'</small></div></div>';
    }
  }

  function renderBreadcrumbs(path) {
    const root = path.match(/^[A-Za-z]:\\/) ? path.slice(0,3) : '';
    const rest = root ? path.slice(3).split('\\').filter(Boolean) : path.split(/[\\/]/).filter(Boolean);
    let current = root;
    const parts = [];
    if (root) {
      parts.push('<button class="crumb" data-path="'+escapeAttr(root)+'">'+escapeHtml(root.replace('\\',''))+'</button>');
      for (const part of rest) {
        current += (current.endsWith('\\') ? '' : '\\') + part;
        parts.push('<span>›</span><button class="crumb" data-path="'+escapeAttr(current)+'">'+escapeHtml(part)+'</button>');
      }
    } else parts.push('<span>'+escapeHtml(path)+'</span>');
    $('#breadcrumbs').innerHTML = parts.join('');
    $$('.crumb').forEach(b => b.onclick = () => loadFiles(b.dataset.path));
  }

  function renderFiles() {
    const term = $('#fileSearch').value.trim().toLowerCase();
    const rows = state.files.filter(x => !term || x.name.toLowerCase().includes(term)).map(x => {
      const icon = x.directory ? '📁' : fileIcon(x.name);
      return '<div class="file-row" data-path="'+escapeAttr(x.path)+'" data-dir="'+x.directory+'"><div class="file-icon">'+icon+'</div><div><b>'+escapeHtml(x.name)+'</b><small>'+new Date(x.modified).toLocaleDateString('ru-RU')+'</small></div><div class="size">'+(x.directory?'›':fmtSize(x.size))+'</div></div>';
    }).join('');
    $('#fileList').innerHTML = rows || '<div class="file-row"><div>∅</div><div><b>Пусто</b><small>Файлы не найдены</small></div></div>';
    $$('.file-row[data-path]').forEach(r => r.onclick = () => {
      if (r.dataset.dir === 'true') loadFiles(r.dataset.path);
      else window.open('/api/file?pin='+encodeURIComponent(state.pin)+'&path='+encodeURIComponent(r.dataset.path),'_blank');
    });
  }
  $('#fileSearch').addEventListener('input', renderFiles);

  async function loadRecent() {
    try {
      const data = await api('/api/recent');
      state.recent = data.items || [];
      renderRecent();
    } catch {}
  }

  function recentCard(f, gallery=false) {
    const url = '/api/file?pin='+encodeURIComponent(state.pin)+'&path='+encodeURIComponent(f.path);
    const preview = f.image ? '<img loading="lazy" src="'+url+'" alt="">' : '<div class="'+(gallery?'gallery-placeholder':'recent-placeholder')+'">'+(f.video?'▶':'📄')+'</div>';
    if (gallery) return '<button class="gallery-item panel" data-url="'+escapeAttr(url)+'">'+preview+'<div class="gallery-meta"><b>'+escapeHtml(f.name)+'</b><small>'+fmtSize(f.size)+'</small></div></button>';
    return '<div class="recent-card">'+preview+'<div class="meta"><b>'+escapeHtml(f.name)+'</b><small>'+fmtSize(f.size)+'</small></div></div>';
  }

  function renderRecent() {
    $('#homeRecent').innerHTML = state.recent.slice(0,8).map(f => recentCard(f,false)).join('') || '<div class="recent-card"><div class="recent-placeholder">⌁</div><div class="meta"><b>Нет файлов</b></div></div>';
    $('#galleryGrid').innerHTML = state.recent.map(f => recentCard(f,true)).join('') || '<div class="panel help-card">Недавние файлы не найдены.</div>';
    $$('.gallery-item[data-url]').forEach(b => b.onclick = () => window.open(b.dataset.url,'_blank'));
  }

  function fileIcon(name) {
    const e = name.split('.').pop().toLowerCase();
    if (['png','jpg','jpeg','webp','gif'].includes(e)) return '🖼️';
    if (['mp4','mov','mkv','avi'].includes(e)) return '🎬';
    if (['json','txt','log','md'].includes(e)) return '📄';
    return '📦';
  }

  function wsUrl(path) {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return proto + '//' + location.host + path + '?pin=' + encodeURIComponent(state.pin);
  }

  function startDesktop() {
    if (state.video || state.control) return;
    $('#desktopConnecting').style.display = 'grid';
    const v = new WebSocket(wsUrl('/ws/video'));
    const c = new WebSocket(wsUrl('/ws/control'));
    v.binaryType = 'arraybuffer';
    state.video = v; state.control = c;

    let vo=false,co=false;
    const ready=()=>{if(vo&&co)$('#desktopConnecting').style.display='none'};
    v.onopen=()=>{vo=true;ready()};
    c.onopen=()=>{co=true;ready()};
    v.onmessage=e=>queueFrame(e.data);
    const closed=()=>{ if ($('#pageDesktop').classList.contains('active')) { $('#desktopConnecting').textContent='Переподключение…'; $('#desktopConnecting').style.display='grid'; setTimeout(()=>{stopDesktop();startDesktop()},900); } };
    v.onclose=closed; c.onclose=closed; v.onerror=()=>{}; c.onerror=()=>{};
    updateModeButton();
  }

  function stopDesktop() {
    try{state.video?.close()}catch{}
    try{state.control?.close()}catch{}
    state.video=null; state.control=null; state.pointers.clear();
  }

  async function queueFrame(buffer) {
    state.pendingFrame = buffer;
    if (state.decoding) return;
    state.decoding = true;
    while (state.pendingFrame) {
      const data = state.pendingFrame;
      state.pendingFrame = null;
      try {
        const blob = new Blob([data],{type:'image/jpeg'});
        const bmp = await createImageBitmap(blob);
        state.frameSize={w:bmp.width,h:bmp.height};
        drawBitmap(bmp);
        bmp.close();
      } catch {}
    }
    state.decoding = false;
  }

  function drawBitmap(bmp) {
    const canvas=$('#desktopCanvas'), box=$('#desktopStage').getBoundingClientRect(), dpr=Math.min(2,devicePixelRatio||1);
    const w=Math.max(1,Math.round(box.width*dpr)), h=Math.max(1,Math.round(box.height*dpr));
    if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h}
    const ctx=canvas.getContext('2d',{alpha:false});
    ctx.fillStyle='#000';ctx.fillRect(0,0,w,h);
    const s=Math.min(w/bmp.width,h/bmp.height),dw=bmp.width*s,dh=bmp.height*s,x=(w-dw)/2,y=(h-dh)/2;
    ctx.drawImage(bmp,x,y,dw,dh);
  }

  function send(o) {
    if (state.control && state.control.readyState===1) state.control.send(JSON.stringify(o));
  }

  const hit=$('#desktopHit');
  hit.addEventListener('contextmenu',e=>e.preventDefault());
  hit.addEventListener('pointerdown',e=>{
    try{hit.setPointerCapture(e.pointerId)}catch{}
    state.pointers.set(e.pointerId,{x:e.clientX,y:e.clientY,sx:e.clientX,sy:e.clientY,at:performance.now(),moved:false});
    if(!state.mouseMode){const p=normalPoint(e.clientX,e.clientY);send({t:'touch',a:'down',id:e.pointerId%32,x:p.x,y:p.y})}
  });
  hit.addEventListener('pointermove',e=>{
    const p=state.pointers.get(e.pointerId);if(!p)return;
    const dx=e.clientX-p.x,dy=e.clientY-p.y;p.x=e.clientX;p.y=e.clientY;
    if(Math.hypot(e.clientX-p.sx,e.clientY-p.sy)>5)p.moved=true;
    if(state.mouseMode){
      if(state.pointers.size>=2){const d=Math.round(-dy*7);if(d)send({t:'wheel',d})}
      else{const r=hit.getBoundingClientRect();send({t:'mousemove',dx:dx/Math.max(1,r.width),dy:dy/Math.max(1,r.height)})}
    }else{const n=normalPoint(e.clientX,e.clientY);send({t:'touch',a:'move',id:e.pointerId%32,x:n.x,y:n.y})}
  });
  function pointerEnd(e,cancel=false){
    const p=state.pointers.get(e.pointerId);if(!p)return;
    if(state.mouseMode){
      if(state.pointers.size===1&&!p.moved&&performance.now()-p.at<330)send({t:'button',b:0,a:2});
    }else{const n=normalPoint(e.clientX,e.clientY);send({t:'touch',a:cancel?'cancel':'up',id:e.pointerId%32,x:n.x,y:n.y})}
    state.pointers.delete(e.pointerId);
  }
  hit.addEventListener('pointerup',e=>pointerEnd(e,false));
  hit.addEventListener('pointercancel',e=>pointerEnd(e,true));

  function normalPoint(x,y){
    const r=hit.getBoundingClientRect(),iw=state.frameSize.w,ih=state.frameSize.h,s=Math.min(r.width/iw,r.height/ih),dw=iw*s,dh=ih*s,l=(r.width-dw)/2,t=(r.height-dh)/2;
    return{x:Math.max(0,Math.min(1,(x-r.left-l)/Math.max(1,dw))),y:Math.max(0,Math.min(1,(y-r.top-t)/Math.max(1,dh)))};
  }

  $('#modeBtn').onclick=()=>{state.mouseMode=!state.mouseMode;localStorage.setItem('tdweb.mouse',state.mouseMode?'1':'0');state.pointers.clear();updateModeButton()};
  function updateModeButton(){const b=$('#modeBtn');b.classList.toggle('active',state.mouseMode);b.innerHTML=(state.mouseMode?'🖱<span>Мышь</span>':'☝️<span>Сенсор</span>')}

  let holdLeft=false;
  $('#lmbBtn').addEventListener('pointerdown',e=>{e.preventDefault();holdLeft=true;send({t:'button',b:0,a:0})});
  $('#lmbBtn').addEventListener('pointerup',e=>{e.preventDefault();if(holdLeft){holdLeft=false;send({t:'button',b:0,a:1})}});
  $('#lmbBtn').addEventListener('pointercancel',()=>{if(holdLeft){holdLeft=false;send({t:'button',b:0,a:1})}});
  $('#rmbBtn').onclick=()=>send({t:'button',b:1,a:2});

  const sink=$('#keyboardSink');
  $('#keyboardBtn').onclick=()=>sink.focus({preventScroll:true});
  sink.addEventListener('input',()=>{if(sink.value){send({t:'text',v:sink.value});sink.value=''}});
  sink.addEventListener('keydown',e=>{if(['Enter','Backspace','Escape','Tab','Delete','ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End','PageUp','PageDown'].includes(e.key)){send({t:'key',v:e.key});e.preventDefault()}});

  document.addEventListener('visibilitychange',()=>{if(document.hidden)stopDesktop();else if($('#pageDesktop').classList.contains('active'))startDesktop()});

  function escapeHtml(s){return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
  function escapeAttr(s){return escapeHtml(s).replace(/\n/g,' ')}

  if (state.pin.length===6) connect();
})();
