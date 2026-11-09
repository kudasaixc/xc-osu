// xc-osu! audio output worklet — drains the xcbass mixer ring (shared wasm memory). xc softworks (kudasaixc)
// ctrl (Int32): [0]=read frames (we own) [1]=write frames (mixer owns) [2]=capacity [3]=latency frames [4]=running [5]=underruns
class XcOutput extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { buffer, ring, ctrl, cap } = options.processorOptions;
    this.ring = new Float32Array(buffer, ring, cap * 2);
    this.ctrl = new Int32Array(buffer, ctrl, 16);
    this.mask = cap - 1;
    this.primed = false;
  }

  process(inputs, outputs) {
    const out = outputs[0];
    const L = out[0], R = out[1] || out[0];
    const n = L.length;
    const ctrl = this.ctrl;
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
    } else {
      // underrun: play silence and don't advance — the audible clock pauses for this quantum, exactly like a stalled device
      L.fill(0); if (R !== L) R.fill(0);
      if (this.primed) Atomics.add(ctrl, 5, 1);
      return true;
    }

    Atomics.store(ctrl, 0, r);
    Atomics.notify(ctrl, 0);
    return true;
  }
}

registerProcessor('xc-output', XcOutput);
