// Bounded, acknowledged telemetry outbox. One storage key per session avoids cross-tab overwrites.
const PREFIX = 'xc.tm.v1.';
const LOCAL_PREFIX = 'xc.tm.local.v1.';
const LIVE_PREFIX = 'xc.tm.live.v1.'; // one small marker per running session: a marker left without an end = unclean exit
const IID_KEY = 'xc.tm.iid';          // random installation id: links the sessions of one browser, nothing else
export function localSessions() {
  try {
    return Object.keys(localStorage).filter(k => k.startsWith(LOCAL_PREFIX)).flatMap(k => {
      try { const s = JSON.parse(localStorage.getItem(k)); return s && Array.isArray(s.items) ? [s] : []; } catch { return []; }
    }).sort((a, b) => b.started - a.started);
  } catch { return []; }
}
export function clearLocalSessions() {
  for (const s of localSessions()) { try { localStorage.removeItem(LOCAL_PREFIX + s.sid); } catch { } }
}

const MAX_BYTES = 48000; // below the browser's shared 64 KiB keepalive budget
const encode = (x) => JSON.stringify(x);
const size = (s) => new TextEncoder().encode(s).length;
export function startTelemetry(options, build, log) {
  const keys = () => { try { return Object.keys(localStorage).filter(k => k.startsWith(PREFIX)); } catch { return []; } };
  const liveKeys = () => { try { return Object.keys(localStorage).filter(k => k.startsWith(LIVE_PREFIX)); } catch { return []; } };
  if (!options.telemetry) {
    try { for (const k of [...keys(), ...liveKeys(), IID_KEY]) localStorage.removeItem(k); } catch { }
    return;
  }
  const sid = crypto.randomUUID(), key = PREFIX + sid;
  let iid;
  try {
    iid = localStorage.getItem(IID_KEY);
    if (!/^[0-9a-f-]{16,40}$/.test(iid || '')) { iid = crypto.randomUUID(); localStorage.setItem(IID_KEY, iid); }
  } catch { iid = undefined; }
  let q = 0, busy = false;
  const context = [];
  let contextDropped = 0;
  function remember(type, data = {}, at = Date.now()) {
    context.push({ at, type, ...data });
    while (context.length && context[0].at < Date.now() - 10000) context.shift();
    if (context.length > 1000) { context.shift(); contextDropped++; }
  }
  globalThis.xcTelemetryEvent = remember;
  function packet(value) {
    const result = { ...value, items: [] };
    for (const item of value.items) {
      result.items.push(item);
      if (size(encode(result)) > MAX_BYTES) { result.items.pop(); break; }
    }
    return encode(result);
  }
  const hw = { cores: navigator.hardwareConcurrency, ram: navigator.deviceMemory, browser: navigator.userAgent, os: navigator.platform, dpr: devicePixelRatio, width: screen.width, height: screen.height };
  try {
    const gl = document.createElement('canvas').getContext('webgl2', { powerPreference: options.gpu || 'high-performance' });
    const ext = gl?.getExtension('WEBGL_debug_renderer_info');
    hw.gpu = ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl?.getParameter(gl.RENDERER);
    gl?.getExtension('WEBGL_lose_context')?.loseContext();
  } catch { }
  const meta = { build, channel: location.pathname.startsWith('/dev') ? 'dev' : 'aot', hw,
    options: Object.fromEntries(Object.entries(options).filter(([k]) => k !== 'server')) };
  let batch = { sid, iid, meta, items: [] };
  const archive = { sid, meta, started: Date.now(), items: [], dropped: 0 };
  let archiveTimer = 0;
  function persistArchive() {
    clearTimeout(archiveTimer); archiveTimer = 0;
    try {
      while (archive.items.length && size(encode(archive)) > 256000) { archive.items.shift(); archive.dropped++; }
      localStorage.setItem(LOCAL_PREFIX + sid, encode(archive));
      for (const old of localSessions().slice(3)) localStorage.removeItem(LOCAL_PREFIX + old.sid);
    } catch { /* Local history must never interrupt the game when storage is unavailable. */ }
  }
  let persistTimer = 0;
  const persist = () => { clearTimeout(persistTimer); persistTimer = 0; try { localStorage.setItem(key, encode(batch)); } catch { } };
  function add(item) {
    item.q = ++q; item.at ||= Date.now();
    if (size(encode(item)) > 22000) return;
    batch.items.push(item);
    archive.items.push(item);
    if (archive.items.length > 1500) { archive.items.shift(); archive.dropped++; }
    if (!archiveTimer) archiveTimer = setTimeout(persistArchive, 1000);
    while (size(encode(batch)) > 256000) { batch.items.shift(); meta.outboxDropped = (meta.outboxDropped || 0) + 1; }
    if (!persistTimer) persistTimer = setTimeout(persist, 100);
  }
  function event(n, data = {}) { remember(n, data); add({ k: 'e', n, ...data }); }
  // ------------------------------------------------------------------ errors, warnings and crashes
  const scrubLine = (s) => String(s)
    .replace(/https?:\/\/[^\s"')]+/g, (u) => { try { const x = new URL(u); return x.origin + x.pathname.replace(/\/\d+/g, '/#'); } catch { return '<url>'; } })
    .replace(/[\w.+-]+@[\w-]+\.[\w.-]+/g, '<email>')
    .replace(/\beyJ[\w-]{8,}\.[\w-]+(\.[\w-]+)?/g, '<jwt>')
    .replace(/\b(bearer|token|password|secret|authorization)\b\s*[:=]?\s*\S+/gi, '$1 <redacted>')
    // long random identifiers only: method and symbol names (underscores, dots, no digit) must stay readable
    .replace(/\b(?=[A-Za-z-]*\d)[A-Za-z0-9-]{32,}\b/g, '<id>')
    .replace(/[ \t]+/g, ' ').trim();
  const scrub = (s, max = 300) => scrubLine(String(s).replace(/\s+/g, ' ')).slice(0, max);
  const scrubLines = (s, maxLines, maxChars) => {
    const out = []; let chars = 0;
    for (const part of String(s).split('\n')) {
      const line = scrubLine(part).slice(0, 300);
      if (!line) continue;
      if (out.length >= maxLines || chars + line.length > maxChars) break;
      out.push(line); chars += line.length + 1;
    }
    return out.join('\n');
  };
  const FATAL = /thread handle : 0x|\* Assertion|RuntimeError|Aborted\(|\babort\(|signature mismatch|out of bounds|unreachable|out of memory|\[launch\] xc-osu! crashed|runtime start failed|uncaught: |SIGSEGV|SIGABRT|Unhandled exception|mono_wasm_.*fail/i;
  const categorize = (text) => FATAL.test(text) ? 'crash' : /webgl|shader|texture|framebuffer|\bGL_[A-Z]/i.test(text) ? 'graphics'
    : /audio|\bbass\b/i.test(text) ? 'audio' : /memory|heap|alloc/i.test(text) ? 'memory' : 'runtime';

  // Where the player is, kept from the engine's own screen / overlay events.
  const where = { scr: 'boot', ov: '' };
  const started = Date.now();

  // --- live marker (unclean exit detection)
  const liveKey = LIVE_PREFIX + sid;
  const live = { sid, started, at: started, build, scr: where.scr, ov: where.ov, hidden: +document.hidden };
  function saveLive() {
    live.at = Date.now(); live.scr = where.scr; live.ov = where.ov; live.hidden = +document.hidden;
    try { localStorage.setItem(liveKey, encode(live)); } catch { }
  }
  // Sessions that never said goodbye: frozen page, killed tab, browser or GPU process crash, power loss.
  // A marker still refreshed by another open tab (every 5 s) is left alone.
  const previous = [];
  for (const k of liveKeys()) {
    try {
      const old = JSON.parse(localStorage.getItem(k));
      if (old && !old.end && Date.now() - old.at < 20000) continue;
      localStorage.removeItem(k);
      if (old && !old.end && Date.now() - old.at < 7 * 86400000) previous.push(old);
    } catch { try { localStorage.removeItem(k); } catch { } }
  }

  // --- log blocks: consecutive error / warning lines (a stack trace, a thread dump) travel as ONE event.
  // The block is appended to in place so that a page dying right after still has it in the outbox; it is closed by
  // 300 ms of silence, its size limit, or a send (what was sent is never modified afterwards).
  const BLOCK_LINES = 80, BLOCK_CHARS = 9000, BLOCK_GAP = 300;
  let block = null, suppressing = false, lastLineAt = 0;
  let logCount = 0, logRepeats = 0;
  const logSeen = new Map();
  setInterval(() => { logCount = 0; }, 10000);
  function closeBlock() { block = null; }
  // A fatal line may be the last thing this page ever does: store the outbox at once (a page that freezes now still
  // delivers it at the next launch) and hand it to the browser a moment later, once the rest of the burst is in.
  let urgentTimer = 0;
  function urgent(now = false) {
    persist();
    if (now) { clearTimeout(urgentTimer); urgentTimer = 0; beacon(); }
    else if (!urgentTimer) urgentTimer = setTimeout(() => { urgentTimer = 0; persist(); beacon(); }, 60);
  }
  function onLogLine(line) {
    const text = scrub(line.text);
    if (!text) return;
    const now = Date.now(), fatal = FATAL.test(text);
    const continues = now - lastLineAt <= BLOCK_GAP;
    lastLineAt = now;
    if (continues && suppressing && !fatal) return;
    if (continues && block && block.lines < BLOCK_LINES && block.text.length + text.length < BLOCK_CHARS) {
      block.text += '\n' + text; block.lines++;
      if (line.level === 'error') block.name = 'error';
      if (fatal && block.category !== 'crash') block.category = 'crash';
    } else {
      // repeats of a known message are counted (see 'log-repeats') after the third copy; crashes always go through
      const seen = logSeen.get(text) || 0;
      suppressing = !fatal && (seen >= 3 || (!seen && logSeen.size >= 200) || logCount >= 20);
      if (logSeen.has(text) || logSeen.size < 200) logSeen.set(text, seen + 1);
      if (suppressing) { logRepeats++; block = null; return; }
      logCount++;
      const cont = continues && block ? 1 : undefined; // same burst, previous block full or already sent
      block = { k: 'e', n: 'log', name: line.level, category: categorize(text), scr: where.scr, ov: where.ov, lines: 1, cont, text };
      remember('log', { name: line.level, category: block.category });
      add(block);
    }
    if (fatal) {
      if (!live.fatal) { live.fatal = text.slice(0, 200); saveLive(); }
      urgent();
    } else if (!persistTimer) persistTimer = setTimeout(persist, 100);
  }
  // Window histograms remain exhaustive. Detailed diagnostics are sampled separately.
  let nextCapture = 0, acceptedCapture = null;
  let sampledSpikes = 0, sampledCaptures = 0, engineDropped = 0;
  setInterval(() => {
    if (sampledSpikes || sampledCaptures || engineDropped) {
      event('diagnostics-sampled', { spikes: sampledSpikes, captures: sampledCaptures, engineDropped });
      sampledSpikes = sampledCaptures = engineDropped = 0;
    }
  }, 30000);
  log.on(line => {
    if (line.text.startsWith('[tm] ')) {
      try {
        const item = JSON.parse(line.text.slice(5));
        if (item.k === 's') {
          const all = Array.isArray(item.sp) ? item.sp : [];
          item.sp = document.hidden ? [] : all.filter(s => s.ms >= 33)
            .sort((a, b) => b.ms - a.ms).slice(0, 5);
          sampledSpikes += all.length - item.sp.length;
          engineDropped += Number(item.spx) || 0;
          delete item.spx;
          if (!item.sp.length) return;
        }
        if (item.n === 'flight') {
          if (item.part === 0) {
            const now = performance.now();
            acceptedCapture = !document.hidden && now >= nextCapture ? item.at : null;
            if (acceptedCapture !== null) nextCapture = now + 60000;
            else sampledCaptures++;
          }
          // Decide once per capture so accepted captures retain every fragment.
          if (acceptedCapture === null || item.at !== acceptedCapture) return;
        }
        if (item.k === 'e' && item.n === 'screen' && item.to) { where.scr = String(item.to); saveLive(); }
        if (item.k === 'e' && item.n === 'overlay') { where.ov = String(item.to || ''); saveLive(); }
        if (item.k === 'e' && item.n !== 'flight') remember(item.n, { from: item.from, to: item.to, scr: item.scr }, item.at);
        if (item.n === 'flight' && item.part === 0) {
          const events = context.filter(e => e.at >= item.at - 2000 && e.at <= item.at + 2000);
          // Separate chunks preserve the entire bounded context without exceeding per-item limits.
          for (let i = 0; i < events.length; i += 50)
            add({ k: 'e', n: 'flight-context', at: item.at, part: i / 50, parts: Math.ceil(events.length / 50), dropped: contextDropped, events: events.slice(i, i + 50) });
        }
        if (item.k === 'w') item.js = { hidden: +document.hidden, audio: globalThis.xcAudioDebug?.(), heap: performance.memory?.usedJSHeapSize, wasm: globalThis.xcWasmBytes?.() };
        add(item);
      } catch { }
    } else if (line.level === 'error' || line.level === 'warn') {
      try { onLogLine(line); } catch { }
    }
  });
  setInterval(() => { if (logRepeats) { event('log-repeats', { count: logRepeats }); logRepeats = 0; } }, 30000);

  // --- explicit crash report: the engine exited with an error, or the page caught something fatal.
  // Carries what a stack alone does not: exit code, message, where the player was, and the last console lines
  // (all levels, scrubbed) since the reason is often printed just before the stack.
  let crashes = 0;
  globalThis.xcTelemetryCrash = (name, info = {}) => {
    try {
      if (crashes++ >= 5) return;
      let tail;
      try { tail = scrubLines((log.lines || []).slice(-60).map(l => (l.level === 'log' ? '' : l.level + ': ') + l.text).join('\n'), 60, 7000); } catch { }
      closeBlock();
      add({ k: 'e', n: 'crash', name: String(name), code: Number.isFinite(info.code) ? info.code : undefined,
        text: info.text ? scrubLines(info.text, 80, 8000) : undefined, tail,
        scr: where.scr, ov: where.ov, up: Math.round((Date.now() - started) / 1000), hidden: +document.hidden,
        heap: performance.memory?.usedJSHeapSize, booted: meta.boot ? 1 : 0 });
      live.end = 'crash'; saveLive();
      urgent(true);
      void send();
    } catch { }
  };
  try {
    new PerformanceObserver(list => {
      for (const e of list.getEntries()) event('longtask', { ms: e.duration, start: e.startTime, hidden: +document.hidden });
    }).observe({ type: 'longtask', buffered: true });
  } catch { }
  try {
    new PerformanceObserver(list => {
      for (const e of list.getEntries()) {
        if (e.name.includes('/api/xc/tm')) continue;
        // duration remains consistent for cached / restricted resource timing entries.
        const duration = Math.max(0, e.duration);
        const end = performance.timeOrigin + e.startTime + duration;
        remember('network', { phase: 'start', kind: e.initiatorType }, performance.timeOrigin + e.startTime);
        remember('network', { phase: 'end', kind: e.initiatorType, ms: duration, bytes: e.transferSize }, end);
      }
    }).observe({ type: 'resource', buffered: true });
  } catch { }
  document.addEventListener('webglcontextlost', () => event('context-lost'), true);
  document.addEventListener('webglcontextrestored', () => event('context-restored'), true);
  addEventListener('xc-boot-complete', e => { meta.boot = { ms: e.detail }; event('boot', { ms: e.detail }); });
  const cutoff = Date.now() - 7 * 86400000;
  for (const k of keys()) {
    try { const old = JSON.parse(localStorage.getItem(k)); if (!old.items?.length || old.items.at(-1).at < cutoff) localStorage.removeItem(k); } catch { localStorage.removeItem(k); }
  }
  // Bound disk usage across abandoned sessions.
  for (const k of keys().slice(0, -8)) localStorage.removeItem(k);
  let sessionUntil = 0;
  async function send() {
    if (busy) return;
    busy = true;
    closeBlock();
    try {
      if (Date.now() >= sessionUntil) {
        const session = await fetch('/api/xc/tm/session', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(10000) });
        if (!session.ok) return;
        sessionUntil = Date.now() + 50 * 60000;
      }
      for (const k of new Set([key, ...keys()])) {
        const raw = k === key ? batch : JSON.parse(localStorage.getItem(k) || 'null');
        const body = raw ? packet(raw) : null;
        if (!body || size(body) > MAX_BYTES) continue;
        const sent = JSON.parse(body);
        const response = await fetch('/api/xc/tm', { method: 'POST', body, credentials: 'same-origin', headers: { 'Content-Type': 'text/plain' }, signal: AbortSignal.timeout(10000) });
        if (response.status === 401) sessionUntil = 0;
        if (!response.ok) break;
        const { seq } = await response.json();
        if (!Number.isSafeInteger(seq)) break;
        if (k === key) { batch.items = batch.items.filter(i => i.q > seq); persist(); }
        else {
          const latest = JSON.parse(localStorage.getItem(k) || 'null');
          if (!latest) continue;
          latest.items = latest.items.filter(i => i.q > seq);
          if (latest.items.length) localStorage.setItem(k, encode(latest)); else localStorage.removeItem(k);
        }
      }
    } catch { /* Retain until acknowledged; retry on next tick or next launch. */ }
    finally { busy = false; }
  }
  // Beacon acceptance is not a server acknowledgement; keep the outbox (the server ignores what it already stored).
  function beacon() {
    closeBlock();
    try { navigator.sendBeacon('/api/xc/tm', new Blob([packet(batch)], { type: 'text/plain' })); } catch { }
  }
  globalThis.xcTelemetryCheckpoint = name => { event('boot-checkpoint', { name }); persist(); void send(); };
  function leaving(e) {
    const closing = e?.type === 'pagehide';
    event('lifecycle', { name: closing ? 'pagehide' : 'hidden' });
    if (closing && !live.end) live.end = 'pagehide';
    saveLive();
    persist();
    persistArchive();
    beacon();
  }
  document.addEventListener('visibilitychange', () => { if (document.hidden) leaving(); else { saveLive(); send(); } });
  addEventListener('pagehide', leaving);
  // back from the back/forward cache: the session goes on
  addEventListener('pageshow', (e) => { if (e.persisted && live.end === 'pagehide') { delete live.end; saveLive(); } });
  addEventListener('online', send);
  saveLive();
  setInterval(saveLive, 5000);
  event('start');
  for (const old of previous.slice(-4)) {
    event('unclean-exit', { name: old.scr || 'unknown', ov: old.ov || '', prev: old.sid, build: old.build,
      up: Math.round((old.at - old.started) / 1000), ago: Math.round((Date.now() - old.at) / 1000),
      hidden: old.hidden ? 1 : 0, fatal: old.fatal });
  }
  setInterval(send, 2000);
  send();
}
