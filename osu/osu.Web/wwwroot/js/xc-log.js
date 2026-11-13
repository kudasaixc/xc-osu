// xc-osu! console capture: xc softworks (kudasaixc)
// Everything the page prints (the loader, the .NET runtime on every worker thread, the game's Logger, realm-core)
// ends up in console.* on the UI thread. We keep a copy so the launch screen and the in-game log viewer can show the
// real output, without changing what devtools sees.

const MAX_LINES = 20000;
const t0 = performance.now();

export const xcLog = {
  lines: [],          // { t, level, text }
  listeners: new Set(),
  t0,
  on(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); },
  text() {
    return this.lines.map((l) => `[${fmtTime(l.t)}] ${l.level === 'log' ? '' : l.level.toUpperCase() + ' '}${l.text}`).join('\n');
  },
  save() {
    const blob = new Blob([this.text()], { type: 'text/plain' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `xc-osu-${new Date().toISOString().replace(/[:.]/g, '-')}.log`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 30000);
  },
};

export const fmtTime = (t) => '+' + (t / 1000).toFixed(3).padStart(8, ' ') + 's';

function stringify(a) {
  if (typeof a === 'string') return a;
  if (a instanceof Error) return a.stack || String(a);
  try { return typeof a === 'object' ? JSON.stringify(a) : String(a); } catch { return String(a); }
}

function push(level, args) {
  let text = args.map(stringify).join(' ');
  // console format specifiers (%c etc.) carry no meaning outside devtools
  if (typeof args[0] === 'string' && args[0].includes('%c')) text = args[0].replace(/%c/g, '');
  const t = performance.now() - t0;
  for (const part of text.split('\n')) {
    const line = { t, level, text: part };
    if (!part.startsWith('[tm] ')) xcLog.lines.push(line);
    for (const fn of xcLog.listeners) { try { fn(line); } catch { } }
  }
  if (xcLog.lines.length > MAX_LINES) xcLog.lines.splice(0, xcLog.lines.length - MAX_LINES);
}

let installed = false;
export function captureConsole() {
  if (installed) return;
  installed = true;
  for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
    const orig = console[level].bind(console);
    console[level] = (...args) => { if (!String(args[0]).startsWith('[tm] ')) orig(...args); push(level === 'info' || level === 'debug' ? 'log' : level, args); };
  }
  addEventListener('error', (e) => push('error', ['uncaught: ' + (e.error?.stack || e.message)]));
  addEventListener('unhandledrejection', (e) => push('error', ['unhandled rejection: ' + stringify(e.reason)]));
}
