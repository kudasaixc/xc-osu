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
  self.addEventListener('message', function (e) {
    if (e.data && e.data.xcCanvas) self.__xcCanvas = e.data.xcCanvas;
  });
});

void xc_request_canvas(void) {
  xc_worker_install();
  MAIN_THREAD_ASYNC_EM_ASM({
    var off = globalThis.xcTakeOffscreenCanvas();
    PThread.pthreads[$0].postMessage({ xcCanvas: off }, [off]);
  }, pthread_self());
}

EM_JS(int, xc_gl_attach, (), {
  var c = self.__xcCanvas;
  if (!c) return 0;
  var attrs = { alpha: false, depth: true, stencil: true, antialias: false, premultipliedAlpha: false,
                preserveDrawingBuffer: false, powerPreference: 'high-performance', desynchronized: true,
                failIfMajorPerformanceCaveat: false };
  var ctx = c.getContext('webgl2', attrs);
  if (!ctx) return -1;
  attrs.majorVersion = 2; attrs.minorVersion = 0; attrs.enableExtensionsByDefault = 1;
  var h = GL.registerContext(ctx, attrs);
  GL.makeContextCurrent(h);
  return h;
});

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

// xc-osu! (xc softworks, kudasaixc): .NET 10's multithreaded browser System.Native returns the same "OS thread id" (1)
// for every thread, so System.Threading.Lock believes foreign threads already own a lock (SynchronizationLockException /
// LockRecursionException in TimerQueue, Task.Delay, SQLite pools...). Returning -1 makes Lock fall back to the managed
// thread id, which is unique. Installed with -Wl,--wrap=SystemNative_TryGetUInt32OSThreadId.
#include <stdint.h>
__attribute__((used)) uint32_t __wrap_SystemNative_TryGetUInt32OSThreadId(void)
{
    return (uint32_t)-1;
}
