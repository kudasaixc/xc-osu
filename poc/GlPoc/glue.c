#include <emscripten.h>
#include <emscripten/threading.h>
#include <pthread.h>
#include <GLES3/gl3.h>

EM_JS(void, xc_install_listener, (), {
  self.xcRaf = () => new Promise(r => requestAnimationFrame(r));
  self.addEventListener('message', function (e) { if (e.data && e.data.xcCanvas) self.__xcCanvas = e.data.xcCanvas; });
});
void xc_request_canvas(void) {
  xc_install_listener();
  MAIN_THREAD_ASYNC_EM_ASM({
    var c = document.querySelector('#canvas');
    var off = c.transferControlToOffscreen();
    PThread.pthreads[$0].postMessage({ xcCanvas: off }, [off]);
  }, pthread_self());
}
EM_JS(int, xc_gl_attach, (), {
  var c = self.__xcCanvas; if (!c) return 0;
  var attrs = { alpha: false, depth: true, stencil: true, antialias: false, premultipliedAlpha: false, preserveDrawingBuffer: false, powerPreference: 'high-performance', desynchronized: true };
  var ctx = c.getContext('webgl2', attrs); if (!ctx) return -1;
  attrs.majorVersion = 2; attrs.minorVersion = 0;
  var h = GL.registerContext(ctx, attrs); GL.makeContextCurrent(h); return h;
});
// keep GL lib referenced
void xc_dummy(void) { glFlush(); }
