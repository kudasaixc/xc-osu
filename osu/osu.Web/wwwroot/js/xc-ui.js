// xc-osu! UI thread bridge: xc softworks (kudasaixc)
// Owns the DOM: captures input at full device rate and writes it into a shared-memory ring that the
// osu-framework input thread drains every frame. Never calls into .NET synchronously.
import { XC_KEY_CODES } from './keys.js';

const S = {
  MAGIC: 0, WIDTH: 1, HEIGHT: 2, DPR_MILLI: 3, FOCUSED: 4, VISIBLE: 5, CURSOR_INSIDE: 6, POINTER_LOCKED: 7,
  FULLSCREEN: 8, REFRESH_MILLIHZ: 9, CAPS_LOCK: 10, SCREEN_W: 11, SCREEN_H: 12, RING_CAP: 13, WRITE_INDEX: 14,
  READ_INDEX: 15, KEYBOARD_ATTACHED: 16, CLIPBOARD_SEQ: 17, CLIPBOARD_START: 18, CLIPBOARD_LEN: 19, START_GATE: 20,
};
const HEADER_INTS = 32, EVENT_INTS = 8, TEXT_CAPACITY = 1 << 17;

const PEN = { Contact: 1, InRange: 2, Raw: 4, Eraser: 8, Direct: 16 };

const keyIndex = new Map(XC_KEY_CODES.map((c, i) => [c, i]));

export function installUi(rt, canvas, hooks) {
  const Module = rt.Module;
  let base = 0, ringCap = 0, textPos = 0, attached = false;
  const pending = [];

  // views must be refreshed if wasm memory grows (new SharedArrayBuffer view)
  let i32 = null, f32 = null, u16 = null, buf = null;
  function views() {
    const b = Module.HEAP8 ? Module.HEAP8.buffer : rt.localHeapViewU8().buffer;
    if (b !== buf || !i32) { buf = b; i32 = new Int32Array(b); f32 = new Float32Array(b); u16 = new Uint16Array(b); }
  }
  const slot = (s, v) => { views(); Atomics.store(i32, (base >> 2) + s, v | 0); };

  const tmpF = new Float32Array(1), tmpI = new Int32Array(tmpF.buffer);
  const fb = (x) => { tmpF[0] = x; return tmpI[0]; };

  function push(type, a = 0, b = 0, c = 0, d = 0, e = 0, f = 0, g = 0) {
    if (!attached) { pending.push([type, a, b, c, d, e, f, g]); return; }
    views();
    const hi = base >> 2;
    const w = Atomics.load(i32, hi + S.WRITE_INDEX);
    const r = Atomics.load(i32, hi + S.READ_INDEX);
    if (w - r >= ringCap) return; // ring full: drop (input thread stalled)
    const p = hi + HEADER_INTS + (w % ringCap) * EVENT_INTS;
    i32[p] = type; i32[p + 1] = a; i32[p + 2] = b; i32[p + 3] = c; i32[p + 4] = d; i32[p + 5] = e; i32[p + 6] = f; i32[p + 7] = g;
    Atomics.store(i32, hi + S.WRITE_INDEX, w + 1);
  }

  function writeText(str) {
    views();
    const t = (base + (HEADER_INTS + ringCap * EVENT_INTS) * 4) >> 1;
    const start = textPos;
    for (let i = 0; i < str.length; i++) u16[t + ((textPos + i) % TEXT_CAPACITY)] = str.charCodeAt(i);
    textPos = (textPos + str.length) % TEXT_CAPACITY;
    return [start, str.length];
  }

  // ------------------------------------------------------------------ geometry
  // render resolution = CSS size x devicePixelRatio x launcher scale (the browser upscales the canvas when < 1)
  const renderScale = Math.min(2, Math.max(0.25, +globalThis.xcRenderScale || 1));
  function renderDpr() { const scaled = (devicePixelRatio || 1) * renderScale; return globalThis.xcIosCompat ? Math.min(1, scaled) : scaled; }
  let dpr = renderDpr(), pixW = 0, pixH = 0;
  function publishSize(w, h) {
    pixW = Math.max(1, w | 0); pixH = Math.max(1, h | 0);
    slot(S.WIDTH, pixW); slot(S.HEIGHT, pixH); slot(S.DPR_MILLI, Math.round(dpr * 1000));
    slot(S.SCREEN_W, Math.round(screen.width * dpr)); slot(S.SCREEN_H, Math.round(screen.height * dpr));
  }
  const ro = new ResizeObserver((entries) => {
    const e = entries[0];
    dpr = renderDpr();
    if (e.devicePixelContentBoxSize && renderScale === 1) publishSize(e.devicePixelContentBoxSize[0].inlineSize, e.devicePixelContentBoxSize[0].blockSize);
    else publishSize(e.contentRect.width * dpr, e.contentRect.height * dpr);
  });
  try { ro.observe(canvas, { box: 'device-pixel-content-box' }); } catch { ro.observe(canvas); }

  // refresh rate: median rAF interval, re-measured on screen change
  function measureRefresh() {
    const t = []; let last = 0;
    const step = (now) => {
      if (last) t.push(now - last);
      last = now;
      if (t.length < 90) requestAnimationFrame(step);
      else { t.sort((a, b) => a - b); const hz = 1000 / t[t.length >> 1]; slot(S.REFRESH_MILLIHZ, Math.round(hz * 1000)); }
    };
    requestAnimationFrame(step);
  }
  matchMedia(`(resolution: ${devicePixelRatio || 1}dppx)`).addEventListener?.('change', () => { dpr = renderDpr(); measureRefresh(); });

  // ------------------------------------------------------------------ coordinates
  const rectCache = { r: null };
  const rect = () => rectCache.r || (rectCache.r = canvas.getBoundingClientRect());
  addEventListener('resize', () => { rectCache.r = null; });
  addEventListener('scroll', () => { rectCache.r = null; }, { passive: true });
  const px = (e) => { const r = rect(); return [(e.clientX - r.left) * (pixW / r.width), (e.clientY - r.top) * (pixH / r.height)]; };

  // ------------------------------------------------------------------ user activation helpers
  let wantLock = false, wantFullscreen = -1;
  const lockOpts = { unadjustedMovement: true };
  function tryLock() {
    if (!wantLock || !canvas.requestPointerLock || globalThis.xcIosCompat || document.pointerLockElement === canvas) return;
    try {
      const p = canvas.requestPointerLock(lockOpts);
      p?.catch(error => {
        // Retry only unsupported raw movement; denial needs a fresh user gesture.
        if (error?.name !== 'NotSupportedError') return;
        try { canvas.requestPointerLock()?.catch(() => {}); } catch { }
      });
    } catch { }
  }
  function tryFullscreen() {
    try {
      if (wantFullscreen === 1) {
        // a refused request (no user activation yet) stays pending and is retried on the next key press / click
        if (globalThis.xcIosCompat || document.fullscreenElement || !document.documentElement.requestFullscreen) wantFullscreen = -1;
        else document.documentElement.requestFullscreen({ navigationUI: 'hide' })?.then(() => { wantFullscreen = -1; }, () => { });
      } else if (wantFullscreen === 0) {
        if (document.fullscreenElement) document.exitFullscreen?.()?.catch(() => { });
        wantFullscreen = -1;
      }
    } catch { wantFullscreen = -1; /* Unsupported browser APIs must not interrupt input or audio unlock. */ }
  }
  function onGesture() { tryLock(); if (wantFullscreen >= 0) tryFullscreen(); audioUnlock(); }

  // ------------------------------------------------------------------ held keys / buttons
  // The browser never delivers the key-up of a key that is still held when the page loses focus (Alt+Tab, leaving
  // fullscreen, switching tabs, OS shortcuts). The engine would keep it pressed forever: a stuck Alt turns every scroll
  // and arrow key into a volume change and breaks every shortcut. Track what is down and release it ourselves.
  const downKeys = new Set(), downButtons = new Set();
  function releaseAll() {
    for (const k of downKeys) push(7, k);
    downKeys.clear();
    for (const b of downButtons) push(4, b);
    downButtons.clear();
  }
  const MODIFIERS = [['altKey', 'AltLeft', 'AltRight'], ['ctrlKey', 'ControlLeft', 'ControlRight'], ['shiftKey', 'ShiftLeft', 'ShiftRight'], ['metaKey', 'MetaLeft', 'MetaRight']]
    .map(([flag, ...codes]) => [flag, codes.map((c) => keyIndex.get(c)).filter((k) => k !== undefined)]);
  // every keyboard / mouse / wheel event carries the real modifier state: drop modifiers the engine still believes held
  function syncModifiers(e) {
    if (!downKeys.size) return;
    for (const [flag, keys] of MODIFIERS) {
      if (e[flag] !== false) continue;
      for (const k of keys) if (downKeys.delete(k)) push(7, k);
    }
  }
  let audioUnlocked = false;
  function audioUnlock() { audioUnlocked = true; hooks.onFirstGesture?.(); }

  // ------------------------------------------------------------------ pointer (mouse / pen / touch)
  const locked = () => document.pointerLockElement === canvas;
  const touchIds = new Map();
  let tabletAnnounced = false, penButtons = 0, penInRange = false;

  function penSample(e, flagsExtra) {
    if (!tabletAnnounced) {
      tabletAnnounced = true;
      const [s, l] = writeText('Pen tablet (OS mapping)');
      push(25, s, l, fb(0), fb(0));
    }
    const [x, y] = px(e);
    const sw = screen.width || innerWidth, sh = screen.height || innerHeight;
    const nx = Math.min(1, Math.max(0, e.screenX / sw)), ny = Math.min(1, Math.max(0, e.screenY / sh));
    let b = 0;
    if (e.buttons & 1) b |= 1;
    if (e.buttons & 2) b |= 2;      // barrel 1
    if (e.buttons & 4) b |= 4;      // barrel 2 (middle)
    let flags = PEN.InRange | flagsExtra;
    if (e.buttons & 32) flags |= PEN.Eraser;
    if ((e.buttons & 1) || (e.buttons & 32)) flags |= PEN.Contact;
    penButtons = b;
    push(12, fb(x), fb(y), fb(e.pressure || ((flags & PEN.Contact) ? 0.5 : 0)), fb(nx), fb(ny), b, flags);
  }

  const directPen = matchMedia('(pointer: coarse)').matches && navigator.maxTouchPoints > 0;

  function onMove(e) {
    if (e.pointerType === 'pen') {
      const list = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
      if (list && list.length) for (const c of list) penSample(c, directPen ? PEN.Direct : 0);
      else penSample(e, directPen ? PEN.Direct : 0);
      penInRange = true;
      return;
    }
    if (e.pointerType === 'touch') {
      const id = touchIds.get(e.pointerId);
      if (id !== undefined) { const [x, y] = px(e); push(10, id, fb(x), fb(y)); }
      return;
    }
    if (locked()) {
      // raw relative motion (unadjustedMovement = no OS acceleration), in device pixels
      const list = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
      if (list && list.length > 1) for (const c of list) push(2, fb(c.movementX * dpr), fb(c.movementY * dpr));
      else push(2, fb(e.movementX * dpr), fb(e.movementY * dpr));
      return;
    }
    const list = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
    if (list && list.length > 1) for (const c of list) { const [x, y] = px(c); push(1, fb(x), fb(y)); }
    else { const [x, y] = px(e); push(1, fb(x), fb(y)); }
  }

  // pointerrawupdate fires at device rate before rAF alignment (Chromium); fall back to pointermove
  const moveEvent = 'onpointerrawupdate' in window ? 'pointerrawupdate' : 'pointermove';
  // Touch uses pointermove only: Chromium also emits pointerrawupdate for the same sample.
  canvas.addEventListener(moveEvent, (e) => { if (moveEvent === 'pointermove' || e.pointerType !== 'touch') onMove(e); });
  if (moveEvent !== 'pointermove') canvas.addEventListener('pointermove', (e) => { if (e.pointerType === 'touch') onMove(e); });

  canvas.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    globalThis.xcAudioMarkInput?.(e.timeStamp);
    onGesture();
    syncModifiers(e);
    if (e.pointerType === 'pen') { try { canvas.setPointerCapture(e.pointerId); } catch { } penSample(e, directPen ? PEN.Direct : 0); return; }
    if (e.pointerType === 'touch') {
      let id = 0; const used = new Set(touchIds.values());
      while (used.has(id) && id < 9) id++;
      touchIds.set(e.pointerId, id);
      const [x, y] = px(e); push(9, id, fb(x), fb(y));
      return;
    }
    if (!locked()) {
      try { canvas.setPointerCapture(e.pointerId); } catch { }
      const [x, y] = px(e); push(1, fb(x), fb(y));
    }
    downButtons.add(e.button);
    push(3, e.button);
  });

  const up = (e) => {
    if (e.pointerType === 'pen') { penSample(e, directPen ? PEN.Direct : 0); return; }
    if (e.pointerType === 'touch') {
      const id = touchIds.get(e.pointerId);
      if (id !== undefined) { touchIds.delete(e.pointerId); const [x, y] = px(e); push(11, id, fb(x), fb(y)); }
      return;
    }
    downButtons.delete(e.button);
    push(4, e.button);
  };
  canvas.addEventListener('pointerup', up);
  canvas.addEventListener('pointercancel', up);
  canvas.addEventListener('pointerleave', (e) => {
    if (e.pointerType === 'pen' && penInRange) {
      penInRange = false;
      const [x, y] = px(e);
      push(12, fb(x), fb(y), fb(0), fb(e.screenX / screen.width), fb(e.screenY / screen.height), 0, 0);
    }
    if (e.pointerType === 'mouse' && !locked()) push(16, 0);
  });
  canvas.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') push(16, 1); });

  // mouse buttons other than pointerdown's first button (chorded presses come as pointermove with button != -1)
  canvas.addEventListener('pointermove', (e) => {
    if (e.pointerType !== 'mouse' || e.button < 0) return;
    if (e.buttons & (1 << [0, 2, 1, 3, 4][e.button])) { downButtons.add(e.button); push(3, e.button); }
    else { downButtons.delete(e.button); push(4, e.button); }
  });

  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    syncModifiers(e);
    // the scroll must land on what is under the cursor even if no move event preceded it
    if (!locked()) { const [x, y] = px(e); push(1, fb(x), fb(y)); }
    let dx = -e.deltaX, dy = -e.deltaY, precise = 0;
    if (e.deltaMode === 0) {
      // A mouse wheel notch is 100 px only at 100 % zoom / scaling (125, 133.33... otherwise), so the pixel delta cannot
      // tell a wheel from a touchpad. The legacy wheelDelta stays a multiple of 120 per notch on every browser that has it.
      // (touchpads can also land on a multiple of 120 with a small pixel delta, hence the size check)
      const notches = e.wheelDeltaY !== undefined && e.deltaX === 0 && e.wheelDeltaY !== 0 && e.wheelDeltaY % 120 === 0 && Math.abs(e.deltaY) >= 80;
      if (notches) { dx = 0; dy = e.wheelDeltaY / 120; }
      else { dx /= 100; dy /= 100; precise = 1; }
    }
    else if (e.deltaMode === 1) { dx /= 3; dy /= 3; } // lines (Firefox mouse wheel): 3 lines per notch
    push(5, fb(dx), fb(dy), precise);
  }, { passive: false });

  document.addEventListener('pointerlockchange', () => {
    slot(S.POINTER_LOCKED, locked() ? 1 : 0);
    if (locked()) push(16, 1);
  });
  // Escape belongs to the game (pause, back): while fullscreen, ask the browser to deliver it instead of leaving
  // fullscreen (Keyboard Lock, Chromium; holding Escape still exits). F11 / Alt+Enter toggle fullscreen from the game.
  function onFullscreenChange() {
    const on = !!document.fullscreenElement;
    rectCache.r = null;
    wantFullscreen = -1;
    slot(S.FULLSCREEN, on ? 1 : 0);
    try {
      if (on) navigator.keyboard?.lock?.(['Escape'])?.catch(() => { });
      else navigator.keyboard?.unlock?.();
    } catch { }
    // the browser drops pointer lock together with fullscreen and may swallow key-ups during the transition
    releaseAll();
    if (wantLock) setTimeout(tryLock, 100);
  }
  document.addEventListener('fullscreenchange', onFullscreenChange);

  // ------------------------------------------------------------------ keyboard + text
  const hiddenInput = document.createElement('textarea');
  hiddenInput.setAttribute('autocomplete', 'off'); hiddenInput.setAttribute('autocapitalize', 'off'); hiddenInput.spellcheck = false;
  hiddenInput.style.cssText = 'position:fixed;left:-1000px;top:0;width:1px;height:1px;opacity:0;';
  document.body.appendChild(hiddenInput);
  let textInputActive = false, composing = false;

  const passthrough = (e) => e.code === 'F12' || (e.ctrlKey && e.shiftKey && (e.code === 'KeyI' || e.code === 'KeyJ'))
    || (e.target instanceof Element && e.target.closest('[data-xc-own-keys]') !== null); // launcher overlays (log viewer filter box)
  addEventListener('keydown', (e) => {
    if (passthrough(e)) return;
    onGesture();
    slot(S.CAPS_LOCK, e.getModifierState && e.getModifierState('CapsLock') ? 1 : 0);
    const k = keyIndex.get(e.code);
    syncModifiers(e);
    if (!e.repeat) globalThis.xcAudioMarkInput?.(e.timeStamp);
    if (k !== undefined) { downKeys.add(k); push(6, k); }
    // let the hidden textarea receive printable keys/IME when text input is active; block everything else
    const printable = e.key.length === 1 && !e.ctrlKey && !e.metaKey;
    if (!(textInputActive && (printable || composing || e.key === 'Process' || e.key === 'Dead'))) e.preventDefault();
    if (!textInputActive && printable) push(8, e.key.codePointAt(0));
  }, true);
  addEventListener('keyup', (e) => {
    if (passthrough(e)) return;
    const k = keyIndex.get(e.code);
    if (k !== undefined) { downKeys.delete(k); push(7, k); }
    syncModifiers(e);
    e.preventDefault();
  }, true);

  hiddenInput.addEventListener('compositionstart', () => { composing = true; });
  hiddenInput.addEventListener('compositionupdate', (e) => {
    const [s, l] = writeText(e.data || '');
    push(18, s, l, (e.data || '').length, 0);
  });
  hiddenInput.addEventListener('compositionend', (e) => {
    composing = false;
    push(18, 0, 0, 0, 0);
    if (e.data) { const [s, l] = writeText(e.data); push(19, s, l); }
    hiddenInput.value = '';
  });
  hiddenInput.addEventListener('input', (e) => {
    if (composing || e.isComposing) return;
    if (e.data) { const [s, l] = writeText(e.data); push(19, s, l); }
    hiddenInput.value = '';
  });

  // ------------------------------------------------------------------ focus / visibility
  const publishFocus = () => { slot(S.FOCUSED, document.hasFocus() ? 1 : 0); slot(S.VISIBLE, document.visibilityState === 'visible' ? 1 : 0); };
  addEventListener('focus', () => { publishFocus(); rectCache.r = null; });
  addEventListener('blur', () => { releaseAll(); publishFocus(); });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') { releaseAll(); hooks.onHidden?.(); }
    else { rectCache.r = null; globalThis.xcAudioResume?.(); }
    publishFocus();
  });
  addEventListener('pagehide', releaseAll);

  // ------------------------------------------------------------------ clipboard
  addEventListener('paste', (e) => {
    const text = e.clipboardData?.getData('text/plain');
    if (text != null) setClipboard(text);
  });
  function setClipboard(text) {
    const [s, l] = writeText(text);
    slot(S.CLIPBOARD_START, s); slot(S.CLIPBOARD_LEN, l);
    views(); Atomics.add(i32, (base >> 2) + S.CLIPBOARD_SEQ, 1);
  }
  async function refreshClipboard() {
    try { const t = await navigator.clipboard.readText(); if (t) setClipboard(t); } catch { }
  }
  addEventListener('focus', refreshClipboard);

  // ------------------------------------------------------------------ files (drag & drop / picker) → FS → DragDrop
  async function importFiles(files) {
    for (const f of files) {
      const path = await hooks.storeImport(f);
      if (path) { const [s, l] = writeText(path); push(17, s, l); }
    }
  }
  addEventListener('dragover', (e) => e.preventDefault());
  addEventListener('drop', (e) => { e.preventDefault(); if (e.dataTransfer?.files?.length) importFiles(e.dataTransfer.files); });

  function pickFiles(accept) {
    const input = document.createElement('input');
    input.type = 'file'; input.multiple = true;
    if (accept) input.accept = accept;
    input.onchange = () => importFiles(input.files);
    input.click();
  }

  // ------------------------------------------------------------------ gamepads
  const padState = [];
  function pollPads() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    for (const p of pads) {
      if (!p) continue;
      const st = padState[p.index] || (padState[p.index] = { b: [], a: [] });
      p.buttons.forEach((b, i) => { if (!!b.pressed !== !!st.b[i]) { st.b[i] = b.pressed; push(22, i, b.pressed ? 1 : 0); } });
      p.axes.forEach((v, i) => { if (Math.abs(v - (st.a[i] || 0)) > 0.002) { st.a[i] = v; push(23, i, fb(v)); } });
    }
    requestAnimationFrame(pollPads);
  }
  addEventListener('gamepadconnected', () => { if (!padState.polling) { padState.polling = true; requestAnimationFrame(pollPads); } });

  navigator.keyboard?.addEventListener?.('layoutchange', () => push(24));
  addEventListener('beforeunload', () => { hooks.onHidden?.(); });

  // ------------------------------------------------------------------ globals used by native glue
  globalThis.xcTakeOffscreenCanvas = () => {
    const r = canvas.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(r.width * dpr)); canvas.height = Math.max(1, Math.round(r.height * dpr));
    return canvas.transferControlToOffscreen();
  };

  globalThis.xcAttachShared = (ptr, size) => {
    base = ptr;
    views();
    ringCap = i32[(base >> 2) + S.RING_CAP];
    attached = true;
    const r = canvas.getBoundingClientRect();
    dpr = renderDpr();
    publishSize(r.width * dpr, r.height * dpr);
    publishFocus();
    slot(S.KEYBOARD_ATTACHED, 1);
    measureRefresh();
    for (const p of pending.splice(0)) push(...p);
    hooks.onAttached?.();
  };

  // screen wake lock (released by the browser whenever the tab is hidden, so re-acquire on return)
  let wakeWanted = false, wakeLock = null;
  async function setWakeLock(on) {
    wakeWanted = on;
    try {
      if (on && !wakeLock && document.visibilityState === 'visible' && navigator.wakeLock) {
        wakeLock = await navigator.wakeLock.request('screen');
        wakeLock.addEventListener('release', () => { wakeLock = null; });
      } else if (!on && wakeLock) { const l = wakeLock; wakeLock = null; await l.release(); }
    } catch { wakeLock = null; }
  }
  document.addEventListener('visibilitychange', () => { if (wakeWanted && document.visibilityState === 'visible') setWakeLock(true); });

  globalThis.xcUi = (op, a, b, s) => {
    globalThis.xcTelemetryEvent?.('worker-ui', { op });
    switch (op) {
      case 1: document.title = s || 'xc-osu!'; break;
      case 2: canvas.style.cursor = a ? 'default' : 'none'; break;
      case 3: wantLock = !!a; if (a) tryLock(); else if (locked()) document.exitPointerLock(); break;
      case 4: wantFullscreen = a ? 1 : 0; tryFullscreen(); break;
      // links written for the official website lead to the same page of this site
      case 5: if (s) window.open(s.replace(/^https?:\/\/osu\.ppy\.sh(?=[\/?#]|$)/i, location.origin), '_blank', 'noopener'); break;
      case 6: if (s != null) navigator.clipboard?.writeText(s).catch(() => { }); break;
      case 7: textInputActive = true; hiddenInput.focus({ preventScroll: true }); break;
      case 8: textInputActive = false; hiddenInput.blur(); break;
      case 9: case 14: if (s) hooks.download(s); break;
      case 10: hooks.onExit?.(a, s); break;
      case 11: break; // cursor confinement is implicit with pointer lock
      case 12:
        // a < 0: screen suspension control (b = 1 keeps the screen awake while playing), otherwise a vibration in ms
        if (a < 0) setWakeLock(!!b); else navigator.vibrate?.(a);
        break;
      case 13: pickFiles(s); break;
      case 15: hooks.onStartGate?.(); break; // engine loaded, waiting for the player's click
    }
  };

  const openStartGate = () => slot(S.START_GATE, 1);
  return { push, writeText, pickFiles, importFiles, openStartGate };
}
