// xc-osu! audio output worklet: xc softworks (kudasaixc)
// Two sources are mixed into every 128-frame block:
//  - the xcbass mixer ring (shared wasm memory): music and everything that goes through effects. Its delay is known and
//    compensated (positions are reported as heard).
//  - direct voices: one-shot samples (hit sounds, UI sounds) described in a small shared table and mixed right here from
//    their PCM, so they start in the very next block instead of waiting for the mixer thread and the ring.
// ctrl (Int32): [0]=read frames (we own) [1]=write frames (mixer owns) [2]=capacity [3]=latency frames [4]=running [5]=underruns
// voices: see xcbass.c ("direct voices").
const NVOICE = 32;

class XcOutput extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { buffer, ring, ctrl, cap, voices } = options.processorOptions;
    this.ringPtr = ring; this.ctrlPtr = ctrl; this.cap = cap; this.voicesPtr = voices || 0;
    this.mask = cap - 1;
    this.primed = false;
    this.vgen = new Int32Array(NVOICE);
    this.vpos = new Float64Array(NVOICE);
    this.attach(buffer);
    // the wasm memory can grow: the page then sends the new (larger) view of it
    this.port.onmessage = (e) => { if (e.data && e.data.buffer) this.attach(e.data.buffer); };
  }

  attach(buffer) {
    this.ring = new Float32Array(buffer, this.ringPtr, this.cap * 2);
    this.ctrl = new Int32Array(buffer, this.ctrlPtr, 16);
    this.heap = new Float32Array(buffer, 0, buffer.byteLength >> 2);
    if (this.voicesPtr) {
      this.vi = new Int32Array(buffer, this.voicesPtr, 8 + NVOICE * 8 + 128);
      this.vf = new Float32Array(buffer, this.voicesPtr, 8 + NVOICE * 8 + 128);
      Atomics.store(this.vi, 0, buffer.byteLength >>> 16);
    }
  }

  mixVoices(L, R, n) {
    const vi = this.vi;
    if (!vi) return;
    const vf = this.vf, heap = this.heap, limit = heap.length;
    for (let s = 0; s < NVOICE; s++) {
      const b = 8 + s * 8;
      const state = Atomics.load(vi, b);
      if (state === 2) { Atomics.compareExchange(vi, b, 2, 0); continue; }
      if (state !== 1) continue;
      const gen = Atomics.load(vi, b + 1);
      let pos;
      if (this.vgen[s] !== gen) {
        this.vgen[s] = gen; pos = 0;
        // latency probe: time between the play request and this block (Date has millisecond resolution here)
        let wait = ((Date.now() % 1e6) * 1000 - vi[b + 7]);
        if (wait < 0) wait += 1e9;
        if (wait < 1e6) { Atomics.add(vi, 1, 1); Atomics.add(vi, 2, wait | 0); }
        // exact start: this voice's first sample is frame `currentFrame` of the context. The page turns that into the
        // time it is heard with AudioContext.getOutputTimestamp().
        const k = 8 + NVOICE * 8 + (vi[3] & 63) * 2;
        vi[k] = vi[b + 7]; vi[k + 1] = currentFrame | 0;
        Atomics.add(vi, 3, 1);
      } else pos = this.vpos[s];
      const frames = vi[b + 3], base = (vi[b + 2] >>> 0) >> 2;
      if (base + frames * 2 > limit) { Atomics.compareExchange(vi, b, 1, 0); continue; }
      const step = vf[b + 4], gl = vf[b + 5], gr = vf[b + 6];
      const last = frames - 1;
      for (let i = 0; i < n; i++) {
        const ip = pos | 0;
        if (ip > last) break;
        const t = pos - ip, p = base + ip * 2;
        const l0 = heap[p], r0 = heap[p + 1];
        // linear interpolation towards the next frame (silence after the last one)
        const l1 = ip < last ? heap[p + 2] : 0, r1 = ip < last ? heap[p + 3] : 0;
        L[i] += (l0 + (l1 - l0) * t) * gl;
        R[i] += (r0 + (r1 - r0) * t) * gr;
        pos += step;
      }
      this.vpos[s] = pos;
      if ((pos | 0) > last) Atomics.compareExchange(vi, b, 1, 0);
    }
  }

  process(inputs, outputs) {
    const out = outputs[0];
    const L = out[0], R = out[1] || new Float32Array(L.length);
    const n = L.length;
    const ctrl = this.ctrl;
    // call cadence: the browser asks for several quanta back to back each time the device wants a buffer
    if (this.vi) {
      const now = Date.now();
      if (this.lastCall) { const d = now - this.lastCall; Atomics.add(this.vi, d <= 1 ? 4 : d <= 5 ? 5 : d <= 12 ? 6 : 7, 1); }
      this.lastCall = now;
    }
    let r = Atomics.load(ctrl, 0);
    const w = Atomics.load(ctrl, 1);
    const avail = (w - r) | 0;

    if (avail >= n) {
      const ring = this.ring, mask = this.mask;
      for (let i = 0; i < n; i++) {
        const idx = ((r + i) & mask) << 1;
        L[i] = ring[idx];
        R[i] = ring[idx + 1];
      }
      r = (r + n) | 0;
      this.primed = true;
      Atomics.store(ctrl, 0, r);
      Atomics.notify(ctrl, 0);
    } else {
      // underrun: silence from the ring, and don't advance: the audible clock pauses for this quantum, exactly like a
      // stalled device. Direct voices keep playing.
      L.fill(0); R.fill(0);
      if (this.primed) Atomics.add(ctrl, 5, 1);
    }

    this.mixVoices(L, R, n);
    // same soft clip protection as the mixer applies to the ring
    for (let i = 0; i < n; i++) {
      if (L[i] > 1.5) L[i] = 1.5; else if (L[i] < -1.5) L[i] = -1.5;
      if (R[i] > 1.5) R[i] = 1.5; else if (R[i] < -1.5) R[i] = -1.5;
    }
    if (out.length === 1) for (let i = 0; i < n; i++) L[i] = (L[i] + R[i]) * 0.5;
    return true;
  }
}

registerProcessor('xc-output', XcOutput);
