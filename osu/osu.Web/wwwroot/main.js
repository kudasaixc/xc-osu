// xc-osu! bootstrap — xc softworks (kudasaixc)
import { dotnet } from './_framework/dotnet.js';
import { installUi } from './js/xc-ui.js?v=8209999e6f';

const $ = (id) => document.getElementById(id);
const status = (t) => { const el = $('xc-status'); if (el) el.textContent = t; console.log('[xc-osu!] ' + t); };

if (!crossOriginIsolated) {
  status('This page needs cross-origin isolation (COOP/COEP headers) to run.');
  throw new Error('not cross-origin isolated');
}

status('loading runtime…');

// URL switches: ?log ?perf ?diag (diagnostics) · ?mute (no audio output) · ?nursery=16m (GC young generation size)
// A/B switches for frame pacing work (each restores the pre-fix behaviour): ?legacyraf ?desync ?drawwait ?uncapped
const params = new URLSearchParams(location.search);
const flag = (name) => (params.has(name) ? '1' : '');
// Mono's default 4 MB nursery fills several times per second at osu!'s allocation rate, and every minor collection stops
// all threads (draw worker included). A larger nursery means proportionally fewer of those pauses.
const nursery = /^\d+[km]$/.test(params.get('nursery') || '') ? params.get('nursery') : '32m';

const rt = await dotnet
  .withConfig({ pthreadPoolInitialSize: 12, pthreadPoolUnusedSize: 4 })
  .withEnvironmentVariable('XC_ORIGIN', location.origin)
  .withEnvironmentVariable('XC_VERBOSE_LOG', flag('log'))
  .withEnvironmentVariable('XC_PERF', flag('perf'))
  .withEnvironmentVariable('XC_DIAG', flag('diag'))
  .withEnvironmentVariable('XC_DRAW_LEGACY', flag('legacyraf'))
  .withEnvironmentVariable('XC_DESYNC', flag('desync'))
  .withEnvironmentVariable('XC_DRAW_WAIT', flag('drawwait'))
  .withEnvironmentVariable('XC_UPDATE_UNCAPPED', flag('uncapped'))
  .withEnvironmentVariable('MONO_GC_PARAMS', `nursery-size=${nursery}`)
  .withResourceLoader((type, name, defaultUri) => defaultUri)
  .withModuleConfig({
    onDownloadResourceProgress: (loaded, total) => status(`downloading ${loaded}/${total}`),
  })
  .create();

const FS = rt.Module.FS;
const ROOT = '/xc';

// ---------------------------------------------------------------- persistent storage (IDBFS)
try { FS.mkdir(ROOT); } catch { }
let persistent = false;
try {
  FS.mount(rt.Module.IDBFS || FS.filesystems.IDBFS, { autoPersist: false }, ROOT);
  await new Promise((res) => FS.syncfs(true, (err) => { if (err) console.warn(err); res(); }));
  persistent = true;
  navigator.storage?.persist?.();
} catch (e) { console.warn('xc-osu!: persistent storage unavailable, running in memory', e); }

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
let audioGate = null;
function showAudioGate() { /* no prompt: audio starts silently on the first interaction (or immediately when installed as an app) */ }
globalThis.xcAudioResume = () => {
  if (!audioCtx || audioCtx.state === 'running') return;
  audioCtx.resume().catch(() => { });
};
globalThis.xcAudioInit = async (memory, ring, ctrl, cap, rate) => {
  if (audioCtx) return;
  const buffer = memory ? memory.buffer : (rt.Module.wasmMemory?.buffer ?? rt.Module.HEAPU8.buffer);
  if (ctrl + 64 > buffer.byteLength) { console.error('[xc-osu!] audio ring outside the visible heap', ctrl, buffer.byteLength); return; }
  const ctrlView = new Int32Array(buffer, ctrl, 16);
  audioCtx = new AudioContext({ sampleRate: rate, latencyHint: 'interactive' });
  await audioCtx.audioWorklet.addModule(new URL('./js/xc-audio-worklet.js?v=e862fb3f11', import.meta.url));
  const node = new AudioWorkletNode(audioCtx, 'xc-output', {
    numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2],
    processorOptions: { buffer, ring, ctrl, cap },
  });
  if (params.has('mute')) {
    // keep the audio clock running (the game syncs to it) but output silence
    const silent = audioCtx.createGain();
    silent.gain.value = 0;
    node.connect(silent).connect(audioCtx.destination);
  } else node.connect(audioCtx.destination);
  const updateLatency = () => {
    const secs = (audioCtx.outputLatency || 0) + (audioCtx.baseLatency || 0);
    // frames consumed by the worklet one render quantum before they reach the output stage
    Atomics.store(ctrlView, 3, Math.round(secs * audioCtx.sampleRate) + 128);
    Atomics.store(ctrlView, 6, audioCtx.state === 'running' ? 1 : 0);
  };
  updateLatency();
  setInterval(updateLatency, 500);
  globalThis.xcAudioDebug = () => ({ state: audioCtx.state, rate: audioCtx.sampleRate, read: ctrlView[0], write: ctrlView[1], cap: ctrlView[2], latency: ctrlView[3], running: ctrlView[6], underruns: ctrlView[5] });
  audioCtx.onstatechange = () => { updateLatency(); if (audioCtx.state !== 'running') showAudioGate(); };
  if (audioCtx.state !== 'running') { xcAudioResume(); setTimeout(showAudioGate, 300); }
  console.log(`[xc-osu!] audio: ${audioCtx.sampleRate} Hz, base latency ${(audioCtx.baseLatency * 1000).toFixed(1)} ms`);
};

// xcbass may start its output from a worker thread: it then posts the ring location here.
const audioChannel = new BroadcastChannel('xc-audio');
audioChannel.onmessage = (e) => {
  if (audioCtx) return;
  const { ring, ctrl, cap, rate } = e.data;
  globalThis.xcAudioInit(null, ring, ctrl, cap, rate).catch((err) => console.error('[xc-osu!] audio init failed', err));
};

// ---------------------------------------------------------------- UI bridge
const canvas = $('xc-canvas');
installUi(rt, canvas, {
  onAttached: () => { $('xc-splash')?.classList.add('gone'); },
  onHidden: flush,
  onFirstGesture: () => { globalThis.xcAudioResume?.(); },
  onExit: (code, msg) => { flush(); status(code ? 'xc-osu! crashed:\n' + msg : 'xc-osu! has exited.'); $('xc-splash')?.classList.remove('gone'); },
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
status('starting xc-osu!…');
await rt.runMain();
