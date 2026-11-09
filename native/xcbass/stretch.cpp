// xc-osu! xcbass — time-stretch wrapper around Signalsmith Stretch (MIT). xc softworks (kudasaixc)
#include "signalsmith-stretch/signalsmith-stretch.h"
#include <vector>

using Stretch = signalsmith::stretch::SignalsmithStretch<float>;

struct XcStretch {
  Stretch s;
  std::vector<float> inL, inR, outL, outR;
};

extern "C" {

void* xs_create(int sampleRate) {
  auto* x = new XcStretch();
  // "cheaper" preset keeps CPU low (osu! needs it every frame) while still being far better than WSOLA
  x->s.presetCheaper(2, (float)sampleRate);
  return x;
}

void xs_destroy(void* p) { delete (XcStretch*)p; }

int xs_input_latency(void* p) { return ((XcStretch*)p)->s.inputLatency(); }
int xs_output_latency(void* p) { return ((XcStretch*)p)->s.outputLatency(); }

void xs_set_transpose(void* p, float factor) { ((XcStretch*)p)->s.setTransposeFactor(factor); }

static void deinterleave(XcStretch* x, const float* in, int n) {
  if ((int)x->inL.size() < n) { x->inL.resize(n); x->inR.resize(n); }
  for (int i = 0; i < n; i++) { x->inL[i] = in[i * 2]; x->inR[i] = in[i * 2 + 1]; }
}

// prime so that the next output frame corresponds exactly to the first input frame given.
// inFrames should be xs_output_seek_length(rate).
void xs_output_seek(void* p, const float* in, int inFrames) {
  auto* x = (XcStretch*)p;
  deinterleave(x, in, inFrames);
  float* ins[2] = { x->inL.data(), x->inR.data() };
  x->s.outputSeek(ins, inFrames);
}

int xs_output_seek_length(void* p, float rate) { return ((XcStretch*)p)->s.outputSeekLength(rate); }

void xs_process(void* p, const float* in, int inFrames, float* out, int outFrames) {
  auto* x = (XcStretch*)p;
  deinterleave(x, in, inFrames);
  if ((int)x->outL.size() < outFrames) { x->outL.resize(outFrames); x->outR.resize(outFrames); }
  float* ins[2] = { x->inL.data(), x->inR.data() };
  float* outs[2] = { x->outL.data(), x->outR.data() };
  x->s.process(ins, inFrames, outs, outFrames);
  for (int i = 0; i < outFrames; i++) { out[i * 2] = x->outL[i]; out[i * 2 + 1] = x->outR[i]; }
}

void xs_reset(void* p) { ((XcStretch*)p)->s.reset(); }

}
