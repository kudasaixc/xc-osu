// xc-osu! bootstrap: xc softworks (kudasaixc)
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
// Engine .wasm files are served from this CDN (Cloudflare R2) when set, with the site itself as fallback.
// tools/r2-sync.py fills it in at deploy time, only once every .wasm of the build is on the CDN.
const cdnBase = '';

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
// The CDN copy is tried first; anything going wrong there (blocked domain, missing file, bad content)
// falls back to the same file on the site. Workers and dotnet.js always stay same-origin.
const originFetch = (uri, integrity) => fetch(uri, { credentials: 'same-origin', ...(integrity ? { integrity } : {}) });
async function engineFetch(uri, integrity) {
  if (!cdnBase) return originFetch(uri, integrity);
  const cdn = cdnBase + '_framework/' + new URL(uri, document.baseURI).pathname.split('/').pop();
  try {
    const res = await fetch(cdn, { mode: 'cors', credentials: 'omit', ...(integrity ? { integrity } : {}) });
    if (res.ok) return res;
    console.warn('[launch] cdn answered ' + res.status + ' for ' + cdn + ', using the site copy');
  } catch (e) {
    console.warn('[launch] cdn failed for ' + cdn + ' (' + e + '), using the site copy');
  }
  return originFetch(uri, integrity);
}

// The Cache Storage key stays the site URL wherever the bytes came from, so moving to or from the CDN
// does not make anyone download the file again.
async function bigAsset(uri) {
  const url = new URL(uri, document.baseURI).href;
  let cache = null;
  try {
    cache = await caches.open(BIG_CACHE);
    const hit = await cache.match(url);
    if (hit) return hit;
  } catch { cache = null; }
  const res = await engineFetch(url);
  if (cache && res.ok && res.status === 200) {
    cache.put(url, res.clone()).then(async () => {
      for (const k of await cache.keys()) if (k.url !== url) await cache.delete(k);
    }).catch(e => console.warn('[launch] could not keep ' + url + ' offline: ' + e));
  }
  return res;
}

const builder = dotnet
  // The game's translations are not loaded in the browser yet, so its interface is English. Without this the runtime
  // takes the browser's language as the game language: a French browser got an English game that asked the server
  // for French news, changelog and wiki. Picking a language in the game's settings still overrides it.
  .withApplicationCulture('en')
  .withConfig({ pthreadPoolInitialSize: options.workers, pthreadPoolUnusedSize: options.iosCompat ? 0 : Math.min(4, options.workers) })
  .withResourceLoader((type, name, defaultUri, integrity) => {
    if (BIG_ASSET.test(name)) return bigAsset(defaultUri);
    return cdnBase && /\.wasm(\?|$)/.test(defaultUri) ? engineFetch(defaultUri, integrity) : defaultUri;
  })
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
globalThis.xcTelemetryCheckpoint?.('launch-start');
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
  console.error('[launch] engine download failed: ' + dl.error);
  globalThis.xcTelemetryCheckpoint?.('download-failed');
  globalThis.xcTelemetryCrash?.('download', { text: String(e?.stack || e?.message || e) });
  boot.fail(t('dl.failed', dl.error));
  throw e;
}
listeners.delete(dlStage);
// boot checkpoints: a session that never reaches the menu (seen on iOS) shows where it stopped
globalThis.xcTelemetryCheckpoint?.('download-done');
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
  // the engine's whole address space (capped by EmccMaximumHeapSize): telemetry samples it to tell memory exhaustion
  // apart from other crashes. wasmMemory.buffer is re-read each time, the HEAP views can be stale after a worker grew it.
  globalThis.xcWasmBytes = () => { try { return (rt.Module.wasmMemory?.buffer ?? rt.Module.HEAPU8.buffer).byteLength; } catch { return undefined; } };
  boot.stage('runtime', 'done', `.NET ${rt.runtimeBuildInfo?.productVersion || ''}`.trim());
  globalThis.xcTelemetryCheckpoint?.('runtime-created');
} catch (e) {
  console.error('[launch] runtime start failed', e);
  globalThis.xcTelemetryCheckpoint?.('runtime-failed');
  globalThis.xcTelemetryCrash?.('runtime-start', { text: String(e?.stack || e?.message || e) });
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
    processorOptions: { buffer, ring, ctrl, cap, voices: ctrlView[7] >>> 0 },
  });
  // direct voices read sample PCM anywhere in the wasm memory: when it grows, hand the larger view to the worklet
  const currentBuffer = () => (memory ? memory.buffer : (rt.Module.wasmMemory?.buffer ?? rt.Module.HEAPU8.buffer));
  let sentLength = buffer.byteLength;
  setInterval(() => {
    const b = currentBuffer();
    if (b.byteLength > sentLength) { sentLength = b.byteLength; node.port.postMessage({ buffer: b }); }
  }, 250);
  const voicesView = ctrlView[7] ? new Int32Array(buffer, ctrlView[7] >>> 0, 8 + 32 * 8 + 128) : null;
  // ?mute: silent output for test runs (the audio clock keeps running)
  if (query.has('mute')) { const silent = audioCtx.createGain(); silent.gain.value = 0; node.connect(silent).connect(audioCtx.destination); }
  else node.connect(audioCtx.destination);
  // Output latency = how far what the worklet has rendered is ahead of what is leaving the device. xcbass subtracts it
  // from the ring position, so the music position the game sees is the one being heard.
  // It used to be baseLatency + outputLatency + one quantum (10 + 40 + 2.7 = 52.7 ms on Chrome / Windows). Those are
  // round figures: measured against the browser's own output clock (getOutputTimestamp) the real distance averages
  // ~45 ms there, so the game's music clock ran ~8 ms behind the music. The measured value is used when the browser
  // provides a plausible one (see the 200 ms sampler below); the sum stays as the fallback.
  let measuredLatency = null, latencySamples = 0, storedLatency = -1;
  const updateLatency = () => {
    const rate = audioCtx.sampleRate;
    let frames = Math.round(((audioCtx.outputLatency || 0) + (audioCtx.baseLatency || 0)) * rate) + 128;
    if (measuredLatency != null && latencySamples >= 25 && !query.has('oldlatency')) frames = Math.round(measuredLatency * rate / 1000);
    // 1 ms of hysteresis: the music position must not jitter with the estimate
    if (storedLatency < 0 || Math.abs(frames - storedLatency) >= rate / 1000) { storedLatency = frames; Atomics.store(ctrlView, 3, frames); }
    Atomics.store(ctrlView, 6, audioCtx.state === 'running' ? 1 : 0);
  };
  updateLatency();
  setInterval(updateLatency, 500);
  globalThis.xcAudioDebug = () => ({ state: audioCtx.state, rate: audioCtx.sampleRate, read: ctrlView[0], write: ctrlView[1], cap: ctrlView[2], latency: ctrlView[3], running: ctrlView[6], underruns: ctrlView[5],
    target: ctrlView[15], lat: latencyReport });
  // ---- hit sound latency probe (see xcbass.c): the page stamps every key / button press, the mixer measures each
  // sample start. Reported per stage; "output" is what the browser reports for its own buffer + the device.
  globalThis.xcAudioMarkInput = (timeStamp) => {
    Atomics.store(ctrlView, 8, Math.round(((performance.timeOrigin + timeStamp) % 1e6) * 1000));
    Atomics.store(ctrlView, 9, 1);
  };
  let latencyReport = null;
  // Exact figures. The worklet logs the audio frame at which each direct voice starts; getOutputTimestamp() gives the
  // context time currently leaving the device and when, so "frame F is heard at ..." needs no latency estimate of ours.
  const heard = []; let logRead = 0;
  const session = []; // every start of the session (capped), for the median / P99
  const pct = (sorted, q) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * q))];
  const outLat = { sum: 0, n: 0 };
  const cadencePrev = [0, 0, 0, 0];
  setInterval(() => {
    if (!voicesView || audioCtx.state !== 'running') return;
    const ts = audioCtx.getOutputTimestamp();
    if (!ts || !ts.performanceTime) return;
    // output latency as the browser's clock sees it: what is being rendered now minus what is leaving the device now
    const leaving = ts.contextTime + (performance.now() - ts.performanceTime) / 1000;
    const ahead = (audioCtx.currentTime - leaving) * 1000;
    outLat.sum += ahead; outLat.n++;
    // single samples swing by one device buffer (the browser renders in bursts): only the average means something
    if (ahead > 3 && ahead < 400) {
      latencySamples++;
      measuredLatency = measuredLatency == null ? ahead : measuredLatency + (ahead - measuredLatency) * (latencySamples < 50 ? 1 / latencySamples : 0.02);
    }
    const written = Atomics.load(voicesView, 3);
    if (written - logRead > 64) logRead = written - 64;
    for (; logRead < written; logRead++) {
      const k = 8 + 32 * 8 + (logRead & 63) * 2;
      const reqUs = voicesView[k], frame = voicesView[k + 1] >>> 0;
      const heardAt = performance.timeOrigin + ts.performanceTime + (frame / audioCtx.sampleRate - ts.contextTime) * 1000;
      let d = (heardAt % 1e6) * 1000 - reqUs;
      if (d < -5e8) d += 1e9; else if (d > 5e8) d -= 1e9;
      if (d > -50000 && d < 500000) heard.push(d / 1000);
    }
  }, 200);
  const prev = [0, 0, 0, 0, 0, 0, 0];
  const logLatency = query.has('perf') || query.has('audiolat') || L.perfOverlay;
  setInterval(() => {
    const cur = [10, 11, 12, 13, 14].map((i) => Atomics.load(ctrlView, i)).concat(voicesView ? [Atomics.load(voicesView, 1), Atomics.load(voicesView, 2)] : [0, 0]);
    const d = cur.map((v, i) => (v - prev[i]) | 0);
    if (d[0] <= 0 && d[5] <= 0) return;
    cur.forEach((v, i) => { prev[i] = v; });
    const rate = audioCtx.sampleRate, r1 = (x) => Math.round(x * 10) / 10;
    const outFrames = Atomics.load(ctrlView, 3), out = outFrames / rate * 1000, outDirect = out;
    const input = d[2] > 0 ? r1(d[1] / d[2] / 1000) : null;
    const direct = d[5] > 0 ? { n: d[5], wait: r1(d[6] / d[5] / 1000) } : null;
    const ringed = d[0] > 0 ? { n: d[0], wait: r1(d[3] / d[0] / 1000), ring: r1(d[4] / d[0] / rate * 1000) } : null;
    latencyReport = { input, direct, ringed, out: r1(out), outDirect: r1(outDirect),
      directTotal: direct ? r1((input || 0) + direct.wait + outDirect) : null,
      ringTotal: ringed ? r1((input || 0) + ringed.wait + ringed.ring + out) : null,
      base: r1((audioCtx.baseLatency || 0) * 1000), device: r1((audioCtx.outputLatency || 0) * 1000), target: Atomics.load(ctrlView, 15) };
    if (logLatency) {
      const press = `press->request ${input == null ? '?' : input} ms (${d[2]} presses)`;
      const dev = `(browser buffer ${latencyReport.base} + device ${latencyReport.device})`;
      if (direct) console.log(`[audio] direct voices x${direct.n}: ${press} + request->block ${direct.wait} ms + output ${r1(outDirect)} ms ${dev} = ${latencyReport.directTotal} ms`);
      if (heard.length) {
        const h = heard.splice(0), avg = h.reduce((a, b) => a + b, 0) / h.length;
        session.push(...h); if (session.length > 20000) session.splice(0, session.length - 20000);
        const sorted = session.slice().sort((a, b) => a - b);
        const cad = [4, 5, 6, 7].map((i, j) => { const v = Atomics.load(voicesView, i), dd = v - cadencePrev[j]; cadencePrev[j] = v; return dd; });
        const calls = cad.reduce((a, b) => a + b, 0) || 1, pc = (x) => Math.round(x / calls * 100);
        const ol = outLat.n ? r1(outLat.sum / outLat.n) : null; outLat.sum = 0; outLat.n = 0;
        latencyReport.heard = { n: h.length, min: r1(Math.min(...h)), avg: r1(avg), max: r1(Math.max(...h)) };
        latencyReport.cadence = cad; latencyReport.outputByTimestamp = ol;
        // session figures. request->block = request->heard minus the output latency of the browser clock (a constant).
        const o = ol == null ? 0 : ol;
        latencyReport.session = { n: sorted.length, heardMedian: r1(pct(sorted, 0.5)), heardP99: r1(pct(sorted, 0.99)), heardMin: r1(sorted[0]), heardMax: r1(sorted[sorted.length - 1]),
          blockMedian: r1(pct(sorted, 0.5) - o), blockP99: r1(pct(sorted, 0.99) - o), musicCompensationError: r1(o - out) };
        console.log(`[audio] session x${sorted.length}: request->heard median ${latencyReport.session.heardMedian} ms, P99 ${latencyReport.session.heardP99} ms (min ${latencyReport.session.heardMin}, max ${latencyReport.session.heardMax})`
          + ` · request->block median ${latencyReport.session.blockMedian} ms, P99 ${latencyReport.session.blockP99} ms`
          + ` · music compensation error ${latencyReport.session.musicCompensationError} ms (browser clock ${ol} vs used ${r1(out)})`);
        console.log(`[audio] exact (output timestamp) x${h.length}: request->heard min ${latencyReport.heard.min} / avg ${latencyReport.heard.avg} / max ${latencyReport.heard.max} ms`
          + ` · worklet calls: ${pc(cad[0])}% back-to-back, ${pc(cad[1])}% after 2-5 ms, ${pc(cad[2])}% after 6-12 ms, ${pc(cad[3])}% later`
          + ` · output latency by the browser clock ${ol} ms (music is compensated with ${r1(out)} ms)`);
      }
      if (ringed) console.log(`[audio] ring path x${ringed.n}: ${press} + request->mixed ${ringed.wait} ms + ring ${ringed.ring} ms (target ${latencyReport.target} frames) + output ${r1(out)} ms ${dev} = ${latencyReport.ringTotal} ms`);
    }
  }, 5000);
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
    globalThis.xcTelemetryCheckpoint?.('gate-shown');
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
    }, L.fullscreen);
  },
  onAttached: () => {
    attachedAt = performance.now();
    globalThis.xcTelemetryCheckpoint?.('attached');
    say(`window attached to the engine (${canvas.width}x${canvas.height} px)`);
    // safety net: if the intro marker never shows up (log format change), don't keep the game hidden forever
    fallback = setTimeout(() => { if (!boot.done && !boot.gate) { say('no intro marker after 120 s, showing the game anyway'); boot.finish(); } }, 120000);
  },
  onHidden: flush,
  onFirstGesture: () => { globalThis.xcAudioResume?.(); },
  onExit: (code, msg) => {
    flush();
    if (code) {
      console.error('[launch] xc-osu! crashed:\n' + msg);
      globalThis.xcTelemetryCrash?.('exit', { code, text: msg });
      if (!boot.done) boot.fail(msg); else { logViewer.show(); }
    }
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
