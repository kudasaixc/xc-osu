// xc-osu! performance overlay (Ctrl+Alt+P): xc softworks (kudasaixc)
// Sources, all live: the [perf] probe printed by Program.cs (per-thread fps / frame time, GC, managed heap),
// the UI thread's own requestAnimationFrame cadence, the wasm linear memory size, Chromium's JS heap,
// and the xcbass ring buffer state (xcAudioDebug).

const SERIES = ['#3987e5', '#d95926', '#199e70']; // validated categorical slots 1-3 (dark surface)
const N = 120;

class Ring {
  constructor() { this.v = new Float32Array(N); this.n = 0; this.i = 0; }
  push(x) { this.v[this.i] = x; this.i = (this.i + 1) % N; this.n = Math.min(N, this.n + 1); }
  last() { return this.n ? this.v[(this.i + N - 1) % N] : NaN; }
  *values() { for (let k = 0; k < this.n; k++) yield this.v[(this.i + N - this.n + k) % N]; }
}

class Chart {
  constructor(parent, title, unit, names, fmt = (x) => x.toFixed(0)) {
    this.unit = unit; this.fmt = fmt; this.names = names;
    this.data = names.map(() => new Ring());
    const box = document.createElement('div');
    box.className = 'pf-chart';
    box.innerHTML = `<div class="pf-title">${title} <span class="pf-unit">${unit}</span></div><div class="pf-legend"></div>`;
    this.legend = box.querySelector('.pf-legend');
    this.legend.innerHTML = names.map((n, i) => `<span><i style="background:${SERIES[i]}"></i>${n} <b data-i="${i}">–</b></span>`).join('');
    this.canvas = document.createElement('canvas');
    box.append(this.canvas);
    parent.append(box);
  }
  push(values) { values.forEach((v, i) => this.data[i].push(v)); }
  draw() {
    const c = this.canvas, dpr = devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    if (!w) return;
    if (c.width !== Math.round(w * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    let max = 0;
    for (const r of this.data) for (const v of r.values()) if (v > max) max = v;
    max = niceMax(max);
    // recessive grid: baseline + half line
    g.strokeStyle = 'rgba(255,255,255,0.08)'; g.lineWidth = 1;
    g.beginPath(); g.moveTo(0, h - 0.5); g.lineTo(w, h - 0.5); g.moveTo(0, Math.round(h / 2) + 0.5); g.lineTo(w, Math.round(h / 2) + 0.5); g.stroke();
    g.fillStyle = 'rgba(242,234,255,0.45)'; g.font = '9px ui-monospace, monospace'; g.textBaseline = 'top';
    g.fillText(this.fmt(max), 2, 1);
    this.data.forEach((r, i) => {
      if (!r.n) return;
      g.strokeStyle = SERIES[i]; g.lineWidth = 2; g.lineJoin = 'round'; g.lineCap = 'round';
      g.beginPath();
      let k = 0;
      for (const v of r.values()) {
        const x = w - (r.n - 1 - k) * (w / (N - 1));
        const y = h - 2 - (max ? (v / max) * (h - 12) : 0);
        k++ === 0 ? g.moveTo(x, y) : g.lineTo(x, y);
      }
      g.stroke();
    });
    for (const b of this.legend.querySelectorAll('b')) { const v = this.data[+b.dataset.i].last(); b.textContent = Number.isFinite(v) ? this.fmt(v) : '–'; }
  }
}

function niceMax(x) {
  if (!(x > 0)) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(x)));
  for (const m of [1, 1.5, 2, 3, 5, 7.5, 10]) if (x <= m * p) return m * p;
  return 10 * p;
}

export class PerfOverlay {
  constructor(root, t, { position = 'tr', getWasmBytes } = {}) {
    this.root = root; this.t = t; this.getWasmBytes = getWasmBytes; this.visible = false;
    root.className = 'pf pos-' + position;
    root.innerHTML = `<div class="pf-head"><strong>${t('perf.title')}</strong><span class="pf-key">Ctrl+Alt+P</span></div>`;
    const body = document.createElement('div');
    body.className = 'pf-body';
    root.append(body);
    this.fps = new Chart(body, t('perf.fps'), 'fps', [t('perf.draw'), t('perf.browser')]);
    this.upd = new Chart(body, t('perf.update'), 'Hz', [t('perf.update'), t('perf.audio'), t('perf.input')]);
    this.ft = new Chart(body, t('perf.frametime'), 'ms', [t('perf.draw'), t('perf.update')], (x) => x.toFixed(1));
    this.mem = new Chart(body, t('perf.memory'), 'MB', [t('perf.heap'), t('perf.wasm'), t('perf.js')]);
    this.gc = new Chart(body, t('perf.gc'), 'ms', [t('perf.pause')]);
    this.stats = document.createElement('div');
    this.stats.className = 'pf-stats';
    body.append(this.stats);
    this.threads = {}; this.gcInfo = ''; this.audio = '';
    this.rafFps = 0;
    this.measureRaf();
    setInterval(() => this.sample(), 1000);
  }

  measureRaf() {
    let frames = 0, last = performance.now();
    const step = (now) => {
      frames++;
      if (now - last >= 500) { this.rafFps = frames * 1000 / (now - last); frames = 0; last = now; }
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }

  /** fed every captured log line; only [perf] lines matter */
  feed(text) {
    if (!text.startsWith('[perf]')) return;
    const th = {};
    for (const m of text.matchAll(/ ([A-Za-z]+)=(\d+)fps\/([\d.]+)ms/g)) th[m[1].toLowerCase()] = { fps: +m[2], ms: +m[3] };
    this.threads = th;
    const heap = /heap=(\d+)MB/.exec(text), pause = /pause\+(\d+)ms/.exec(text);
    this.heapMb = heap ? +heap[1] : NaN;
    this.pauseMs = pause ? +pause[1] : NaN;
    const gcs = [...text.matchAll(/gc(\d)\+(\d+)/g)].map((m) => `gen${m[1]} +${m[2]}`).join(' · ');
    this.gcInfo = gcs;
    this.gotPerf = true;
  }

  sample() {
    const th = this.threads;
    const wasm = (this.getWasmBytes?.() || 0) / 1048576;
    const js = performance.memory ? performance.memory.usedJSHeapSize / 1048576 : NaN;
    this.fps.push([th.draw?.fps ?? NaN, this.rafFps]);
    this.upd.push([th.update?.fps ?? NaN, th.audio?.fps ?? NaN, th.input?.fps ?? NaN]);
    this.ft.push([th.draw?.ms ?? NaN, th.update?.ms ?? NaN]);
    this.mem.push([this.heapMb ?? NaN, wasm, js]);
    this.gc.push([this.pauseMs ?? NaN]);
    this.threads = {}; this.pauseMs = NaN; // a probe line older than one sample is not repeated

    let audio = '';
    try {
      const a = globalThis.xcAudioDebug?.();
      if (a) {
        const buffered = ((a.write - a.read) | 0) / a.rate * 1000; // absolute frame counters
        audio = `${this.t('perf.audio')}: ${(a.rate / 1000).toFixed(1)} kHz · ${a.state} · ${this.t('perf.buffer')} ${buffered.toFixed(0)} ms · ${this.t('perf.latency')} ${(a.latency / a.rate * 1000).toFixed(0)} ms · ${this.t('perf.underruns')} ${a.underruns}`;
      }
    } catch { }
    this.stats.textContent = [audio, this.gcInfo && 'GC ' + this.gcInfo, !this.gotPerf ? this.t('perf.waiting') : ''].filter(Boolean).join('\n');
    if (this.visible) for (const c of [this.fps, this.upd, this.ft, this.mem, this.gc]) c.draw();
  }

  toggle(force) {
    this.visible = force ?? !this.visible;
    this.root.classList.toggle('show', this.visible);
    if (this.visible) this.sample();
  }
}
