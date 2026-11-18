// xc-osu! reproducible performance benchmark (xc softworks / kudasaixc)
//
// Runs the game's ?bench scenario (osu.Web/XcBench.cs) in headless Chromium against a served build, captures the engine
// telemetry (/api/xc/tm is intercepted, nothing reaches a server), samples the browser's real memory (PSS of every
// Chromium process of the run) and prints per-phase figures. Headless Chromium renders with SwiftShader on the CPU:
// draw figures are not comparable with real GPUs, update / GC / memory / boot figures are comparable between builds
// measured the same way on the same machine.
//
// usage: node --experimental-websocket tools/perfbench.mjs <base url> [--runs 3] [--bench full|short] [--env "K=V;K=V"]
//          [--opts '{"nursery":4}'] [--label name] [--out file.json] [--timeout 600] [--profile play-default --symbols f]
//          [--capture regex]
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const argv = process.argv.slice(2);
const base = argv[0] || 'http://127.0.0.1:8813';
const arg = (name, def) => { const i = argv.indexOf('--' + name); return i >= 0 ? argv[i + 1] : def; };
const runs = +arg('runs', 1), bench = arg('bench', 'full'), env = arg('env', ''), label = arg('label', base);
const opts = JSON.parse(arg('opts', '{}')), out = arg('out', null), timeoutS = +arg('timeout', 600);
// --profile <phase>: CPU-profile every worker (each .NET thread is one) during that phase; --symbols maps wasm function
// indices to names (obj/.../for-publish/dotnet.native.js.symbols of the measured build)
const profilePhase = arg('profile', null), symbolsFile = arg('symbols', null);
// --capture <regex>: count console lines matching it (e.g. "AOT NOT FOUND" with --env MONO_LOG_LEVEL=debug;MONO_LOG_MASK=aot)
const capture = arg('capture', null) ? new RegExp(arg('capture', null)) : null;
const logFile = arg('log', null); // every console line of the last run
const aotProfileOut = arg('aotprofile', null); // ?aotprofile: save the Mono AOT profile the build sends (XcAotProfile.cs)
const chrome = process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome';

// ---------------------------------------------------------------- process memory (PSS, shared pages split fairly)
function pss(tag) {
  const ps = execFileSync('ps', ['-eo', 'pid=,ppid=,args='], { encoding: 'utf8' }).split('\n').map(l => l.trim().match(/^(\d+)\s+(\d+)\s+(.*)$/)).filter(Boolean);
  const root = ps.find(m => m[3].includes(tag) && !m[3].includes('--type='));
  if (!root) return null;
  const kids = new Map();
  for (const m of ps) { if (!kids.has(m[2])) kids.set(m[2], []); kids.get(m[2]).push(m); }
  const all = [], stack = [root];
  while (stack.length) { const m = stack.pop(); all.push(m); for (const k of kids.get(m[1]) || []) stack.push(k); }
  let total = 0; const byType = {};
  for (const m of all) {
    let kb = 0;
    try { kb = +(/^Pss:\s+(\d+)/m.exec(readFileSync(`/proc/${m[1]}/smaps_rollup`, 'utf8'))?.[1] || 0); } catch { }
    const type = /--type=([a-z-]+)/.exec(m[3])?.[1] || 'browser';
    byType[type] = (byType[type] || 0) + kb;
    total += kb;
  }
  return { total: Math.round(total / 1024), ...Object.fromEntries(Object.entries(byType).map(([k, v]) => [k, Math.round(v / 1024)])) };
}


// ---------------------------------------------------------------- raw CDP (flattened sessions reach the workers)
async function cdpConnect(port) {
  const ver = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  const ws = new WebSocket(ver.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let id = 0; const pending = new Map(); const workers = new Map();
  const send = (method, params = {}, sessionId) => new Promise((res, rej) => {
    const m = { id: ++id, method, params }; if (sessionId) m.sessionId = sessionId;
    pending.set(m.id, { res, rej }); ws.send(JSON.stringify(m));
  });
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); return; }
    if (m.method === 'Target.attachedToTarget') {
      const { sessionId, targetInfo } = m.params;
      if (targetInfo.type === 'page') send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true }, sessionId).catch(() => { });
      else if (targetInfo.type === 'worker') { workers.set(sessionId, targetInfo); send('Profiler.enable', {}, sessionId).catch(() => { }); }
    }
    if (m.method === 'Target.detachedFromTarget') workers.delete(m.params.sessionId);
  };
  await send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
  return {
    workers, close: () => ws.close(),
    async start() { for (const s of workers.keys()) { await send('Profiler.setSamplingInterval', { interval: 250 }, s).catch(() => { }); await send('Profiler.start', {}, s).catch(() => { }); } },
    async stop() {
      const out = [];
      for (const [s, info] of workers) { try { const { profile } = await send('Profiler.stop', {}, s); out.push({ url: info.url, title: info.title, profile }); } catch { } }
      return out;
    },
  };
}

function profileReport(profiles, symbols) {
  const name = (cf) => {
    let f = cf.functionName || '(anonymous)';
    const m = /^(?:\$func|wasm-function\[)(\d+)\]?$/.exec(f) || /^\$?(\d+)$/.exec(f);
    if (m && symbols) f = symbols[+m[1]] || f;
    return f;
  };
  const reports = [];
  for (const { profile } of profiles) {
    const dt = (profile.endTime - profile.startTime) / 1000;
    const self = new Map(); let total = 0;
    const byId = new Map(profile.nodes.map(n => [n.id, n]));
    for (const n of profile.nodes) { const k = name(n.callFrame); self.set(k, (self.get(k) || 0) + (n.hitCount || 0)); total += n.hitCount || 0; }
    const idle = ['(idle)', '(program)', '(garbage collector)'].reduce((a, k) => a + (self.get(k) || 0), 0)
      + [...self].filter(([k]) => /futex|Atomics\.wait|emscripten_thread_sleep|__timedwait|sleep/i.test(k)).reduce((a, [, v]) => a + v, 0);
    // inclusive time of selected roots: walk parents
    const parent = new Map(); for (const n of profile.nodes) for (const c of n.children || []) parent.set(c, n.id);
    const incl = new Map();
    for (const n of profile.nodes) {
      if (!n.hitCount) continue;
      const seen = new Set(); let cur = n.id;
      while (cur != null) { const k = name(byId.get(cur).callFrame); if (!seen.has(k)) { seen.add(k); incl.set(k, (incl.get(k) || 0) + n.hitCount); } cur = parent.get(cur); }
    }
    const busy = total - idle;
    const top = [...self].filter(([k]) => !['(idle)', '(program)', '(root)'].includes(k)).sort((a, b) => b[1] - a[1]).slice(0, 45)
      .map(([k, v]) => [k.slice(0, 140), Math.round(v / Math.max(1, busy) * 1000) / 10]);
    const topIncl = [...incl].filter(([k]) => !['(root)'].includes(k)).sort((a, b) => b[1] - a[1]).slice(0, 60)
      .map(([k, v]) => [k.slice(0, 140), Math.round(v / Math.max(1, busy) * 1000) / 10]);
    reports.push({ seconds: Math.round(dt / 100) / 10, samples: total, busySamples: busy, busyPct: Math.round(busy / Math.max(1, total) * 100), top, topIncl });
  }
  return reports.sort((a, b) => b.busySamples - a.busySamples);
}

// ---------------------------------------------------------------- one run
async function run(n) {
  const tag = 'xcbench' + Date.now() + n;
  const port = 9300 + n;
  let prof = null, profiles = null, captured = new Map();
  const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required', '--enable-blink-features=ForceEagerMeasureMemory', `--xc-bench-tag=${tag}`, ...(profilePhase ? [`--remote-debugging-port=${port}`] : [])] });
  if (profilePhase) prof = await cdpConnect(port);
  const items = [], marks = [], logs = [], errors = [], mem = [], all = [], aotChunks = [];
  let t0 = Date.now(), done = false;
  try {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 720 } });
    await ctx.addInitScript((o) => { try { localStorage.setItem('xc.launcher.v1', JSON.stringify(o)); } catch { } },
      { autoLaunch: true, startGate: false, fullscreen: false, telemetry: true, rev: 2, ...opts });
    await ctx.route('**/api/xc/tm/session', (route) => route.fulfill({ contentType: 'application/json', body: '{}' }));
    await ctx.route('**/api/xc/tm', async (route) => {
      const b = JSON.parse(route.request().postData());
      items.push(...b.items);
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ seq: Math.max(0, ...b.items.map(i => i.q)) }) });
    });
    const page = await ctx.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('console', (m) => {
      const t = m.text();
      if (logFile && all.length < 200000) all.push(((Date.now() - t0) / 1000).toFixed(2) + ' ' + t);
      if (t.startsWith('[aotprof] ')) {
        const m = /^\[aotprof\] (\d+)\/(\d+) (.*)$/.exec(t);
        if (m) aotChunks[+m[1]] = m[3];
        else if (t.startsWith('[aotprof] end') && aotProfileOut) {
          const buf = Buffer.from(aotChunks.join(''), 'base64');
          writeFileSync(aotProfileOut, buf);
          console.log(`  [${n}] AOT profile saved: ${buf.length} bytes (${t.slice(14)} announced) -> ${aotProfileOut}`);
        }
        return;
      }
      if (capture && capture.test(t)) { const k = t.slice(0, 220); captured.set(k, (captured.get(k) || 0) + 1); }
      if (t.startsWith('[bench] ')) {
        try {
          const j = JSON.parse(t.slice(8)); marks.push(j);
          console.log(`  [${n}] ${j.phase}${j.map ? ' ' + j.map + ' (' + j.length + ' s)' : ''}`);
          if (j.phase === 'done' || j.phase === 'failed') done = true;
          if (prof && j.phase === profilePhase) prof.start().then(() => console.log(`  [${n}] profiling ${prof.workers.size} workers`));
          else if (prof && marks.length > 1 && marks[marks.length - 2].phase === profilePhase) prof.stop().then((p) => { profiles = p; console.log(`  [${n}] profile taken`); });
        } catch { }
      }
      else if (/\[launch\]|\[respack\]|fatal|Exception|crash|error/i.test(t)) logs.push({ at: Date.now(), t: t.slice(0, 400) });
    });
    const q = '?launch&mute&bench=' + encodeURIComponent(bench) + (env ? '&env=' + encodeURIComponent(env) : '') + (aotProfileOut ? '&aotprofile' : '');
    t0 = Date.now();
    await page.goto(base + '/' + q);
    await page.mouse.move(4, 4);
    const timer = setInterval(() => { const p = pss(tag); if (p) mem.push({ at: Date.now(), ...p }); }, 2000);
    while (!done && Date.now() - t0 < timeoutS * 1000) {
      await page.waitForTimeout(2000);
      if (errors.some(e => /fatal|abort|RuntimeError/i.test(e))) break;
    }
    // let the last telemetry window reach the interceptor
    await page.waitForTimeout(done ? 12000 : 1000);
    clearInterval(timer);
  } finally { prof?.close(); await browser.close(); if (logFile) writeFileSync(logFile, all.join('\n')); }
  return { n, t0, done, items, marks, logs, errors, mem, profiles, captured: [...captured].sort((a, b) => b[1] - a[1]) };
}

// ---------------------------------------------------------------- per-phase figures
const sum = (a, f) => a.reduce((s, x) => s + (f(x) || 0), 0);
const max = (a, f) => a.reduce((m, x) => Math.max(m, f(x) || 0), 0);
const r = (x, d = 1) => Math.round(x * 10 ** d) / 10 ** d;

function summarise(res) {
  const windows = res.items.filter(i => i.k === 'w');
  const spikes = res.items.filter(i => i.k === 's').flatMap(i => i.sp || []);
  const events = res.items.filter(i => i.k === 'e');
  const marks = res.marks;
  const phases = [];
  for (let i = 0; i < marks.length; i++) {
    const a = marks[i].at, b = i + 1 < marks.length ? marks[i + 1].at : a + 15000;
    if (['done', 'failed'].includes(marks[i].phase)) continue;
    const ws = windows.filter(w => { const mid = w.at - w.dur / 2; return mid >= a && mid < b; });
    const sp = spikes.filter(s => s.at >= a && s.at < b);
    const dur = sum(ws, w => w.dur) || 1;
    const th = (name, f) => sum(ws, w => f(w.th?.[name] || {}));
    const gcSp = sp.filter(s => s.gp > 0);
    const minor = gcSp.filter(s => !s.g1).map(s => s.gp).sort((x, y) => x - y), major = gcSp.filter(s => s.g1).map(s => s.gp).sort((x, y) => x - y);
    const pct = (arr, p) => arr.length ? arr[Math.min(arr.length - 1, Math.floor(arr.length * p))] : null;
    const memIn = res.mem.filter(m => m.at >= a && m.at < b);
    phases.push({
      phase: marks[i].phase, seconds: r((b - a) / 1000), windows: ws.length,
      update: { hz: r(th('update', t => t.n) / dur * 1000), msPerFrame: r(th('update', t => t.ws) / (th('update', t => t.n) || 1), 2), busyPct: r(th('update', t => t.ws) / dur * 100), maxMs: r(max(ws, w => w.th?.update?.wm)), allocMBs: r(th('update', t => t.am) / dur * 1000, 2) },
      draw: { msPerFrame: r(th('draw', t => t.ws) / (th('draw', t => t.n) || 1), 2), late: th('draw', t => t.late), allocMBs: r(th('draw', t => t.am) / dur * 1000, 2) },
      audio: { busyPct: r(th('audio', t => t.ws) / dur * 100), hz: r(th('audio', t => t.n) / dur * 1000), allocMBs: r(th('audio', t => t.am) / dur * 1000, 2) },
      gc: { minor: sum(ws, w => w.gc?.c0) - sum(ws, w => w.gc?.c1), major: sum(ws, w => w.gc?.c1), pauseMs: r(sum(ws, w => w.gc?.pause)), allocMBs: r(sum(ws, w => w.gc?.alloc) / dur * 1000, 2), heapMax: r(max(ws, w => w.gc?.heap)),
        minorP50: pct(minor, 0.5), minorMax: minor.length ? minor[minor.length - 1] : null, majorP50: pct(major, 0.5), majorMax: major.length ? major[major.length - 1] : null },
      spikes: { update: sp.filter(s => s.th === 'update' && s.kd === 'slow').length, updateMax: r(max(sp.filter(s => s.th === 'update'), s => s.ms)), drawLate: sp.filter(s => s.th === 'draw').length },
      mem: { wasm: max(ws, w => w.mem?.wasm || w.js?.wasm / 1048576), sbrk: max(ws, w => w.mem?.sbrk), mallocUsed: max(ws, w => w.mem?.used), mallocArena: max(ws, w => w.mem?.arena), jsHeap: r(max(ws, w => (w.js?.heap || 0) / 1048576)), pssMax: max(memIn, m => m.total), rendererPss: max(memIn, m => m.renderer) },
      res: { reads: sum(ws, w => w.res?.reads), mb: r(sum(ws, w => w.res?.mb)), maxMs: r(max(ws, w => w.res?.max)), fg: sum(ws, w => w.res?.fg) },
    });
  }
  const cp = {};
  for (const e of events.filter(e => e.n === 'boot-checkpoint')) cp[e.name] ??= e.at;
  const boot = {};
  for (const [k, v] of Object.entries(cp)) boot[k] = r((v - res.t0) / 1000, 2);
  const menu = marks.find(m => m.phase === 'menu-idle');
  if (menu) boot.menu = r((menu.at - res.t0) / 1000, 2);
  return { run: res.n, done: res.done, boot, pssMax: max(res.mem, m => m.total), phases, errors: res.errors.slice(0, 10), logs: res.logs.filter(l => /resource pack|fatal|Exception|respack/i.test(l.t)).slice(0, 20).map(l => l.t) };
}

const results = [];
for (let i = 1; i <= runs; i++) {
  console.log(`run ${i}/${runs} ${label}`);
  const res = await run(i);
  const s = summarise(res);
  const symbols = symbolsFile ? Object.fromEntries(readFileSync(symbolsFile, 'utf8').split('\n').filter(Boolean).map(l => { const i = l.indexOf(':'); return [+l.slice(0, i), l.slice(i + 1)]; })) : null;
  if (res.profiles) {
    s.profile = profileReport(res.profiles, symbols);
    for (const w of s.profile.slice(0, 4)) {
      console.log(`  profile worker: ${w.busyPct}% busy over ${w.seconds}s; self: ` + w.top.slice(0, 12).map(([k, v]) => `${v}% ${k}`).join(' | '));
    }
  }
  if (res.captured.length) { s.captured = res.captured.slice(0, 400); console.log(`  captured ${res.captured.length} distinct lines (${res.captured.reduce((a, [, v]) => a + v, 0)} total)`); }
  results.push({ summary: s, raw: { items: res.items, marks: res.marks, mem: res.mem } });
  console.log(`  boot ${JSON.stringify(s.boot)} · PSS max ${s.pssMax} MB · ${s.done ? 'complete' : 'INCOMPLETE'}${s.errors.length ? ' · errors: ' + s.errors.join(' | ').slice(0, 300) : ''}`);
  for (const p of s.phases)
    console.log(`  ${p.phase.padEnd(13)} upd ${p.update.hz}Hz ${p.update.msPerFrame}ms ${p.update.busyPct}% max ${p.update.maxMs} · draw ${p.draw.msPerFrame}ms · gc ${p.gc.minor}/${p.gc.major} pause ${p.gc.pauseMs} alloc ${p.gc.allocMBs}MB/s heap ${p.gc.heapMax} · wasm ${p.mem.wasm} sbrk ${p.mem.sbrk} used ${p.mem.mallocUsed} js ${p.mem.jsHeap} pss ${p.mem.pssMax} · audio ${p.audio.busyPct}% · res ${p.res.reads}/${p.res.mb}MB fg ${p.res.fg}`);
}
if (out) writeFileSync(out, JSON.stringify({ label, base, bench, env, opts, when: new Date().toISOString(), results }, null, 1));
