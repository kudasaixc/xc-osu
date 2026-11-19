// xc-osu! web platform glue (xc softworks / kudasaixc)
// Bridges osu-framework (running on .NET threads = emscripten pthreads) with the browser UI thread.
#include <emscripten.h>
#include <emscripten/threading.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <GLES3/gl3.h>

// ---------------------------------------------------------------------------------------------
// Draw worker: OffscreenCanvas transfer + WebGL2 context registered with emscripten's GL layer.
// ---------------------------------------------------------------------------------------------

EM_JS(void, xc_worker_install, (), {
  self.xcRaf = () => new Promise(r => requestAnimationFrame(r));
  // permanent loop: the next frame is requested before the callback runs, so a long frame never costs an extra refresh
  self.xcRafLoop = (cb) => { const step = (t) => { requestAnimationFrame(step); cb(t); }; requestAnimationFrame(step); };
  self.addEventListener('message', function (e) {
    if (e.data && e.data.xcCanvas) { self.__xcCanvas = e.data.xcCanvas; self.__xcGl = e.data.xcGl || null; }
  });
});

void xc_request_canvas(void) {
  xc_worker_install();
  MAIN_THREAD_ASYNC_EM_ASM({
    var off = globalThis.xcTakeOffscreenCanvas();
    // launch-screen choices (GPU preference, low-latency canvas, MSAA) travel with the canvas
    PThread.pthreads[$0].postMessage({ xcCanvas: off, xcGl: globalThis.xcGlAttributes || null }, [off]);
  }, pthread_self());
}

EM_JS(int, xc_gl_attach_js, (), {
  var c = self.__xcCanvas;
  if (!c) return 0;
  var attrs = { alpha: false, depth: true, stencil: true, antialias: false, premultipliedAlpha: false,
                preserveDrawingBuffer: false, powerPreference: 'high-performance', desynchronized: false,
                failIfMajorPerformanceCaveat: false };
  var o = self.__xcGl;
  if (o) {
    if (o.powerPreference) attrs.powerPreference = o.powerPreference;
    if (typeof o.desynchronized === 'boolean') attrs.desynchronized = o.desynchronized;
    if (typeof o.antialias === 'boolean') attrs.antialias = o.antialias;
  }
  var ctx = c.getContext('webgl2', attrs);
  if (ctx) console.log('[launch] WebGL2 context: powerPreference=' + attrs.powerPreference + ' desynchronized=' + attrs.desynchronized + ' antialias=' + attrs.antialias
                      + (ctx.getContextAttributes && ctx.getContextAttributes().desynchronized === false && attrs.desynchronized ? ' (low-latency canvas refused by the browser)' : ''));
  if (!ctx) return -1;
  attrs.majorVersion = 2; attrs.minorVersion = 0; attrs.enableExtensionsByDefault = 1;
  var h = GL.registerContext(ctx, attrs);
  GL.makeContextCurrent(h);
  return h;
});

// the draw worker's console is not mirrored on the page: the GL context step is logged synchronously on the UI
// thread so a browser that kills the tab while creating the context still leaves the line in the boot log
int xc_gl_attach(void) {
  static int announced = 0;
  if (!announced && EM_ASM_INT({ return self.__xcCanvas ? 1 : 0; })) {
    announced = 1;
    MAIN_THREAD_EM_ASM({ console.log('[launch] draw worker: creating the WebGL2 context'); });
  }
  int h = xc_gl_attach_js();
  if (h != 0) MAIN_THREAD_EM_ASM({ console.log('[launch] draw worker: WebGL2 context ' + ($0 > 0 ? 'ready (' + $0 + ')' : 'refused')); }, h);
  return h;
}

EM_JS(void, xc_canvas_resize, (int w, int h), {
  var c = self.__xcCanvas;
  if (c && (c.width !== w || c.height !== h)) { c.width = w; c.height = h; }
});

// ---------------------------------------------------------------------------------------------
// UI thread bridge: shared state block (written by UI JS, read by C#) and fire-and-forget calls.
// ---------------------------------------------------------------------------------------------

void xc_attach_shared(void* shared, int size) {
  MAIN_THREAD_ASYNC_EM_ASM({ globalThis.xcAttachShared($0, $1); }, shared, size);
}

// generic UI request: op + 2 ints + optional utf8 string (copied before returning)
void xc_ui_call(int op, int a, int b, const char* s) {
  char* copy = 0;
  if (s) { int n = 0; while (s[n]) n++; copy = (char*)malloc(n + 1); for (int i = 0; i <= n; i++) copy[i] = s[i]; }
  MAIN_THREAD_ASYNC_EM_ASM({
    var str = $3 ? UTF8ToString($3) : null;
    if ($3) _free($3);
    globalThis.xcUi($0, $1, $2, str);
  }, op, a, b, copy);
}

// keep the GL library referenced so it's always linked
void xc_gl_keepalive(void) { glFlush(); }

// ---------------------------------------------------------------------------------------------
// Input thread futex wait: sleeps in native code (so Mono GC never waits for this thread) until
// the UI thread increments WRITE_INDEX and calls Atomics.notify, or timeoutMs elapses.
// ---------------------------------------------------------------------------------------------
void xc_input_wait(volatile int32_t* writeIdxPtr, int32_t expected, int timeoutMs) {
  emscripten_futex_wait((volatile void*)writeIdxPtr, (uint32_t)expected, (double)timeoutMs);
}

// ---------------------------------------------------------------------------------------------
// Batched WebGL2 command buffer replay: drains an entire frame's hot draw/state/upload ops in a
// single wasm -> JS transition directly against GLctx, avoiding per-call P/Invoke & Emscripten glue.
// ---------------------------------------------------------------------------------------------
EM_JS(void, xc_gl_submit, (const int32_t* cmds, int cmdInts, const uint8_t* data), {
  var gl = GLctx;
  if (!gl || cmdInts <= 0) return;
  var buf = wasmMemory.buffer;
  if (self.__xcGlBuf !== buf) {
    self.__xcGlBuf = buf;
    self.__xcGlI32 = new Int32Array(buf);
    self.__xcGlF32 = new Float32Array(buf);
    self.__xcGlU8 = new Uint8Array(buf);
  }
  var h32 = self.__xcGlI32;
  var f32 = self.__xcGlF32;
  var u8 = self.__xcGlU8;
  var p = cmds >>> 2;
  var end = p + cmdInts;
  var dataBase = data >>> 0;
  var progs = GL.programs;
  var vaos = GL.vaos;
  var bufs = GL.buffers;
  var texs = GL.textures;
  var fbs = GL.framebuffers;
  while (p < end) {
    switch (h32[p++]) {
      case 1: { // USE_PROGRAM
        var pr = progs[h32[p++]] || null;
        gl.useProgram(pr);
        gl.currentProgram = pr;
        break;
      }
      case 2: // BIND_VAO
        gl.bindVertexArray(vaos[h32[p++]] || null);
        break;
      case 3: { // BIND_BUFFER
        var t = h32[p++];
        var id = h32[p++];
        gl.bindBuffer(t, bufs[id] || null);
        break;
      }
      case 4: { // BIND_BUFFER_BASE
        var t = h32[p++];
        var idx = h32[p++];
        var id = h32[p++];
        gl.bindBufferBase(t, idx, bufs[id] || null);
        break;
      }
      case 5: { // BIND_BUFFER_RANGE
        var t = h32[p++];
        var idx = h32[p++];
        var id = h32[p++];
        var off = h32[p++];
        var sz = h32[p++];
        gl.bindBufferRange(t, idx, bufs[id] || null, off, sz);
        break;
      }
      case 6: // ACTIVE_TEXTURE
        gl.activeTexture(h32[p++]);
        break;
      case 7: { // BIND_TEXTURE
        var t = h32[p++];
        var id = h32[p++];
        gl.bindTexture(t, texs[id] || null);
        break;
      }
      case 8: { // SCISSOR
        var x = h32[p++], y = h32[p++], w = h32[p++], h = h32[p++];
        gl.scissor(x, y, w, h);
        break;
      }
      case 9: { // VIEWPORT
        var x = h32[p++], y = h32[p++], w = h32[p++], h = h32[p++];
        gl.viewport(x, y, w, h);
        break;
      }
      case 10: // ENABLE
        gl.enable(h32[p++]);
        break;
      case 11: // DISABLE
        gl.disable(h32[p++]);
        break;
      case 12: { // BLEND_EQ_SEP
        var r = h32[p++], a = h32[p++];
        gl.blendEquationSeparate(r, a);
        break;
      }
      case 13: { // BLEND_FUNC_SEP
        var sr = h32[p++], dr = h32[p++], sa = h32[p++], da = h32[p++];
        gl.blendFuncSeparate(sr, dr, sa, da);
        break;
      }
      case 14: { // COLOR_MASK
        var r = h32[p++], g = h32[p++], b = h32[p++], a = h32[p++];
        gl.colorMask(!!r, !!g, !!b, !!a);
        break;
      }
      case 15: // DEPTH_FUNC
        gl.depthFunc(h32[p++]);
        break;
      case 16: // DEPTH_MASK
        gl.depthMask(!!h32[p++]);
        break;
      case 17: { // STENCIL_FUNC
        var fn = h32[p++], rf = h32[p++], mk = h32[p++];
        gl.stencilFunc(fn, rf, mk);
        break;
      }
      case 18: { // STENCIL_OP
        var f = h32[p++], zf = h32[p++], zp = h32[p++];
        gl.stencilOp(f, zf, zp);
        break;
      }
      case 19: // CLEAR
        gl.clear(h32[p++]);
        break;
      case 20: { // CLEAR_COLOR
        var r = f32[p++], g = f32[p++], b = f32[p++], a = f32[p++];
        gl.clearColor(r, g, b, a);
        break;
      }
      case 21: // CLEAR_DEPTH
        gl.clearDepth(f32[p++]);
        break;
      case 22: // CLEAR_STENCIL
        gl.clearStencil(h32[p++]);
        break;
      case 23: { // BIND_FRAMEBUFFER
        var t = h32[p++];
        var id = h32[p++];
        gl.bindFramebuffer(t, fbs[id] || null);
        break;
      }
      case 24: { // BUFFER_SUB_DATA
        var t = h32[p++];
        var off = h32[p++];
        var sz = h32[p++];
        var dOff = h32[p++];
        if (sz > 0) gl.bufferSubData(t, off, u8, dataBase + dOff, sz);
        break;
      }
      case 25: { // DRAW_ELEMENTS
        var m = h32[p++], c = h32[p++], ty = h32[p++], off = h32[p++];
        gl.drawElements(m, c, ty, off);
        break;
      }
      case 26: { // UNIFORM_BLOCK_BINDING
        var pr = progs[h32[p++]];
        var idx = h32[p++];
        var bnd = h32[p++];
        if (pr) gl.uniformBlockBinding(pr, idx, bnd);
        break;
      }
      case 27: { // BUFFER_DATA_NULL (orphan)
        var t = h32[p++];
        var sz = h32[p++];
        var u = h32[p++];
        gl.bufferData(t, sz, u);
        break;
      }
    }
  }
});

// xc-osu! (xc softworks, kudasaixc): .NET 10's multithreaded browser System.Native returns the same "OS thread id" (1)
// for every thread, so System.Threading.Lock believes foreign threads already own a lock (SynchronizationLockException /
// LockRecursionException in TimerQueue, Task.Delay, SQLite pools...). Returning -1 makes Lock fall back to the managed
// thread id, which is unique. Installed with -Wl,--wrap=SystemNative_TryGetUInt32OSThreadId.
#include <stdint.h>
__attribute__((used)) uint32_t __wrap_SystemNative_TryGetUInt32OSThreadId(void)
{
    return (uint32_t)-1;
}

// ---------------------------------------------------------------------------------------------
// Resource pack (xc softworks, kudasaixc): osu.Game.Resources lives in a separate file kept by the page in Cache
// Storage (js/xc-respack.js), not in the wasm heap. A read is served by the UI thread (Blob.slice -> arrayBuffer,
// asynchronous there) straight into the caller's buffer; the calling .NET thread waits on a futex meanwhile. It is in
// native code during the wait, so a GC never waits for it. The UI thread never blocks on a .NET thread here.
// ---------------------------------------------------------------------------------------------

#include <emscripten/threading.h>

// state[0]: 0 pending / 1 done, state[1]: bytes read or a negative error
int xc_respack_read(int offset, int length, void* dst) {
  volatile int32_t state[2] = { 0, 0 };
  MAIN_THREAD_ASYNC_EM_ASM({
    var off = $0; var len = $1; var dst = $2 >>> 0; var st = $3 >>> 0;
    var done = function (n) {
      var h = new Int32Array(wasmMemory.buffer);
      Atomics.store(h, (st >>> 2) + 1, n);
      Atomics.store(h, st >>> 2, 1);
      Atomics.notify(h, st >>> 2);
    };
    var p = globalThis.xcResPack;
    if (!p) { done(-2); return; }
    p.read(off, len).then(function (buf) {
      var n = Math.min(len, buf.byteLength);
      new Uint8Array(wasmMemory.buffer, dst, n).set(new Uint8Array(buf, 0, n));
      done(n);
    }, function (e) { console.warn('[respack] read failed at ' + off + ' (' + len + ' bytes): ' + e); done(-1); });
  }, offset, length, dst, state);
  // Never give up while the read is in flight: the UI thread would later write into a buffer / stack frame that no
  // longer belongs to this call. A read that takes long is only reported.
  double started = emscripten_get_now();
  int warned = 0;
  while (state[0] == 0) {
    emscripten_futex_wait((volatile void*)&state[0], 0, 250);
    if (!warned && state[0] == 0 && emscripten_get_now() - started > 10000) {
      warned = 1;
      MAIN_THREAD_ASYNC_EM_ASM({ console.warn('[respack] a resource read has been waiting for 10 s (offset ' + $0 + ')'); }, offset);
    }
  }
  // done() stores the result before the flag and the flag before notifying: state[] is no longer touched here
  return state[1];
}

// ---------------------------------------------------------------------------------------------
// Memory map for telemetry: where the wasm linear memory goes (xc softworks, kudasaixc).
//   out[0] wasm memory size, out[1] sbrk top (everything below is handed out: static data, assemblies, malloc arena),
//   out[2] malloc arena (bytes obtained from sbrk by dlmalloc), out[3] in use, out[4] free inside the arena,
//   out[5] largest free chunk at the top (releasable), out[6] microseconds spent collecting this.
// mallinfo() walks the heap under the malloc lock: call it rarely (telemetry does every 10 s).
// ---------------------------------------------------------------------------------------------

#include <malloc.h>
#include <unistd.h>

void xc_mem_stats(double* out) {
  double t0 = emscripten_get_now();
  struct mallinfo mi = mallinfo();
  out[0] = (double)__builtin_wasm_memory_size(0) * 65536.0;
  out[1] = (double)(uintptr_t)sbrk(0);
  out[2] = (double)(unsigned)mi.arena;
  out[3] = (double)(unsigned)mi.uordblks;
  out[4] = (double)(unsigned)mi.fordblks;
  out[5] = (double)(unsigned)mi.keepcost;
  out[6] = (emscripten_get_now() - t0) * 1000.0;
}
