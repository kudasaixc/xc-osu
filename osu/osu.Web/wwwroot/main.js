// xc-osu! bootstrap — xc softworks (kudasaixc)
// 1. the engine starts downloading immediately (dotnet.download(): assets only, no runtime yet)
// 2. the launcher shows while it downloads; launch options are applied when the runtime is created
// 3. the launch screen follows the real boot (loader events + the engine's own log lines) until the intro
import { captureConsole, xcLog } from './js/xc-log.js';
captureConsole();

import { dotnet } from './_framework/dotnet.js';
import { installUi } from './js/xc-ui.js';
import { makeT } from './js/xc-i18n.js';
import { Launcher, loadOptions, buildLaunch, fmtBytes, requestGameFullscreen } from './js/xc-launcher.js';
import { BootScreen, LogViewer } from './js/xc-boot.js';
import { startTelemetry } from './js/xc-telemetry.js';
import { PerfOverlay } from './js/xc-perf.js';

const $ = (id) => document.getElementById(id);
const say = (m) => console.log('[launch] ' + m);
const query = new URLSearchParams(location.search);
const engineBuild = 'source'; // replaced by stamp.py from the published engine asset fingerprints
const build = 'pacing1:' + engineBuild + ':' + (new URL(import.meta.url).searchParams.get('v') || 'dev');

let options = loadOptions();
let t = makeT(options.lang);
const blocked = !crossOriginIsolated;

// ---------------------------------------------------------------- background download
const dl = { loaded: 0, total: 0, bytes: 0, network: 0, speed: 0, done: false, error: null };
const listeners = new Set();
const emit = () => { for (const fn of listeners) fn(dl); };
const recent = []; // [time, bytes] for the speed readout

if (typeof PerformanceObserver !== 'undefined') {
  new PerformanceObserver((list) => {
    for (const e of list.getEntries()) {
      if (!e.name.includes('/_framework/')) continue;
      dl.bytes += e.decodedBodySize || e.encodedBodySize || 0;
      dl.network += e.transferSize || 0;
      recent.push([performance.now(), e.transferSize || 0]);
    }
    const now = performance.now();
    while (recent.length && now - recent[0][0] > 2000) recent.shift();
    dl.speed = recent.reduce((a, r) => a + r[1], 0) / 2;
    emit();
  }).observe({ type: 'resource', buffered: true });
}

// The browser HTTP cache refuses entries above a fraction of its total size, so the 110 MB+ resources
// assembly was downloaded again on every visit. Keep it in Cache Storage instead (file names are
// content-fingerprinted, so a new build simply misses and replaces the old entry).
const BIG_ASSET = /^osu\.Game\.Resources\./;
const BIG_CACHE = 'xc-big-assets-v1';
async function bigAsset(uri) {
  const url = new URL(uri, document.baseURI).href;
  let cache = null;
  try {
    cache = await caches.open(BIG_CACHE);
    const hit = await cache.match(url);
    if (hit) return hit;
  } catch { cache = null; }
  const res = await fetch(url, { credentials: 'same-origin' });
  if (cache && res.ok && res.status === 200) {
    cache.put(url, res.clone()).then(async () => {
      for (const k of await cache.keys()) if (k.url !== url) await cache.delete(k);
    }).catch(e => console.warn('[launch] could not keep ' + url + ' offline: ' + e));
  }
  return res;
}

const builder = dotnet
  .withConfig({ pthreadPoolInitialSize: options.workers, pthreadPoolUnusedSize: options.iosCompat ? 0 : Math.min(4, options.workers) })
  .withResourceLoader((type, name, defaultUri) => BIG_ASSET.test(name) ? bigAsset(defaultUri) : defaultUri)
  .withModuleConfig({ onDownloadResourceProgress: (loaded, total) => { dl.loaded = loaded; dl.total = total; emit(); } });

const dlStart = performance.now();
const downloaded = blocked ? new Promise(() => { }) : builder.download().then(() => {
  dl.ms = performance.now() - dlStart;
  dl.done = true; dl.speed = 0; emit();
}, (e) => { dl.error = e?.message || String(e); emit(); throw e; });
downloaded.catch(() => { });

// ---------------------------------------------------------------- launcher
const auto = !blocked && ((options.autoLaunch && !query.has('launcher')) || query.has('launch') || query.has('play'));
let launcher = null;
function openLauncher() {
  launcher = new Launcher($('xc-launcher'), t, {
    options, build, blocked,
    onLangChange: (lang) => { options = { ...launcher.o, lang }; t = makeT(lang); openLauncher(); launcher.download(dl); },
  });
  return launcher;
}
if (!auto) {
  openLauncher();
  const update = (s) => launcher?.download(s);
  listeners.add(update);
  update(dl);
  options = await launcher.waitForLaunch();
  listeners.delete(update);
  if (blocked) throw new Error('not cross-origin isolated');
}

startTelemetry(options, build, xcLog);
const L = buildLaunch(options);
let lastBoot = null;
try { lastBoot = JSON.parse(localStorage.getItem('xc.lastBoot') || 'null'); } catch { }
const boot = new BootScreen($('xc-boot'), t, { lastBoot, auto });

say(`xc-osu! build ${build} · ${navigator.hardwareConcurrency || '?'} logical cores · devicePixelRatio ${devicePixelRatio}`);
say('options: ' + Object.entries(L.env).map(([k, v]) => `${k}=${v}`).join(' '));
say(`webgl: ${JSON.stringify(L.gl)} · render scale ${Math.round(L.renderScale * 100)}% · audio latency ${L.audioLatency} · ${L.workers} worker threads`);

// globals read by xc-ui.js (canvas resolution), the native GL glue (context attributes) and the audio init below
globalThis.xcRenderScale = L.renderScale;
globalThis.xcIosCompat = L.iosCompat;
globalThis.xcGlAttributes = L.gl;

// ---------------------------------------------------------------- overlays
let rtRef = null;
const perf = new PerfOverlay($('xc-perf'), t, { position: L.perfPos, getWasmBytes: () => rtRef?.Module?.HEAP8?.buffer.byteLength || 0 });
xcLog.on((l) => perf.feed(l.text));
const logViewer = new LogViewer($('xc-logview'), t);
addEventListener('keydown', (e) => {
  if (!(e.ctrlKey && e.altKey)) return;
  if (e.code === 'KeyP') perf.toggle();
  else if (e.code === 'KeyL') logViewer.toggle();
}, true);
boot.onFinished = () => { if (L.perfOverlay) perf.toggle(true); };

// ---------------------------------------------------------------- download -> runtime
const dlStage = (s) => boot.download(s.loaded, s.total, s.bytes ? fmtBytes(s.bytes) + (s.done && s.network === 0 ? ' · cache' : '') : '');
listeners.add(dlStage);
dlStage(dl);
const t0 = performance.now();
try {
  await downloaded;
} catch (e) {
  boot.fail(t('dl.failed', dl.error));
  throw e;
}
listeners.delete(dlStage);
boot.stage('download', 'done', `${dl.total} · ${fmtBytes(dl.bytes)}${dl.network === 0 ? ' · ' + t('dl.cached') : ' · ' + fmtBytes(dl.network) + ' net'}`, dl.ms);
say(`engine assets: ${dl.total} files, ${fmtBytes(dl.bytes)} (${fmtBytes(dl.network)} over the network)`);

boot.stage('runtime', 'active');
builder.withConfig({ pthreadPoolInitialSize: L.workers, pthreadPoolUnusedSize: L.iosCompat ? 0 : Math.min(4, L.workers) });
// awaited rAF loop by default; XC_DRAW_LEGACY=0 opts into the direct rAF pump (see PORTING.md, frame pacing)
if (!('XC_DRAW_LEGACY' in L.env)) builder.withEnvironmentVariable('XC_DRAW_LEGACY', '1');
for (const [k, v] of Object.entries(L.env)) builder.withEnvironmentVariable(k, v);
if (query.has('log')) builder.withEnvironmentVariable('XC_VERBOSE_LOG', '1');
let rt;
try {
  const c0 = performance.now();
  rt = rtRef = await builder.create();
  say(`.NET runtime created in ${(performance.now() - c0).toFixed(0)} ms · wasm memory ${fmtBytes(rt.Module.HEAP8.buffer.byteLength)}`);
  boot.stage('runtime', 'done', `.NET ${rt.runtimeBuildInfo?.productVersion || ''}`.trim());
} catch (e) {
  console.error('[launch] runtime start failed', e);
  boot.fail(String(e?.message || e));
  throw e;
}

const FS = rt.Module.FS;
const ROOT = '/xc';

// ---------------------------------------------------------------- persistent storage (IDBFS)
boot.stage('storage', 'active');
try { FS.mkdir(ROOT); } catch { }
let persistent = false;
try {
  const s0 = performance.now();
  FS.mount(rt.Module.IDBFS || FS.filesystems.IDBFS, { autoPersist: false }, ROOT);
  await new Promise((res) => FS.syncfs(true, (err) => { if (err) console.warn(err); res(); }));
  persistent = true;
  let files = 0;
  const walk = (p) => { for (const n of FS.readdir(p)) { if (n === '.' || n === '..') continue; const f = p + '/' + n; if (FS.isDir(FS.stat(f).mode)) walk(f); else files++; } };
  try { walk(ROOT); } catch { }
  const persisted = await navigator.storage?.persist?.().catch(() => false);
  say(`IndexedDB storage mounted at ${ROOT}: ${files} files loaded in ${(performance.now() - s0).toFixed(0)} ms (persistent: ${!!persisted})`);
  boot.stage('storage', 'done', t('stage.files', files));
} catch (e) {
  console.warn('[launch] persistent storage unavailable, running in memory', e);
  boot.stage('storage', 'fail', 'in memory');
}

let syncing = false, dirty = false;
function flush() {
  if (!persistent) return;
  if (syncing) { dirty = true; return; }
  syncing = true;
  FS.syncfs(false, (err) => { syncing = false; if (err) console.warn('syncfs', err.errno, err.code, err.message, err.stack); if (dirty) { dirty = false; flush(); } });
}
setInterval(flush, 15000);

// ---------------------------------------------------------------- audio output (xcbass -> AudioWorklet)
let audioCtx = null;
globalThis.xcAudioResume = () => {
  if (!audioCtx || audioCtx.state === 'running') return;
  audioCtx.resume().catch(() => { });
};
globalThis.xcAudioInit = async (memory, ring, ctrl, cap, rate) => {
  if (audioCtx) return;
  const buffer = memory ? memory.buffer : (rt.Module.wasmMemory?.buffer ?? rt.Module.HEAPU8.buffer);
  if (ctrl + 64 > buffer.byteLength) { console.error('[xc-osu!] audio ring outside the visible heap', ctrl, buffer.byteLength); return; }
  const ctrlView = new Int32Array(buffer, ctrl, 16);
  audioCtx = new AudioContext({ sampleRate: rate, latencyHint: L.audioLatency || 'interactive' });
  await audioCtx.audioWorklet.addModule(new URL('./js/xc-audio-worklet.js', import.meta.url));
  const node = new AudioWorkletNode(audioCtx, 'xc-output', {
    numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2],
    processorOptions: { buffer, ring, ctrl, cap },
  });
  node.connect(audioCtx.destination);
  const updateLatency = () => {
    const secs = (audioCtx.outputLatency || 0) + (audioCtx.baseLatency || 0);
    // frames consumed by the worklet one render quantum before they reach the output stage
    Atomics.store(ctrlView, 3, Math.round(secs * audioCtx.sampleRate) + 128);
    Atomics.store(ctrlView, 6, audioCtx.state === 'running' ? 1 : 0);
  };
  updateLatency();
  setInterval(updateLatency, 500);
  globalThis.xcAudioDebug = () => ({ state: audioCtx.state, rate: audioCtx.sampleRate, read: ctrlView[0], write: ctrlView[1], cap: ctrlView[2], latency: ctrlView[3], running: ctrlView[6], underruns: ctrlView[5] });
  audioCtx.onstatechange = updateLatency;
  if (audioCtx.state !== 'running') xcAudioResume();
  console.log(`[xc-osu!] audio: ${audioCtx.sampleRate} Hz, latencyHint ${L.audioLatency}, base latency ${(audioCtx.baseLatency * 1000).toFixed(1)} ms, state ${audioCtx.state}`);
};

// xcbass may start its output from a worker thread: it then posts the ring location here.
const audioChannel = new BroadcastChannel('xc-audio');
audioChannel.onmessage = (e) => {
  globalThis.xcTelemetryEvent?.('worker', { name: 'audio-init' });
  if (audioCtx) return;
  const { ring, ctrl, cap, rate } = e.data;
  globalThis.xcAudioInit(null, ring, ctrl, cap, rate).catch((err) => console.error('[xc-osu!] audio init failed', err));
};

// ---------------------------------------------------------------- UI bridge
const canvas = $('xc-canvas');
let attachedAt = 0, fallback = 0;
// while the launch screen is up, keys belong to it (start gate), not to the game: registered before xc-ui.js's listeners
for (const type of ['keydown', 'keyup']) addEventListener(type, (e) => {
  if (boot.done || (e.ctrlKey && e.altKey)) return;
  e.stopImmediatePropagation();
  if (type === 'keydown') boot.key(e);
}, true);
const ui = installUi(rt, canvas, {
  onStartGate: () => {
    clearTimeout(fallback);
    say(`engine ready ${((performance.now() - t0) / 1000).toFixed(1)} s after the download, waiting for the start click`);
    boot.showGate(() => {
      // still inside the player's gesture: the only moment the page may go fullscreen and start audio
      globalThis.xcTelemetryCheckpoint?.('start-click');
      if (L.fullscreen) requestGameFullscreen();
      globalThis.xcAudioResume?.();
      ui.openStartGate();
      globalThis.xcTelemetryCheckpoint?.('gate-open');
      canvas.focus({ preventScroll: true });
      say('start click: fullscreen ' + (L.fullscreen ? 'requested' : 'off') + ', audio ' + (globalThis.xcAudioDebug?.().state || 'not initialised yet'));
    });
  },
  onAttached: () => {
    attachedAt = performance.now();
    say(`window attached to the engine (${canvas.width}x${canvas.height} px)`);
    // safety net: if the intro marker never shows up (log format change), don't keep the game hidden forever
    fallback = setTimeout(() => { if (!boot.done && !boot.gate) { say('no intro marker after 120 s, showing the game anyway'); boot.finish(); } }, 120000);
  },
  onHidden: flush,
  onFirstGesture: () => { globalThis.xcAudioResume?.(); },
  onExit: (code, msg) => {
    flush();
    if (code) { console.error('[launch] xc-osu! crashed:\n' + msg); if (!boot.done) boot.fail(msg); else { logViewer.show(); } }
    else say('xc-osu! has exited.');
  },
  storeImport: async (file) => {
    const dir = ROOT + '/imports';
    try { FS.mkdir(dir); } catch { }
    const path = `${dir}/${Date.now()}-${file.name.replace(/[\\/]/g, '_')}`;
    FS.writeFile(path, new Uint8Array(await file.arrayBuffer()));
    return path;
  },
  download: (path) => {
    try {
      const data = FS.readFile(path);
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([data]));
      a.download = path.split('/').pop();
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 30000);
    } catch (e) { console.warn('xc-osu!: cannot export', path, e); }
  },
});

globalThis.xcFlushStorage = flush;
boot.stage('host', 'active');
say(`starting the game (${((performance.now() - t0) / 1000).toFixed(2)} s after the download finished)`);
await rt.runMain();
