// xc-osu! bootstrap — xc softworks (kudasaixc)
import { dotnet } from './_framework/dotnet.js';
import { installUi } from './js/xc-ui.js';

const $ = (id) => document.getElementById(id);
const status = (t) => { const el = $('xc-status'); if (el) el.textContent = t; console.log('[xc-osu!] ' + t); };

if (!crossOriginIsolated) {
  status('This page needs cross-origin isolation (COOP/COEP headers) to run.');
  throw new Error('not cross-origin isolated');
}

status('loading runtime…');

const rt = await dotnet
  .withConfig({ pthreadPoolInitialSize: 12, pthreadPoolUnusedSize: 4 })
  .withEnvironmentVariable('XC_ORIGIN', location.origin)
  .withEnvironmentVariable('XC_VERBOSE_LOG', new URLSearchParams(location.search).has('log') ? '1' : '')
  .withEnvironmentVariable('XC_PERF', new URLSearchParams(location.search).has('perf') ? '1' : '')
  .withEnvironmentVariable('XC_DIAG', new URLSearchParams(location.search).has('diag') ? '1' : '')
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
function showAudioGate() {
  if (audioGate || !audioCtx || audioCtx.state === 'running') return;
  audioGate = document.createElement('div');
  audioGate.id = 'xc-audio-gate';
  audioGate.textContent = 'click anywhere to enable sound';
  document.body.appendChild(audioGate);
}
globalThis.xcAudioResume = () => {
  if (!audioCtx || audioCtx.state === 'running') return;
  audioCtx.resume().then(() => { audioGate?.remove(); audioGate = null; }).catch(() => { });
};
globalThis.xcAudioInit = async (memory, ring, ctrl, cap, rate) => {
  const buffer = (memory ?? rt.Module.HEAPU8).buffer;
  const ctrlView = new Int32Array(buffer, ctrl, 16);
  audioCtx = new AudioContext({ sampleRate: rate, latencyHint: 'interactive' });
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
  audioCtx.onstatechange = () => { updateLatency(); if (audioCtx.state !== 'running') showAudioGate(); };
  if (audioCtx.state !== 'running') { xcAudioResume(); setTimeout(showAudioGate, 300); }
  console.log(`[xc-osu!] audio: ${audioCtx.sampleRate} Hz, base latency ${(audioCtx.baseLatency * 1000).toFixed(1)} ms`);
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
