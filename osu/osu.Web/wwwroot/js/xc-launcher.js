// xc-osu! launcher — xc softworks (kudasaixc)
// Game-launcher layout: icon rail (Play / News / Status / Options), one tab at a time, bottom bar with download + Play.
// The background is a canvas of approach circles closing onto hit circles to a slow beat.
// Each option maps to a real mechanism (runtime config, environment variable read by the engine, WebGL context
// attribute, AudioContext option, or canvas resolution); see buildLaunch().

const KEY = 'xc.launcher.v1';
const cores = navigator.hardwareConcurrency || 4;

const range = (key, sec, min, max, step, def, unit, extra = {}) => ({ key, sec, type: 'range', min, max, step, def, unit, ...extra });
const select = (key, sec, def, choices, extra = {}) => ({ key, sec, type: 'select', def, choices, ...extra });
const toggle = (key, sec, def, extra = {}) => ({ key, sec, type: 'toggle', def, ...extra });

export const OPTIONS = [
  select('frameSync', 'perf', 'game', ['game', 'VSync', 'Limit2x', 'Limit4x', 'Limit8x', 'Unlimited'], { label: (v, t) => v === 'game' ? t('opt.game') : t('fs.' + v) }),
  range('maxDrawHz', 'perf', 0, 1000, 10, 0, 'fps', { zero: 'off' }),
  range('maxUpdateHz', 'perf', 0, 2000, 50, 0, 'Hz', { zero: 'off' }),
  range('inactiveHz', 'perf', 0, 240, 5, 0, 'Hz', { zero: 'default' }),
  range('renderScale', 'perf', 25, 200, 5, 100, '%'),

  range('workers', 'cpu', 4, 32, 1, 12, ''),
  range('cpuCount', 'cpu', 0, Math.max(cores, 2), 1, 0, '', { zero: 'auto' }),
  range('threadPoolMax', 'cpu', 0, 64, 1, 0, '', { zero: 'default' }),

  select('softHeap', 'mem', 0, [0, 256, 384, 512, 768, 1024, 1536, 2048, 3072], { num: true, label: (v, t) => v ? mb(v) : t('opt.off') }),
  select('hardHeap', 'mem', 0, [0, 512, 768, 1024, 1536, 2048, 3072, 4096], { num: true, label: (v, t) => v ? mb(v) : t('opt.off') }),
  select('nursery', 'mem', 0, [0, 1, 2, 4, 8, 16, 32, 64], { num: true, label: (v, t) => v ? v + ' MB' : t('opt.default') }),

  select('gpu', 'gfx', 'high-performance', ['high-performance', 'default', 'low-power'], { label: (v, t) => t('gpu.' + v) }),
  toggle('lowLatency', 'gfx', true),
  toggle('msaa', 'gfx', false),
  toggle('fullscreen', 'gfx', true),

  select('audioLatency', 'audio', 'interactive', ['interactive', 'balanced', 'playback'], { label: (v, t) => t('al.' + v) }),

  select('log', 'diag', 'boot', ['boot', 'verbose', 'quiet'], { label: (v, t) => t('log.' + v) }),
  toggle('realmTrace', 'diag', false),
  toggle('perfOverlay', 'diag', false),
  select('perfPos', 'diag', 'tr', ['tr', 'tl', 'br', 'bl'], { label: (v, t) => t('pos.' + v) }),
  select('perfMs', 'diag', 1000, [500, 1000, 2000, 5000], { num: true, label: (v) => v >= 1000 ? v / 1000 + ' s' : v + ' ms' }),
  toggle('diag', 'diag', false),

  { key: 'server', sec: 'net', type: 'text', def: '', placeholder: () => location.origin },

  toggle('startGate', 'launcher', true),
  toggle('autoLaunch', 'launcher', false),
  select('lang', 'launcher', 'en', ['en', 'fr'], { label: (v) => v === 'fr' ? 'Français' : 'English' }),
];
const SECTIONS = ['perf', 'cpu', 'mem', 'gfx', 'audio', 'diag', 'net', 'launcher'];
const NOT_PRESET = new Set(['server', 'autoLaunch', 'lang', 'perfPos', 'startGate', 'fullscreen']);

export const PRESETS = {
  balanced: {},
  competitive: { frameSync: 'Unlimited', lowLatency: true, gpu: 'high-performance', audioLatency: 'interactive', renderScale: 100, msaa: false },
  battery: { frameSync: 'VSync', maxDrawHz: 60, maxUpdateHz: 240, inactiveHz: 10, gpu: 'low-power', renderScale: 75, workers: 8, softHeap: 768, audioLatency: 'balanced', lowLatency: false },
  potato: { frameSync: 'VSync', maxDrawHz: 60, maxUpdateHz: 500, inactiveHz: 15, renderScale: 50, workers: 8, softHeap: 512, nursery: 4, audioLatency: 'balanced' },
  debug: { log: 'verbose', realmTrace: true, perfOverlay: true, perfMs: 500, diag: true },
};

function mb(v) { return v >= 1024 ? (v / 1024).toFixed(v % 1024 ? 1 : 0) + ' GB' : v + ' MB'; }
export const fmtBytes = (b) => b >= 1073741824 ? (b / 1073741824).toFixed(2) + ' GB' : b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.round(b / 1024) + ' KB';

export function defaults() { return Object.fromEntries(OPTIONS.map((o) => [o.key, o.def])); }

export function loadOptions() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { }
  const o = { ...defaults(), ...saved };
  if (o.lang !== 'fr') o.lang = 'en'; // the launcher is English unless French was picked explicitly
  // legacy URL switches still work (not saved)
  const q = new URLSearchParams(location.search);
  if (q.has('log')) { o.log = 'verbose'; o.realmTrace = true; }
  if (q.has('perf')) o.perfOverlay = true;
  if (q.has('diag')) o.diag = true;
  return o;
}
export function saveOptions(o) { try { localStorage.setItem(KEY, JSON.stringify(o)); } catch { } }

/** Turns launcher options into what the boot actually consumes. */
export function buildLaunch(o) {
  const env = { XC_ORIGIN: (o.server || location.origin).replace(/\/+$/, '') };
  if (o.frameSync !== 'game') env.XC_FRAMESYNC = o.frameSync;
  if (o.maxDrawHz > 0) env.XC_MAX_DRAW_HZ = String(o.maxDrawHz);
  if (o.maxUpdateHz > 0) env.XC_MAX_UPDATE_HZ = String(o.maxUpdateHz);
  if (o.inactiveHz > 0) env.XC_INACTIVE_HZ = String(o.inactiveHz);
  if (o.cpuCount > 0) env.DOTNET_PROCESSOR_COUNT = String(o.cpuCount);
  if (o.threadPoolMax > 0) env.XC_THREADPOOL_MAX = String(o.threadPoolMax);
  const gc = [];
  if (o.softHeap > 0) gc.push(`soft-heap-limit=${o.softHeap}m`);
  if (o.hardHeap > 0) gc.push(`max-heap-size=${o.hardHeap}m`);
  if (o.nursery > 0) gc.push(`nursery-size=${o.nursery}m`);
  if (gc.length) env.MONO_GC_PARAMS = gc.join(',');
  env.XC_LOG = o.log;
  if (o.realmTrace) env.XC_REALM_TRACE = '1';
  if (o.perfOverlay) { env.XC_PERF = '1'; env.XC_PERF_MS = String(o.perfMs); }
  if (o.diag) env.XC_DIAG = '1';
  if (o.startGate) env.XC_START_GATE = '1';
  return {
    env,
    workers: o.workers,
    gl: { powerPreference: o.gpu, desynchronized: !!o.lowLatency, antialias: !!o.msaa },
    renderScale: o.renderScale / 100,
    audioLatency: o.audioLatency,
    fullscreen: !!o.fullscreen,
    startGate: !!o.startGate,
    perfOverlay: !!o.perfOverlay,
    perfPos: o.perfPos,
  };
}

function commandLine(o) {
  const d = defaults(), parts = ['xc-osu'];
  for (const opt of OPTIONS) {
    if (['autoLaunch', 'lang', 'perfPos'].includes(opt.key)) continue;
    if (o[opt.key] === d[opt.key] || (opt.key === 'perfMs' && !o.perfOverlay)) continue;
    const flag = '--' + opt.key.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase());
    parts.push(opt.type === 'toggle' ? (o[opt.key] ? flag : '--no-' + flag.slice(2)) : `${flag}=${/\s/.test(o[opt.key]) ? JSON.stringify(o[opt.key]) : o[opt.key]}`);
  }
  return parts.join(' ');
}

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const tr = (v, lang) => (v && typeof v === 'object') ? (v[lang] ?? v.en ?? '') : (v ?? '');

export class Launcher {
  constructor(root, t, { options, build, blocked, onLangChange }) {
    this.root = root; this.t = t; this.o = options; this.build = build; this.blocked = blocked; this.onLangChange = onLangChange;
    this.tab = this.tab || 'play';
    this.render();
    this.loadNews();
    this.loadBuild();
    this.systemInfo();
    root.classList.add('show');
  }

  waitForLaunch() { return new Promise((res) => { this.resolve = res; }); }

  render() {
    const t = this.t;
    const icon = {
      play: '<path d="M8 5.5v13l11-6.5z"/>',
      news: '<path d="M5 5h14v14H5z M8 9h8 M8 12h8 M8 15h5" fill="none"/>',
      status: '<circle cx="12" cy="12" r="7.5" fill="none"/><path d="M12 12V4.5A7.5 7.5 0 0 1 19.5 12z"/>',
      options: '<path d="M4 7h10 M18 7h2 M4 17h2 M10 17h10" fill="none"/><circle cx="16" cy="7" r="2" fill="none"/><circle cx="8" cy="17" r="2" fill="none"/>',
    };
    const nav = ['play', 'news', 'status', 'options'].map((k) => `
      <button type="button" class="rail-btn" role="tab" id="tab-${k}" aria-controls="pane-${k}" data-tab="${k}">
        <svg viewBox="0 0 24 24" aria-hidden="true" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" fill="currentColor">${icon[k]}</svg>
        <span>${t('nav.' + k)}</span></button>`).join('');
    this.root.innerHTML = `
      <div class="ln-art" id="ln-art" aria-hidden="true"></div>
      <canvas class="ln-bg" id="ln-bg" aria-hidden="true"></canvas>
      <nav class="rail" role="tablist" aria-orientation="vertical">
        <div class="rail-logo" title="xc-osu!">xc</div>
        ${nav}
      </nav>
      <main class="stage">
        <section class="pane" id="pane-play" role="tabpanel" aria-labelledby="tab-play">
          <header class="pane-head">
            <h1 class="title"><span id="ln-ver">osu!lazer</span><small id="ln-ahead"></small></h1>
            <p class="lede">${t('play.lede')}</p>
            <p class="hello" id="ln-hello">${this.hello()}</p>
          </header>
          <div class="grid-2">
            <div class="panel"><h2>${t('spec.title')}</h2><dl class="spec" id="ln-spec"></dl></div>
            <div class="stack">
              <div class="panel"><h2>${t('play.latest')}</h2><div id="ln-latest"><p class="muted">${t('news.loading')}</p></div></div>
              <div class="panel"><h2>${t('issues.title')}</h2><ul class="issue-mini" id="ln-issues-mini"></ul>
                <button type="button" class="link" data-goto="status">${t('play.allissues')}</button></div>
            </div>
          </div>
        </section>

        <section class="pane" id="pane-news" role="tabpanel" aria-labelledby="tab-news">
          <header class="pane-head"><h1 class="title">${t('news.title')}</h1></header>
          <div id="ln-posts" class="posts"><p class="muted">${t('news.loading')}</p></div>
        </section>

        <section class="pane" id="pane-status" role="tabpanel" aria-labelledby="tab-status">
          <header class="pane-head"><h1 class="title">${t('nav.status')}</h1></header>
          <div class="grid-2">
            <div class="panel"><h2>${t('port.title')}</h2><div id="ln-gauge" class="meter"></div><table class="port" id="ln-port"></table></div>
            <div class="stack">
              <div class="panel"><h2>${t('issues.title')} <span id="ln-asof" class="muted"></span></h2><ul id="ln-issues" class="issues"></ul></div>
              <div class="panel"><h2>${t('build.title')}</h2><div id="ln-build" class="build"><p class="muted">${t('news.loading')}</p></div></div>
              <div class="panel"><h2>${t('sys.title')}</h2><dl id="ln-sys" class="sys"></dl></div>
            </div>
          </div>
        </section>

        <section class="pane" id="pane-options" role="tabpanel" aria-labelledby="tab-options">
          <header class="pane-head row">
            <h1 class="title">${t('nav.options')}</h1>
            <button type="button" class="btn" id="ln-reset">${t('opt.reset')}</button>
          </header>
          <div class="presets"><span class="muted">${t('presets')}</span><div id="ln-presets" class="chips" role="group"></div></div>
          <div id="ln-opts" class="opt-grid"></div>
          <div class="panel cmd"><h2>${t('cmd')}</h2><code id="ln-cmd"></code></div>
        </section>
      </main>
      <footer class="dock">
        <div class="dock-bar"><i id="ln-dl-bar"></i></div>
        <div class="dock-dl" id="ln-dl">
          <strong id="ln-dl-state">${this.blocked ? t('launch.blocked') : t('dl.idle')}</strong>
          <span id="ln-dl-files"></span><span id="ln-dl-size"></span><span id="ln-dl-speed"></span>
          ${this.blocked ? `<span class="bad">${esc(t('launch.noisolation'))}</span>` : ''}
        </div>
        <div class="dock-right">
          <div class="dock-meta"><span id="ln-dock-preset"></span><span class="muted" id="ln-play-sub">${t('launch.sub', esc(this.build))}</span></div>
          <button type="button" id="ln-play" class="play"${this.blocked ? ' disabled' : ''}><span id="ln-play-label">${this.blocked ? t('launch.blocked') : t('launch.play')}</span></button>
        </div>
      </footer>`;
    this.renderOptions();
    this.root.querySelectorAll('[data-tab]').forEach((b) => b.onclick = () => this.show(b.dataset.tab));
    this.root.querySelectorAll('[data-goto]').forEach((b) => b.onclick = () => this.show(b.dataset.goto));
    this.root.querySelector('.rail').onkeydown = (e) => {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      const tabs = ['play', 'news', 'status', 'options'], i = tabs.indexOf(this.tab);
      this.show(tabs[(i + (e.key === 'ArrowDown' ? 1 : 3)) % 4], true); e.preventDefault();
    };
    this.root.querySelector('#ln-play').onclick = () => this.launch();
    this.root.querySelector('#ln-reset').onclick = () => { const keep = { lang: this.o.lang }; this.o = { ...defaults(), ...keep }; this.changed(true); };
    this.show(this.tab);
    this.startBackground();
    this.loadArt();
  }

  hello() {
    let last = null;
    try { last = JSON.parse(localStorage.getItem('xc.lastBoot') || 'null'); } catch { }
    return last ? this.t('hello.back', (last.ms / 1000).toLocaleString(this.t.lang, { maximumFractionDigits: 1 })) : this.t('hello.first');
  }

  // one of the game's own menu backgrounds (the site's seasonal set), different each visit
  async loadArt() {
    try {
      const r = await fetch('/api/v2/seasonal-backgrounds', { cache: 'no-store' });
      if (!r.ok) return;
      const list = (await r.json()).backgrounds || [];
      if (!list.length) return;
      const src = new URL(list[Math.floor(Math.random() * list.length)].url).pathname; // same origin, so COEP lets it through
      const img = new Image();
      img.onload = () => { const el = this.root.querySelector('#ln-art'); if (!el) return; el.style.backgroundImage = `url("${src}")`; el.classList.add('on'); };
      img.src = src;
    } catch { }
  }

  show(tab, focus) {
    this.tab = tab;
    this.root.querySelectorAll('[data-tab]').forEach((b) => {
      const on = b.dataset.tab === tab;
      b.classList.toggle('on', on); b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1;
      if (on && focus) b.focus();
    });
    this.root.querySelectorAll('.pane').forEach((p) => p.hidden = p.id !== 'pane-' + tab);
    this.root.querySelector('.stage').scrollTop = 0;
  }

  // A tiny playfield in the empty band beside the Play title: approach circles close onto numbered hit circles
  // on a slow beat, and you can click them for a judgement. The xc logo pulses on the same beat.
  startBackground() {
    const cv = this.root.querySelector('#ln-bg'), ctx = cv.getContext('2d'), logo = this.root.querySelector('.rail-logo');
    const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const BEAT = 60000 / 92, APPROACH = 1100, FADE = 260, R = 30;
    const JUDGE = [[80, '300', '#66ccff'], [140, '100', '#88dd22'], [200, '50', '#ffcc22']];
    const notes = [], pops = [];
    let combo = 0, next = performance.now(), last = null, t0 = next;
    const size = () => { const d = Math.min(devicePixelRatio || 1, 2); cv.width = Math.round(cv.clientWidth * d); cv.height = Math.round(cv.clientHeight * d); ctx.setTransform(d, 0, 0, d, 0, 0); };
    size(); addEventListener('resize', size);
    const band = () => {
      const head = this.root.querySelector('#pane-play .pane-head'), c = cv.getBoundingClientRect();
      if (!head || this.tab !== 'play') return null;
      const h = head.getBoundingClientRect();
      const x0 = Math.max(h.left - c.left + Math.min(h.width, 640) + 60, c.width * .55), x1 = c.width - 60;
      const y0 = h.top - c.top + 10, y1 = h.bottom - c.top - 10;
      return x1 - x0 > 160 && y1 - y0 > 60 ? { x0, x1, y0: y0 + R, y1: y1 - R } : null;
    };
    const spawn = (at) => {
      const B = band();
      if (!B) { last = null; return; }
      // stay near the previous note, like a real map
      let x = last ? last.x + (Math.random() - .5) * 220 : (B.x0 + B.x1) / 2, y = last ? last.y + (Math.random() - .5) * 120 : (B.y0 + B.y1) / 2;
      x = Math.min(B.x1, Math.max(B.x0, x)); y = Math.min(B.y1, Math.max(B.y0, y));
      combo = combo >= 8 || Math.random() < .1 ? 1 : combo + 1;
      last = { x, y, t: at, n: combo };
      notes.push(last);
    };
    let actx = null;
    const tick = () => {
      try {
        actx ||= new AudioContext();
        const now = actx.currentTime, o = actx.createOscillator(), g = actx.createGain();
        o.type = 'triangle'; o.frequency.setValueAtTime(1400, now); o.frequency.exponentialRampToValueAtTime(500, now + .05);
        g.gain.setValueAtTime(.12, now); g.gain.exponentialRampToValueAtTime(.0001, now + .09);
        o.connect(g).connect(actx.destination); o.start(now); o.stop(now + .1);
      } catch { }
    };
    this.root.addEventListener('pointerdown', (e) => {
      if (this.tab !== 'play' || e.target.closest('button, a, input, select, label, .panel')) return;
      const c = cv.getBoundingClientRect(), x = e.clientX - c.left, y = e.clientY - c.top, now = performance.now();
      const n = notes.find((n) => !n.hit && Math.hypot(n.x - x, n.y - y) <= R * 1.15);
      if (!n) return;
      const off = Math.abs(now - n.t - APPROACH);
      if (now - n.t < APPROACH - 200) return; // too early: the note stays, like notelock
      const j = JUDGE.find(([w]) => off <= w);
      n.hit = now;
      pops.push({ x: n.x, y: n.y, t: now, text: j ? j[1] : '✕', color: j ? j[2] : '#ff6b6b' });
      if (j) tick();
    });
    const frame = (now) => {
      if (!this.root.isConnected || this.launched) return;
      if (!document.hidden) {
        while (now >= next) { spawn(next); next += BEAT; }
        if (logo) { const ph = ((now - t0) % BEAT) / BEAT; logo.style.transform = `scale(${1 + .07 * Math.pow(1 - ph, 4)})`; }
        const w = cv.clientWidth, h = cv.clientHeight;
        if (cv.width !== Math.round(w * Math.min(devicePixelRatio || 1, 2))) size();
        ctx.clearRect(0, 0, w, h);
        ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        for (let i = notes.length - 1; i >= 0; i--) {
          const n = notes[i], age = now - n.t, end = n.hit ? n.hit + FADE : n.t + APPROACH + FADE;
          if (now > end) { notes.splice(i, 1); continue; }
          const out = n.hit ? (now - n.hit) / FADE : age > APPROACH ? (age - APPROACH) / FADE : 0;
          const k = (1 - out) * Math.min(1, age / 220);
          const scale = n.hit ? 1 + .4 * out : 1 + .1 * out;
          ctx.globalAlpha = k;
          ctx.lineWidth = 3; ctx.strokeStyle = n.hit ? '#ffffff' : '#e8ecf4';
          ctx.fillStyle = 'rgba(18,23,34,.85)';
          ctx.beginPath(); ctx.arc(n.x, n.y, R * scale, 0, 7); ctx.fill(); ctx.stroke();
          ctx.fillStyle = '#e8ecf4'; ctx.font = `700 ${Math.round(20 * scale)}px "Chakra Petch", sans-serif`;
          ctx.fillText(n.n, n.x, n.y + 1);
          if (!n.hit && age < APPROACH) {
            ctx.globalAlpha = k * .9; ctx.strokeStyle = '#ff66aa'; ctx.lineWidth = 2.5;
            ctx.beginPath(); ctx.arc(n.x, n.y, R * (1 + 2.4 * (1 - age / APPROACH)), 0, 7); ctx.stroke();
          }
        }
        for (let i = pops.length - 1; i >= 0; i--) {
          const p = pops[i], a = (now - p.t) / 700;
          if (a >= 1) { pops.splice(i, 1); continue; }
          ctx.globalAlpha = 1 - a * a; ctx.fillStyle = p.color;
          ctx.font = '700 22px "Chakra Petch", sans-serif';
          ctx.fillText(p.text, p.x, p.y - 8 - 18 * a);
        }
        ctx.globalAlpha = 1;
      } else next = now;
      if (!still) requestAnimationFrame(frame);
    };
    if (!still) requestAnimationFrame(frame);
  }

  renderOptions() {
    const t = this.t, o = this.o;
    const host = this.root.querySelector('#ln-opts');
    host.innerHTML = SECTIONS.map((sec) => `
      <section class="panel opt-sec" data-sec="${sec}"><h2>${t('sec.' + sec)}</h2>
        ${OPTIONS.filter((x) => x.sec === sec).map((x) => this.optHtml(x)).join('')}
      </section>`).join('');
    for (const x of OPTIONS) {
      const el = host.querySelector(`[data-key="${x.key}"]`);
      const ev = x.type === 'range' || x.type === 'text' ? 'input' : 'change';
      el.addEventListener(ev, () => {
        const v = x.type === 'toggle' ? el.checked : x.type === 'range' || x.num ? +el.value : el.value;
        o[x.key] = v;
        if (x.type === 'range') host.querySelector(`[data-out="${x.key}"]`).textContent = this.rangeText(x, v);
        if (x.key === 'lang') { saveOptions(o); this.onLangChange?.(v); return; }
        this.changed(false);
      });
    }
    this.renderPresets();
    this.root.querySelector('#ln-cmd').textContent = commandLine(o);
  }

  rangeText(x, v) {
    if (v === 0 && x.zero) return this.t('opt.' + x.zero);
    return v + (x.unit ? (x.unit === '%' ? ' %' : ' ' + x.unit) : '');
  }

  optHtml(x) {
    const t = this.t, v = this.o[x.key], id = 'o-' + x.key, help = t('o.' + x.key + '.h');
    const helpHtml = help !== 'o.' + x.key + '.h' ? `<p class="help">${esc(help)}</p>` : '';
    const modified = v !== x.def ? ' mod' : '';
    if (x.type === 'toggle') return `<div class="opt tog${modified}"><label for="${id}"><span>${t('o.' + x.key)}</span><input type="checkbox" role="switch" id="${id}" data-key="${x.key}"${v ? ' checked' : ''}><i class="sw" aria-hidden="true"></i></label>${helpHtml}</div>`;
    if (x.type === 'range') return `<div class="opt${modified}"><label for="${id}"><span>${t('o.' + x.key)}</span><output data-out="${x.key}">${this.rangeText(x, v)}</output></label><input type="range" id="${id}" data-key="${x.key}" min="${x.min}" max="${x.max}" step="${x.step}" value="${v}">${helpHtml}</div>`;
    if (x.type === 'text') return `<div class="opt${modified}"><label for="${id}"><span>${t('o.' + x.key)}</span></label><input type="url" id="${id}" data-key="${x.key}" value="${esc(v)}" placeholder="${esc(x.placeholder())}" spellcheck="false">${helpHtml}</div>`;
    return `<div class="opt${modified}"><label for="${id}"><span>${t('o.' + x.key)}</span></label><select id="${id}" data-key="${x.key}">${x.choices.map((c) => `<option value="${c}"${c === v ? ' selected' : ''}>${esc(x.label ? x.label(c, t) : c)}</option>`).join('')}</select>${helpHtml}</div>`;
  }

  currentPreset() {
    const d = defaults();
    for (const [name, p] of Object.entries(PRESETS)) {
      const target = { ...d, ...p };
      if (OPTIONS.every((x) => NOT_PRESET.has(x.key) || this.o[x.key] === target[x.key])) return name;
    }
    return null;
  }

  renderPresets() {
    const cur = this.currentPreset();
    const box = this.root.querySelector('#ln-presets');
    box.innerHTML = Object.keys(PRESETS).map((p) => `<button type="button" class="chip${p === cur ? ' on' : ''}" aria-pressed="${p === cur}" data-p="${p}">${this.t('preset.' + p)}</button>`).join('')
      + (cur ? '' : `<span class="chip on static">${this.t('preset.custom')}</span>`);
    const dp = this.root.querySelector('#ln-dock-preset');
    if (dp) dp.textContent = this.t('dock.preset', this.t('preset.' + (cur || 'custom')));
    box.querySelectorAll('[data-p]').forEach((b) => b.onclick = () => {
      const keep = Object.fromEntries([...NOT_PRESET].map((k) => [k, this.o[k]]));
      this.o = { ...defaults(), ...PRESETS[b.dataset.p], ...keep };
      this.changed(true);
    });
  }

  changed(rerender) {
    saveOptions(this.o);
    if (rerender) this.renderOptions();
    else {
      this.renderPresets();
      this.root.querySelector('#ln-cmd').textContent = commandLine(this.o);
      for (const x of OPTIONS) this.root.querySelector(`[data-key="${x.key}"]`)?.closest('.opt')?.classList.toggle('mod', this.o[x.key] !== x.def);
    }
  }

  launch() {
    if (this.blocked || this.launched) return;
    this.launched = true;
    saveOptions(this.o);
    // still inside the click: the only moment a page may enter fullscreen
    if (this.o.fullscreen && !this.o.startGate && !document.fullscreenElement) document.documentElement.requestFullscreen({ navigationUI: 'hide' }).catch(() => { });
    this.root.classList.add('gone');
    setTimeout(() => { this.root.classList.remove('show', 'gone'); this.root.innerHTML = ''; }, 450);
    this.resolve?.(this.o);
  }

  // ----------------------------------------------------------------- download status (driven by main.js)
  download(s) {
    if (!this.root.isConnected || this.launched) return;
    const t = this.t, q = (id) => this.root.querySelector(id);
    if (!q('#ln-dl-state')) return;
    if (s.error) { q('#ln-dl-state').textContent = t('dl.failed', s.error); q('#ln-dl').classList.add('err'); return; }
    const frac = s.total ? s.loaded / s.total : 0;
    q('#ln-dl-state').textContent = s.done ? t('dl.ready') : t('dl.downloading');
    q('#ln-dl-files').textContent = t('dl.files', s.done ? s.total : s.loaded, s.total || '…');
    q('#ln-dl-size').textContent = s.bytes ? fmtBytes(s.bytes) : '';
    q('#ln-dl-speed').textContent = s.done ? (s.network === 0 || !s.bytes ? t('dl.cached') : '') : s.speed ? t('dl.speed', fmtBytes(s.speed)) : '';
    q('#ln-dl-bar').style.width = (s.done ? 100 : frac * 100).toFixed(1) + '%';
    this.root.classList.toggle('dl-done', !!s.done);
    if (!this.blocked) q('#ln-play-label').textContent = s.done ? t('launch.play') : t('launch.wait', Math.floor(frac * 100));
  }

  // ----------------------------------------------------------------- news / progress / issues
  async loadNews() {
    const lang = this.t.lang, t = this.t;
    let data;
    try {
      const r = await fetch(new URL('../launcher/news.json?t=' + Date.now(), import.meta.url), { cache: 'no-store' });
      if (!r.ok) throw new Error(r.status);
      data = await r.json();
    } catch {
      for (const id of ['#ln-posts', '#ln-latest']) this.root.querySelector(id).innerHTML = `<p class="muted">${t('news.error')}</p>`;
      return;
    }
    const post = (p) => `
      <article class="post">
        <div class="post-meta"><time datetime="${esc(p.date)}">${esc(new Date(p.date + 'T12:00:00').toLocaleDateString(lang, { day: 'numeric', month: 'long', year: 'numeric' }))}</time>${p.tag ? `<span class="tag">${esc(p.tag)}</span>` : ''}</div>
        <h3>${esc(tr(p.title, lang))}</h3>
        ${tr(p.body, lang) ? `<p>${esc(tr(p.body, lang))}</p>` : ''}
        ${(p.items || []).length ? `<ul>${p.items.map((i) => `<li>${esc(tr(i, lang))}</li>`).join('')}</ul>` : ''}
      </article>`;
    const posts = data.posts || [];
    this.root.querySelector('#ln-posts').innerHTML = posts.map((p) => `<div class="panel">${post(p)}</div>`).join('');
    this.root.querySelector('#ln-latest').innerHTML = posts.length ? post(posts[0]) + `<button type="button" class="link" data-goto="news">${t('play.allnews', posts.length)}</button>` : '';
    this.root.querySelector('#ln-latest [data-goto]')?.addEventListener('click', () => this.show('news'));

    // completion: done = 1, in progress = 0.5, weighted
    const items = data.progress || [];
    const W = items.reduce((a, i) => a + (i.weight || 1), 0) || 1;
    const got = items.reduce((a, i) => a + (i.weight || 1) * (i.status === 'done' ? 1 : i.status === 'partial' ? 0.5 : 0), 0);
    const pct = Math.round(100 * got / W);
    const done = items.filter((i) => i.status === 'done').length;
    this.port = { pct, done, total: items.length };
    this.root.querySelector('#ln-gauge').innerHTML = `
      <div class="meter-top"><b>${pct} %</b><span class="muted">${t('port.ndone', done, items.length)}</span></div>
      <div class="meter-bar" role="img" aria-label="${t('port.title')}: ${pct}%"><i style="width:${pct}%"></i></div>
      <p class="muted small">${t('port.how')}</p>`;
    this.root.querySelector('#ln-port').innerHTML = `<tbody>${items.map((i) => `
      <tr class="st-${esc(i.status)}"><td><span class="dot" aria-hidden="true"></span></td>
        <td>${esc(tr(i.name, lang))}${i.note ? `<div class="muted small">${esc(tr(i.note, lang))}</div>` : ''}</td>
        <td class="ps">${t('port.' + i.status)}</td></tr>`).join('')}</tbody>`;

    if (data.updated) this.root.querySelector('#ln-asof').textContent = t('issues.asof', new Date(data.updated + 'T12:00:00').toLocaleDateString(lang, { day: 'numeric', month: 'long' }));
    const issues = data.issues || [];
    this.root.querySelector('#ln-issues').innerHTML = issues.map((i) => `
      <li class="sev-${esc(i.severity)}"><span class="dot" aria-hidden="true"></span>
        <div><div class="iss-title"><strong>${esc(tr(i.title, lang))}</strong><span class="sev">${t('sev.' + i.severity)}</span></div>${tr(i.body, lang) ? `<p>${esc(tr(i.body, lang))}</p>` : ''}</div></li>`).join('');
    this.root.querySelector('#ln-issues-mini').innerHTML = issues.map((i) => `
      <li class="sev-${esc(i.severity)}"><span class="dot" aria-hidden="true"></span><span>${esc(tr(i.title, lang))}</span><span class="sev">${t('sev.' + i.severity)}</span></li>`).join('');
    this.renderSpec();
  }

  // ----------------------------------------------------------------- upstream version + how much upstream code is kept
  async loadBuild() {
    const t = this.t, lang = t.lang, box = this.root.querySelector('#ln-build');
    let b;
    try {
      const r = await fetch(new URL('../launcher/build.json?t=' + Date.now(), import.meta.url), { cache: 'no-store' });
      if (!r.ok) throw new Error(r.status);
      b = await r.json();
    } catch { box.innerHTML = `<p class="muted">${t('build.none')}</p>`; return; }
    this.b = b;
    const nf = (n) => n.toLocaleString(lang);
    const pct = (x) => x.toLocaleString(lang, { maximumFractionDigits: 2 });
    const lz = b.lazer || {};
    const sub = this.root.querySelector('#ln-play-sub');
    if (sub) sub.textContent = t('launch.sub', this.build);
    this.root.querySelector('#ln-ver').textContent = 'osu!lazer ' + (lz.release || '');
    if (lz.ahead) this.root.querySelector('#ln-ahead').textContent = t('build.ahead', lz.ahead);
    box.innerHTML = `
      <div class="kept"><b>${pct(b.keptPct)} %</b><span>${t('build.kept', nf(b.upstreamLines))}</span></div>
      <table class="trees"><thead><tr><th>${t('build.tree')}</th><th>${t('build.keptcol')}</th><th class="num">${t('build.changed')}</th></tr></thead><tbody>${(b.trees || []).map((tr) => {
        const kept = tr.upstream ? 100 * (tr.upstream - tr.changed) / tr.upstream : 100;
        return `<tr title="${esc(t('build.treeTip', nf(tr.touched), nf(tr.created), nf(tr.changed), nf(tr.added)))}">
          <td>${esc(tr.name)}</td><td><span class="tbar"><i style="width:${kept.toFixed(3)}%"></i></span><span class="tv">${pct(kept)} %</span></td>
          <td class="num muted">${t('build.treeNums', nf(tr.changed), nf(tr.upstream))}</td></tr>`;
      }).join('')}</tbody></table>
      <p class="muted small">${t('build.port', nf(b.portLines), pct(100 - b.upstreamShareOfBuild), nf(b.portInUpstreamTrees), nf(b.portStandalone))}</p>`;
    this.renderSpec();
  }

  // the engine spec sheet on the Play tab
  renderSpec() {
    const t = this.t, lang = t.lang, b = this.b, el = this.root.querySelector('#ln-spec');
    if (!el) return;
    const nf = (n) => n.toLocaleString(lang);
    const lz = b?.lazer || {};
    const rows = [];
    if (b) {
      const commitUrl = `https://github.com/ppy/osu/commit/${encodeURIComponent(lz.commit || '')}`;
      rows.push([t('spec.lazer'), `${esc(lz.release || '?')} <span class="muted">${lz.ahead ? esc(t('build.ahead', lz.ahead)) : ''}</span>`]);
      rows.push([t('spec.commit'), `<a href="${commitUrl}" target="_blank" rel="noopener">${esc((lz.commit || '').slice(0, 10))}</a> <span class="muted">${esc(lz.date ? new Date(lz.date + 'T12:00:00').toLocaleDateString(lang, { day: 'numeric', month: 'short', year: 'numeric' }) : '')}</span>`]);
      rows.push(['osu-framework', esc(b.framework || '?')]);
      rows.push(['Realm', esc(b.realm || '?')]);
      rows.push([t('spec.kept'), `${b.keptPct.toLocaleString(lang, { maximumFractionDigits: 2 })} % <span class="muted">${esc(t('spec.of', nf(b.upstreamLines)))}</span>`]);
      rows.push([t('spec.port'), `${nf(b.portLines)} <span class="muted">${esc(t('spec.lines'))}</span>`]);
    }
    if (this.port) rows.push([t('port.title'), `${this.port.pct} % <span class="muted">${esc(t('port.ndone', this.port.done, this.port.total))}</span>`]);
    rows.push([t('spec.runtime'), esc(t('spec.runtimev', this.o.workers))]);
    rows.push([t('spec.renderer'), esc(t('spec.rendererv', this.gpu || 'WebGL2'))]);
    rows.push([t('spec.audio'), esc(t('spec.audiov'))]);
    rows.push([t('spec.db'), esc(t('spec.dbv'))]);
    el.innerHTML = rows.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${v}</dd></div>`).join('');
  }

  // ----------------------------------------------------------------- machine info (all read from the browser)
  async systemInfo() {
    const t = this.t, rows = [];
    let gpu = '—';
    try {
      const c = document.createElement('canvas');
      const gl = c.getContext('webgl2');
      if (gl) {
        const ext = gl.getExtension('WEBGL_debug_renderer_info');
        gpu = ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
        gl.getExtension('WEBGL_lose_context')?.loseContext();
        // "ANGLE (NVIDIA, NVIDIA GeForce RTX 3070 (0x00002484) Direct3D11 vs_5_0 ps_5_0, D3D11)" -> "NVIDIA GeForce RTX 3070"
        const parts = String(gpu).replace(/^ANGLE \((.*)\)$/, '$1').split(', ');
        this.gpu = (parts[1] || parts[0]).replace(/\s*\(0x[0-9a-f]+\).*$/i, '').replace(/\s+(Direct3D|OpenGL|Metal)\b.*$/, '').trim();
      } else gpu = 'WebGL2 ✕';
    } catch { }
    const brands = navigator.userAgentData?.brands?.filter((b) => !/Not.?A.?Brand/i.test(b.brand)).map((b) => `${b.brand} ${b.version}`).join(', ');
    const ff = /Firefox\/(\d+)/.exec(navigator.userAgent), sf = !brands && !ff && /Version\/([\d.]+).*Safari/.exec(navigator.userAgent);
    rows.push([t('sys.cpu'), String(cores)]);
    if (navigator.deviceMemory) rows.push([t('sys.ram'), t('sys.atleast', navigator.deviceMemory)]);
    rows.push([t('sys.gpu'), gpu]);
    rows.push([t('sys.browser'), brands || (ff ? 'Firefox ' + ff[1] : sf ? 'Safari ' + sf[1] : navigator.userAgent.split(' ').pop())]);
    rows.push([t('sys.isolation'), crossOriginIsolated ? t('sys.ok') : t('sys.no')]);
    try {
      const est = await navigator.storage?.estimate?.();
      const persisted = await navigator.storage?.persisted?.();
      if (est) rows.push([t('sys.storage'), `${fmtBytes(est.usage || 0)} / ${fmtBytes(est.quota || 0)}, ${persisted ? t('sys.persist') : t('sys.besteffort')}`]);
    } catch { }
    let last = null;
    try { last = JSON.parse(localStorage.getItem('xc.lastBoot') || 'null'); } catch { }
    rows.push([t('sys.lastboot'), last ? `${(last.ms / 1000).toFixed(1)} s, ${new Date(last.at).toLocaleString(t.lang, { dateStyle: 'short', timeStyle: 'short' })}` : t('sys.never')]);
    const dl = this.root.querySelector('#ln-sys');
    if (dl) dl.innerHTML = rows.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join('');
    this.renderSpec();
  }
}
