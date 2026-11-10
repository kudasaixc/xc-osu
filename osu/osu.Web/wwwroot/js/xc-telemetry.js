// Bounded, acknowledged telemetry outbox. One storage key per session avoids cross-tab overwrites.
const PREFIX = 'xc.tm.v1.';
const LOCAL_PREFIX = 'xc.tm.local.v1.';
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
  if (!options.telemetry) { for (const k of keys()) localStorage.removeItem(k); return; }
  const sid = crypto.randomUUID(), key = PREFIX + sid;
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
  let batch = { sid, meta, items: [] };
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
  let logCount = 0, logRepeats = 0;
  const logSeen = new Map();
  setInterval(() => { logCount = 0; }, 10000);
  const scrub = (s) => String(s)
    .replace(/https?:\/\/[^\s"')]+/g, (u) => { try { const x = new URL(u); return x.origin + x.pathname.replace(/\/\d+/g, '/#'); } catch { return '<url>'; } })
    .replace(/[\w.+-]+@[\w-]+\.[\w.-]+/g, '<email>')
    .replace(/\b(bearer|token|password|secret|authorization)\b\s*[:=]?\s*\S+/gi, '$1 <redacted>')
    .replace(/\b[A-Za-z0-9_-]{32,}\b/g, '<id>')
    .replace(/\s+/g, ' ').trim().slice(0, 300);
  log.on(line => {
    if (line.text.startsWith('[tm] ')) {
      try {
        const item = JSON.parse(line.text.slice(5));
        if (item.k === 'e' && item.n !== 'flight') remember(item.n, { from: item.from, to: item.to, scr: item.scr }, item.at);
        if (item.n === 'flight' && item.part === 0) {
          const events = context.filter(e => e.at >= item.at - 2000 && e.at <= item.at + 2000);
          // Separate chunks preserve the entire bounded context without exceeding per-item limits.
          for (let i = 0; i < events.length; i += 50)
            add({ k: 'e', n: 'flight-context', at: item.at, part: i / 50, parts: Math.ceil(events.length / 50), dropped: contextDropped, events: events.slice(i, i + 50) });
        }
        if (item.k === 'w') item.js = { hidden: +document.hidden, audio: globalThis.xcAudioDebug?.(), heap: performance.memory?.usedJSHeapSize };
        add(item);
      } catch { }
    } else if (line.level === 'error' || line.level === 'warn') {
      // A category alone cannot be acted upon: send the message itself, scrubbed (no addresses, tokens, e-mails or
      // long identifiers), truncated, and only once per distinct message (repeats are counted, see 'log-repeats').
      const text = scrub(line.text);
      if (!text) return;
      const seen = logSeen.get(text);
      if (seen !== undefined) { logSeen.set(text, seen + 1); logRepeats++; return; }
      if (logSeen.size >= 200 || logCount++ >= 20) return;
      logSeen.set(text, 1);
      event('log', { name: line.level, text, category: /webgl|graphics|render|shader|texture/i.test(text) ? 'graphics' : /audio|bass/i.test(text) ? 'audio' : /memory|heap|alloc/i.test(text) ? 'memory' : 'runtime' });
    }
  });
  setInterval(() => { if (logRepeats) { event('log-repeats', { count: logRepeats }); logRepeats = 0; } }, 30000);
  try {
    new PerformanceObserver(list => {
      for (const e of list.getEntries()) event('longtask', { ms: e.duration, start: e.startTime, hidden: +document.hidden });
    }).observe({ type: 'longtask', buffered: true });
  } catch { }
  try {
    new PerformanceObserver(list => {
      for (const e of list.getEntries()) {
        if (e.name.includes('/api/xc/tm')) continue;
        const end = performance.timeOrigin + e.responseEnd;
        remember('network', { phase: 'start', kind: e.initiatorType }, performance.timeOrigin + e.startTime);
        remember('network', { phase: 'end', kind: e.initiatorType, ms: e.duration, bytes: e.transferSize }, end);
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
  async function send() {
    if (busy) return;
    busy = true;
    try {
      for (const k of new Set([key, ...keys()])) {
        const raw = k === key ? batch : JSON.parse(localStorage.getItem(k) || 'null');
        const body = raw ? packet(raw) : null;
        if (!body || size(body) > MAX_BYTES) continue;
        const sent = JSON.parse(body);
        const response = await fetch('/api/xc/tm', { method: 'POST', body, credentials: 'omit', headers: { 'Content-Type': 'text/plain' }, signal: AbortSignal.timeout(10000) });
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
  globalThis.xcTelemetryCheckpoint = name => { event('boot-checkpoint', { name }); persist(); void send(); };
  function leaving() {
    event('lifecycle', { name: document.hidden ? 'hidden' : 'pagehide' });
    persist();
    persistArchive();
    try { navigator.sendBeacon('/api/xc/tm', new Blob([packet(batch)], { type: 'text/plain' })); } catch { }
    // Beacon acceptance is not a server acknowledgement; keep the outbox.
  }
  document.addEventListener('visibilitychange', () => { if (document.hidden) leaving(); else send(); });
  addEventListener('pagehide', leaving);
  addEventListener('online', send);
  event('start');
  setInterval(send, 2000);
  send();
}
