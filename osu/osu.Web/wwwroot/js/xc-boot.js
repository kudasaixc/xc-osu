// xc-osu! verbose launch screen + log viewer — xc softworks (kudasaixc)
// Stages are driven by real events: loader callbacks from main.js, and the actual log lines the runtime/game prints.

import { xcLog, fmtTime } from './xc-log.js';

const STAGES = [
  // id, matcher on log text that marks the stage as started / finished
  { id: 'download' },
  { id: 'runtime' },
  { id: 'storage' },
  { id: 'host', start: /booting osu!lazer on WebAssembly/, end: /web host starting/ },
  { id: 'gpu', start: /Initialising "WebGL" renderer/, end: /Renderer initialised/ },
  { id: 'audio', end: /BASS initialised/ },
  { id: 'database', start: /Open file: .*client\.realm|realm file store/, end: /Opened realm/ },
  { id: 'components', start: /^\[runtime\] Loading \w+\.\.\.$/, end: / entered (Intro\w*|MainMenu)#/ },
];

const LEVEL_CLASS = { warn: 'w', error: 'e' };

function tagClass(text) {
  const m = /^\[([a-z!.\-]+)\]/i.exec(text);
  if (!m) return '';
  switch (m[1]) {
    case 'launch': case 'xc-osu!': return 't-launch';
    case 'runtime': return 't-runtime';
    case 'network': return 't-network';
    case 'database': return 't-db';
    case 'realm': return 't-realm';
    case 'perf': return 't-perf';
    case 'diag': return 't-diag';
    default: return '';
  }
}

function lineEl(l) {
  const row = document.createElement('div');
  row.className = 'bl ' + (LEVEL_CLASS[l.level] || '') + ' ' + tagClass(l.text) + (/draw worker: stage=|^\[perf\]/.test(l.text) ? ' dim' : '');
  const ts = document.createElement('span');
  ts.className = 'ts';
  ts.textContent = fmtTime(l.t);
  row.append(ts, document.createTextNode(l.text || ' '));
  return row;
}

/** A scrolling terminal fed from xcLog; batched to one DOM update per frame so realm trace floods stay cheap. */
class Terminal {
  constructor(el, { max = 1500, filter = null } = {}) {
    this.el = el; this.max = max; this.filter = filter; this.queue = []; this.follow = true;
    el.addEventListener('scroll', () => { this.follow = el.scrollTop + el.clientHeight >= el.scrollHeight - 24; }, { passive: true });
    this.off = xcLog.on((l) => this.add(l));
  }
  add(l) {
    if (this.filter && !this.filter(l)) return;
    this.queue.push(l);
    if (!this.raf) this.raf = requestAnimationFrame(() => this.flush());
  }
  replay() { this.el.textContent = ''; for (const l of xcLog.lines.slice(-this.max)) if (!this.filter || this.filter(l)) this.queue.push(l); this.flush(); }
  flush() {
    this.raf = 0;
    const q = this.queue.splice(0).slice(-this.max);
    const frag = document.createDocumentFragment();
    for (const l of q) frag.append(lineEl(l));
    this.el.append(frag);
    while (this.el.childElementCount > this.max) this.el.firstElementChild.remove();
    if (this.follow) this.el.scrollTop = this.el.scrollHeight;
  }
  dispose() { this.off(); cancelAnimationFrame(this.raf); }
}

export class BootScreen {
  constructor(root, t, { lastBoot, auto }) {
    this.t = t; this.root = root; this.lastBoot = lastBoot; this.components = 0; this.done = false;
    this.stageState = new Map(STAGES.map((s) => [s.id, { state: 'pending', t0: 0, t1: 0 }]));
    root.innerHTML = `
      <div class="boot-head">
        <div class="boot-title"><span class="xc-mark">xc-osu!</span><span class="boot-sub">${t('boot.title')}</span></div>
        <div class="boot-clock" id="boot-clock">+0.0 s</div>
      </div>
      <div class="boot-bar"><i id="boot-bar"></i></div>
      <div class="boot-body">
        <ol class="boot-stages" id="boot-stages">
          ${STAGES.map((s) => `<li data-id="${s.id}" class="pending"><span class="st-icon" aria-hidden="true"></span><span class="st-name">${t('stage.' + s.id)}</span><span class="st-detail"></span><span class="st-time"></span></li>`).join('')}
        </ol>
        <div class="boot-term" id="boot-term" role="log" aria-live="off"></div>
      </div>
      <div class="boot-foot">
        <span id="boot-hint">${auto ? t('boot.autoHint') : (lastBoot ? t('boot.last', (lastBoot.ms / 1000).toFixed(1)) : '')}</span>
        <span class="boot-actions"><button type="button" class="ghost" id="boot-save">${t('log.save')}</button></span>
      </div>`;
    root.querySelector('#boot-save').onclick = () => xcLog.save();
    this.term = new Terminal(root.querySelector('#boot-term'));
    this.term.replay();
    this.off = xcLog.on((l) => this.onLine(l));
    root.classList.add('show');
    this.started = performance.now();
    this.clock = setInterval(() => {
      root.querySelector('#boot-clock').textContent = '+' + ((performance.now() - this.started) / 1000).toFixed(1) + ' s';
    }, 100);
  }

  el(id) { return this.root.querySelector(`li[data-id="${id}"]`); }

  stage(id, state, detail, durationMs) {
    const s = this.stageState.get(id);
    if (!s || this.done) return;
    const now = performance.now();
    if (durationMs != null) s.t0 = now - durationMs;
    if (state === 'active' && s.state === 'pending') s.t0 = now;
    if (state === 'done' || state === 'fail') {
      if (s.state === 'done') return;
      if (!s.t0) s.t0 = now;
      s.t1 = now;
      // stages are sequential in practice: anything earlier that never reported is implicitly through
      for (const [k, o] of this.stageState) { if (k === id) break; if (o.state !== 'done' && o.state !== 'fail') { o.state = 'done'; o.t1 = o.t1 || now; this.paint(k); } }
    }
    s.state = state;
    if (detail != null) this.el(id).querySelector('.st-detail').textContent = detail;
    this.paint(id);
    this.progress();
  }

  paint(id) {
    const s = this.stageState.get(id), li = this.el(id);
    li.className = s.state;
    li.querySelector('.st-time').textContent = s.t1 ? ((s.t1 - s.t0) / 1000).toFixed(2) + ' s' : '';
  }

  progress() {
    const ids = STAGES.map((s) => s.id);
    let p = 0;
    ids.forEach((id, i) => {
      const s = this.stageState.get(id).state;
      if (s === 'done') p = i + 1;
      else if (s === 'active' && id === 'components' && this.lastBoot?.components) p = i + Math.min(0.95, this.components / this.lastBoot.components);
      else if (s === 'active' && id === 'download' && this.dlFrac != null) p = i + this.dlFrac * 0.95;
    });
    this.root.querySelector('#boot-bar').style.width = (100 * p / ids.length).toFixed(1) + '%';
  }

  download(loaded, total, bytesText) {
    this.dlFrac = total ? loaded / total : 0;
    this.stage('download', 'active', `${loaded}/${total}${bytesText ? ' · ' + bytesText : ''}`);
  }

  onLine(l) {
    if (this.done) return;
    const text = l.text;
    for (const s of STAGES) {
      if (s.start?.test(text)) this.stage(s.id, 'active');
      if (s.end?.test(text)) this.stage(s.id, 'done');
    }
    if (/^\[runtime\] Loaded \w+!$/.test(text)) {
      this.components++;
      this.stage('components', 'active', this.t('stage.componentsN', this.components) + (this.lastBoot?.components ? ` / ~${this.lastBoot.components}` : ''));
    }
    const m = /^\[runtime\] 🔈 BASS initialised|Driver:\s+(.*)$/.exec(text);
    if (m && m[1]) this.el('audio').querySelector('.st-detail').textContent = m[1].trim();
    const gl = /GL Renderer:\s+(.*)$/.exec(text);
    if (gl) this.el('gpu').querySelector('.st-detail').textContent = gl[1].trim();
    if (/ entered (Intro\w*|MainMenu)#/.test(text)) this.finish();
  }

  fail(message) {
    this.done = true;
    clearInterval(this.clock);
    for (const [id, s] of this.stageState) if (s.state === 'active') { s.state = 'fail'; s.t1 = performance.now(); this.paint(id); }
    this.root.classList.add('failed');
    const hint = this.root.querySelector('#boot-hint');
    hint.textContent = message ? message.split('\n')[0] : this.t('boot.failed');
    const back = document.createElement('button');
    back.type = 'button'; back.className = 'primary-sm'; back.textContent = this.t('boot.back');
    back.onclick = () => { const u = new URL(location.href); u.searchParams.set('launcher', ''); location.href = u.toString(); };
    this.root.querySelector('.boot-actions').prepend(back);
  }

  /** Engine fully loaded and holding its intro: one click/key opens it (inside that gesture: fullscreen + audio). */
  showGate(onOpen) {
    if (this.done || this.gate) return;
    this.stage('components', 'done', this.t('stage.componentsN', this.components));
    const g = document.createElement('div');
    g.className = 'gate';
    g.innerHTML = `<div class="gate-ring" aria-hidden="true"></div><div class="gate-title">${this.t('gate.title')}</div><div class="gate-sub">${this.t('gate.sub')} · ${this.t('gate.fs')}</div>`;
    this.root.append(g);
    this.root.classList.add('gated');
    this.root.querySelector('#boot-bar').style.width = '100%';
    this.gate = { el: g, onOpen };
    this.root.addEventListener('pointerdown', this.gateClick = (e) => { if (e.button === 0 || e.pointerType !== 'mouse') this.openGate(); });
  }

  /** called for every key while the launch screen is up (main.js keeps keys away from the game meanwhile) */
  key(e) {
    if (!this.gate || this.gate.opened || e.repeat || ['Shift', 'Control', 'Alt', 'Meta', 'F12'].includes(e.key) || (e.ctrlKey && e.shiftKey)) return;
    e.preventDefault();
    this.openGate();
  }

  openGate() {
    if (!this.gate || this.gate.opened) return;
    this.gate.opened = true;
    this.root.removeEventListener('pointerdown', this.gateClick);
    this.root.classList.add('opening');
    try { this.gate.onOpen(); } catch (e) { console.error('[launch] start gate', e); }
  }

  finish() {
    if (!this.done) dispatchEvent(new CustomEvent('xc-boot-complete', { detail: performance.now() }));
    if (this.done) return;
    const ms = performance.now() - this.started;
    this.stage('components', 'done', this.t('stage.componentsN', this.components));
    this.done = true;
    clearInterval(this.clock);
    this.root.querySelector('#boot-bar').style.width = '100%';
    this.root.querySelector('#boot-hint').textContent = this.t('boot.ready', (ms / 1000).toFixed(1));
    try { localStorage.setItem('xc.lastBoot', JSON.stringify({ ms: Math.round(ms), components: this.components, at: Date.now() })); } catch { }
    this.off(); this.term.dispose();
    // after the start click the intro is already playing: get out of the way at once
    setTimeout(() => { this.root.classList.add('gone'); setTimeout(() => { this.root.classList.remove('show', 'gone', 'gated', 'opening'); this.root.innerHTML = ''; }, 700); }, this.gate ? 0 : 900);
    this.onFinished?.();
  }
}

/** In-game log viewer (Ctrl+Alt+L): same terminal, with a text filter and export. */
export class LogViewer {
  constructor(root, t) { this.root = root; this.t = t; this.open = false; }
  toggle() { this.open ? this.close() : this.show(); }
  show() {
    this.open = true;
    const t = this.t;
    this.root.innerHTML = `
      <div class="lv-head"><strong>${t('log.title')}</strong>
        <input type="search" id="lv-filter" placeholder="${t('log.filter')}" spellcheck="false">
        <button type="button" class="ghost" id="lv-save">${t('log.save')}</button>
        <button type="button" class="ghost" id="lv-close" aria-label="${t('log.close')}">✕</button></div>
      <div class="boot-term" id="lv-term" role="log"></div>
      <div class="lv-foot">${t('log.hint')}</div>`;
    let q = '';
    const termEl = this.root.querySelector('#lv-term');
    this.term = new Terminal(termEl, { max: 3000, filter: (l) => !q || l.text.toLowerCase().includes(q) });
    this.term.replay();
    const f = this.root.querySelector('#lv-filter');
    f.oninput = () => { q = f.value.trim().toLowerCase(); this.term.replay(); };
    // xc-ui.js lets keys typed inside [data-xc-own-keys] reach the DOM instead of the game
    this.root.setAttribute('data-xc-own-keys', '');
    this.root.querySelector('#lv-save').onclick = () => xcLog.save();
    this.root.querySelector('#lv-close').onclick = () => this.close();
    this.root.classList.add('show');
  }
  close() { this.open = false; this.term?.dispose(); this.root.classList.remove('show'); this.root.innerHTML = ''; document.getElementById('xc-canvas')?.focus(); }
}
