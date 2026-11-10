// xc-osu! xcbass — a BASS / BASS_FX / BASSmix compatible audio engine for WebAssembly.
// xc softworks (kudasaixc). Lets osu-framework's unmodified BASS audio stack (ManagedBass bindings) run in the browser.
//
// Graph: device <- [non-decode channels, e.g. global mixer] <- mixers <- channels (decoder / tempo / reverse / sample channel)
// Output: a dedicated pthread mixes 128-frame blocks into a shared float ring that an AudioWorklet plays.
// Timing: every mixer channel records (global frame, source position, rate) per block so that positions reported
// to osu! are the positions *currently audible* (ring consumption + output latency), like BASS_MIXER_CHAN_BUFFER.

#include <emscripten.h>
#include <emscripten/threading.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <math.h>

#include "third_party/dr_mp3.h"
#include "third_party/dr_wav.h"
#include "third_party/dr_flac.h"
#define STB_VORBIS_HEADER_ONLY
#define STB_VORBIS_NO_STDIO
#define STB_VORBIS_NO_PUSHDATA_API
#include "third_party/stb_vorbis.c"

typedef uint32_t DWORD;
typedef uint64_t QWORD;
typedef int BOOL;
#define TRUE 1
#define FALSE 0
#define EXPORT __attribute__((used, visibility("default")))

// ------------------------------------------------------------------------------------------------ constants
enum {
  E_OK = 0, E_MEM = 1, E_FILEOPEN = 2, E_DRIVER = 3, E_HANDLE = 5, E_FORMAT = 6, E_POSITION = 7, E_INIT = 8, E_START = 9,
  E_ALREADY = 14, E_NOTAUDIO = 17, E_NOCHAN = 18, E_ILLTYPE = 19, E_ILLPARAM = 20, E_DEVICE = 23, E_NOPLAY = 24,
  E_NOTAVAIL = 37, E_DECODE = 38, E_FILEFORM = 41, E_ENDED = 45, E_UNKNOWN = -1,
};

#define F_BYTE 0x1
#define F_MONO 0x2
#define F_LOOP 0x4
#define F_FLOAT 0x100
#define F_FX_FREESOURCE 0x10000
#define F_PRESCAN 0x20000
#define F_AUTOFREE 0x40000
#define F_DECODE 0x200000
#define F_SAMCHAN_STREAM 0x2
#define F_SAMPLE_OVER_POS 0x20000

#define MIX_CHAN_BUFFER 0x2000
#define MIX_CHAN_PAUSE 0x20000
#define MIX_CHAN_NORAMPIN 0x800000
#define MIX_NONSTOP 0x20000
#define MIX_END 0x10000

#define A_FREQ 1
#define A_VOL 2
#define A_PAN 3
#define A_NOBUFFER 5
#define A_CPU 7
#define A_SRC 8
#define A_NORAMP 11
#define A_BITRATE 12
#define A_BUFFER 13
#define A_TEMPO 0x10000
#define A_TEMPO_PITCH 0x10001
#define A_TEMPO_FREQ 0x10002
#define A_REVERSE_DIR 0x11000

#define SYNC_POS 0
#define SYNC_END 2
#define SYNC_FREE 8
#define SYNC_STOP 0xE
#define SYNC_MIXTIME 0x40000000
#define SYNC_ONETIME 0x80000000u

#define POS_BYTE 0
#define POS_MIXER_RESET 0x10000
#define POS_DECODE 0x10000000

#define DATA_AVAILABLE 0
#define DATA_FFT_INDIVIDUAL 0x10
#define DATA_FFT_NOWINDOW 0x20
#define DATA_FFT_REMOVEDC 0x40
#define DATA_FFT_COMPLEX 0x80
#define DATA_FIXED 0x20000000
#define DATA_FLOAT 0x40000000
#define DATA_FFT256 0x80000000u

#define LEVEL_MONO 1
#define LEVEL_STEREO 2
#define LEVEL_RMS 4
#define LEVEL_VOLPAN 8

#define ACTIVE_STOPPED 0
#define ACTIVE_PLAYING 1
#define ACTIVE_STALLED 2
#define ACTIVE_PAUSED 3

#define FX_BQF 0x10013
#define FX_PEAKEQ 0x10004
#define FX_VOLUME 0x10003

#define BLOCK 128
#define RING_FRAMES 16384 // power of two
#define HIST 512
#define LVL_FRAMES 8192   // power of two
#define DEVICE_RATE 48000
#define MAX_KIDS_TMP 4096

// ------------------------------------------------------------------------------------------------ errors / config
static _Thread_local int t_err;
static inline int fail(int e) { t_err = e; return 0; }
static inline void ok(void) { t_err = E_OK; }

static DWORD g_config[0x80];
static void* g_configPtr[0x80];
static float g_masterVolume = 1;

// ------------------------------------------------------------------------------------------------ types
typedef void (*SYNCPROC)(DWORD handle, DWORD channel, DWORD data, void* user);
typedef void (*FILECLOSEPROC)(void* user);
typedef QWORD (*FILELENPROC)(void* user);
typedef DWORD (*FILEREADPROC)(void* buffer, DWORD length, void* user);
typedef BOOL (*FILESEEKPROC)(QWORD offset, void* user);
typedef struct { FILECLOSEPROC close; FILELENPROC length; FILEREADPROC read; FILESEEKPROC seek; } BASS_FILEPROCS;

typedef struct { DWORD freq, chans, flags, ctype, origres; DWORD plugin; DWORD sample; const char* filename; } BASS_CHANNELINFO;
typedef struct { const char* name; const char* driver; DWORD flags; } BASS_DEVICEINFO;
typedef struct { DWORD flags, hwsize, hwfree, freesam, free3d, minrate, maxrate; BOOL eax; DWORD minbuf, dsver, latency, initflags, speakers, freq; } BASS_INFO;
typedef struct { DWORD freq; float volume; float pan; DWORD flags; DWORD length; DWORD max; DWORD origres; DWORD chans; DWORD mingap;
                 DWORD mode3d; float mindist; float maxdist; DWORD iangle; DWORD oangle; float outvol; DWORD vam; DWORD priority; } BASS_SAMPLE;
typedef struct { int lFilter; float fCenter, fGain, fBandwidth, fQ, fS; int lChannel; } BASS_BFX_BQF;
typedef struct { int lBand; float fBandwidth, fQ, fCenter, fGain; int lChannel; } BASS_BFX_PEAKEQ;

enum { T_FREE = 0, T_DECODER, T_PUSH, T_TEMPO, T_REVERSE, T_MIXER, T_SAMPLE, T_SAMPCHAN, T_FX, T_SYNC };
enum { FMT_NONE = 0, FMT_MP3, FMT_OGG, FMT_WAV, FMT_FLAC };

typedef struct Fx {
  DWORD handle; int type; int priority; struct Chan* owner; struct Fx* next;
  BASS_BFX_BQF bqf; BASS_BFX_PEAKEQ peq; float volume;
  double b0, b1, b2, a1, a2; double z1[2], z2[2]; int dirty;
} Fx;

typedef struct Sync {
  DWORD handle; DWORD type; QWORD param; SYNCPROC proc; void* user; struct Chan* owner; int mixer; struct Sync* next; int fired;
} Sync;

typedef struct Sample {
  DWORD handle; float* pcm; int64_t frames; int freq, chans; DWORD flags; int max; float vol, pan; int nchan;
} Sample;

typedef struct HistEntry { int64_t g; double pos; double rate; } HistEntry;

typedef struct Chan {
  DWORD handle; int type;
  pthread_mutex_t m;
  int freq, chans; DWORD flags; int device;
  float vol, pan, freqAttr;
  int ended;
  int64_t len; // frames (source)
  int64_t endG; // global frame at which the end was mixed (for End syncs)

  // decoder
  int fmt; uint8_t* data; size_t size; int64_t pos; int bitrate;
  drmp3 mp3; drwav wav; drflac* flac; stb_vorbis* ogg; drmp3_seek_point* seekPoints;
  BASS_FILEPROCS procs; void* procsUser; int hasProcs;

  // sample channel
  Sample* sample;

  // tempo / reverse
  struct Chan* src; int freeSrc;
  void* st; float tempo, pitch, tempoFreq; int stActive; double inAcc; float stRate, stTranspose;
  float* tbuf; int tcap;
  int dir;

  // mixer
  struct Chan** kids; int nkids, capkids; int64_t mpos;

  // membership in a mixer
  struct Chan* mixer; DWORD mflags;
  float* rb; int rbLen; double rpos; int rbEof; // resampler buffer (stereo)
  HistEntry hist[HIST]; int histHead, histCount;
  float lastVolL, lastVolR; int volInit;
  float* lvl; int64_t lvlEnd; // post-volume output history (stereo), keyed by global frame

  // device playback (non-decode channels)
  int playing; // 0 stop, 1 play, 2 pause
  double reqMs; // latency probe: when this sample channel was asked to play (0 = already measured)
  int voice; int32_t voiceGen; // direct worklet voice playing this channel (slot + 1, 0 = none) and its generation
  int64_t devEndG;

  Fx* fx;
  Sync* syncs;
} Chan;

// ------------------------------------------------------------------------------------------------ handle table
#define MAXH 32768
typedef struct { int type; uint16_t gen; void* p; } Slot;
static Slot g_slots[MAXH];
static int g_nextSlot = 1;
static pthread_mutex_t g_lock;
static pthread_once_t g_once = PTHREAD_ONCE_INIT;

static void init_once(void) {
  pthread_mutexattr_t a; pthread_mutexattr_init(&a); pthread_mutexattr_settype(&a, PTHREAD_MUTEX_RECURSIVE);
  pthread_mutex_init(&g_lock, &a);
  g_config[0x00] = 500;  // playback buffer
  g_config[0x01] = 100;  // update period
  g_config[0x1B] = 0;    // device buffer (ms) -> auto
  g_config[0x35] = 10;   // device period
}
static void lock(void) { pthread_once(&g_once, init_once); pthread_mutex_lock(&g_lock); }
static void unlock(void) { pthread_mutex_unlock(&g_lock); }

static DWORD alloc_handle(int type, void* p) {
  for (int n = 0; n < MAXH - 1; n++) {
    int i = g_nextSlot++;
    if (g_nextSlot >= MAXH) g_nextSlot = 1;
    if (g_slots[i].type == T_FREE) {
      g_slots[i].type = type; g_slots[i].p = p; g_slots[i].gen = (uint16_t)((g_slots[i].gen + 1) & 0x7fff); if (!g_slots[i].gen) g_slots[i].gen = 1;
      return ((DWORD)g_slots[i].gen << 16) | (DWORD)i;
    }
  }
  return 0;
}
static void* get_handle(DWORD h, int* type) {
  DWORD i = h & 0xffff; uint16_t gen = (uint16_t)(h >> 16);
  if (i == 0 || i >= MAXH || g_slots[i].type == T_FREE || g_slots[i].gen != gen) return NULL;
  if (type) *type = g_slots[i].type;
  return g_slots[i].p;
}
static void free_handle(DWORD h) { DWORD i = h & 0xffff; if (i && i < MAXH) { g_slots[i].type = T_FREE; g_slots[i].p = NULL; } }

static Chan* get_chan(DWORD h) {
  int t; void* p = get_handle(h, &t);
  if (!p || (t != T_DECODER && t != T_PUSH && t != T_TEMPO && t != T_REVERSE && t != T_MIXER && t != T_SAMPCHAN)) return NULL;
  return (Chan*)p;
}

static Chan* new_chan(int type) {
  Chan* c = (Chan*)calloc(1, sizeof(Chan));
  if (!c) return NULL;
  pthread_mutexattr_t a; pthread_mutexattr_init(&a); pthread_mutexattr_settype(&a, PTHREAD_MUTEX_RECURSIVE);
  pthread_mutex_init(&c->m, &a);
  c->type = type; c->vol = 1; c->pan = 0; c->dir = 1; c->chans = 2;
  c->handle = alloc_handle(type, c);
  if (!c->handle) { free(c); return NULL; }
  return c;
}

// ------------------------------------------------------------------------------------------------ device state
static int g_inited[3];  // 0 = no sound, 1 = default, 2 = the browser's output (listed so frameworks see a real device)
static int g_curDevice = 1;
static int g_rate = DEVICE_RATE;
static float* g_ring;
static int32_t* g_ctrl; // [0]=read (worklet) [1]=write [2]=cap [3]=latency frames [4]=running [5]=underruns [6]=ctx running
static int64_t g_mixG;          // global frame counter of the next block to be mixed
static int64_t g_blockG;        // global frame of the block currently being mixed
static volatile int g_threadRun;
static pthread_t g_thread;
static uint32_t g_lastRead; static int64_t g_consumed;
static int g_target = 1024;
static int g_base = 1024;       // steady-state ring depth (frames); raised when a device proves it needs more
static int g_lastUnderruns;
static int g_cleanFrames;       // frames mixed since the last underrun / adaptation step
static int g_sinceDecay = 1 << 30; // frames mixed since g_target was last lowered
static int g_baseClean;         // frames mixed since g_base was last questioned

// Absolute time shared by every thread (performance.now() alone has a per-worker origin).
EM_JS(double, xc_abs_now, (), { return performance.timeOrigin + performance.now(); });

// ---- latency probe. g_ctrl[8..15], read by the page (xcAudioDebug):
// [8] time of the last key / button press (written by the page, microseconds modulo 1e9)  [9] 1 = that press is unmatched
// [10] sample starts measured  [11] sum press -> play request (us)  [12] presses matched
// [13] sum play request -> first mixed block (us)  [14] sum ring depth in front of that block (frames)  [15] ring target
static void probe_request(struct Chan* k);
static void probe_mixed(struct Chan* k);

// ---- direct voices. The ring above is fine for music (its position is reported as heard, so the delay is compensated)
// but a hit sound cannot be scheduled: everything between "play" and the speakers is heard as lag. One-shot samples
// therefore skip the mixer thread and the ring entirely: the channel is described in a small shared table and the
// AudioWorklet mixes the sample's PCM straight into the block it is rendering (js/xc-audio-worklet.js).
// Table (int32 / float32 words, shared with the worklet):
//   header [0] wasm pages visible to the worklet (written by it)  [1] voices started  [2] sum request -> first block (us)
//   voice i at 8 + i * 8: [0] state (0 free, 1 playing, 2 stop requested, 3 being written)  [1] generation
//                         [2] PCM address (stereo float32)  [3] frames  [4] source frames per output frame (float)
//                         [5] left gain (float)  [6] right gain (float)  [7] request time (us modulo 1e9)
#define NVOICE 32
static int32_t* g_voices;
static void voice_start(struct Chan* k);
static void voice_stop(struct Chan* k);
static void voice_update(struct Chan* k);

static int64_t heard_g(void) {
  if (!g_ctrl) return g_mixG;
  uint32_t r = (uint32_t)__atomic_load_n(&g_ctrl[0], __ATOMIC_ACQUIRE);
  // extend the 32-bit worklet counter (only grows)
  int64_t c = g_consumed + (int64_t)(uint32_t)(r - g_lastRead);
  int64_t lat = __atomic_load_n(&g_ctrl[3], __ATOMIC_RELAXED);
  int64_t h = c - lat;
  return h < 0 ? 0 : h;
}
static void update_consumed(void) {
  if (!g_ctrl) return;
  uint32_t r = (uint32_t)__atomic_load_n(&g_ctrl[0], __ATOMIC_ACQUIRE);
  g_consumed += (int64_t)(uint32_t)(r - g_lastRead);
  g_lastRead = r;
}

// ------------------------------------------------------------------------------------------------ sync delivery
typedef struct { SYNCPROC proc; DWORD sync, chan, data; void* user; int64_t g; } PendingSync;
#define MAXPENDING 1024
static PendingSync g_pending[MAXPENDING];
static int g_npending;

static void queue_syncs(Chan* c, DWORD type, DWORD data, int64_t g) {
  for (Sync* s = c->syncs; s; s = s->next) {
    if ((s->type & 0xffff) != type || s->fired) continue;
    if (g_npending >= MAXPENDING) return;
    g_pending[g_npending++] = (PendingSync){ s->proc, s->handle, c->handle, data, s->user, (s->type & SYNC_MIXTIME) ? 0 : g };
    if (s->type & SYNC_ONETIME) s->fired = 1;
  }
}

// called at entry of API functions on managed threads; invokes callbacks outside of the lock
static void deliver_syncs(void) {
  if (!g_npending) return;
  PendingSync todo[64]; int n = 0;
  lock();
  int64_t heard = heard_g();
  int w = 0;
  for (int i = 0; i < g_npending; i++) {
    if (g_pending[i].g <= heard && n < 64) todo[n++] = g_pending[i];
    else g_pending[w++] = g_pending[i];
  }
  g_npending = w;
  unlock();
  for (int i = 0; i < n; i++) if (todo[i].proc) todo[i].proc(todo[i].sync, todo[i].chan, todo[i].data, todo[i].user);
}

// ------------------------------------------------------------------------------------------------ helpers
static int sample_bytes(DWORD flags) { return (flags & F_FLOAT) ? 4 : (flags & F_BYTE) ? 1 : 2; }
static int bpf(Chan* c) { return c->chans * sample_bytes(c->flags); }
static Chan* root_of(Chan* c) { while (c && (c->type == T_TEMPO || c->type == T_REVERSE)) c = c->src; return c; }

// ------------------------------------------------------------------------------------------------ decoders
static int detect_format(const uint8_t* d, size_t n) {
  if (n >= 4 && !memcmp(d, "OggS", 4)) return FMT_OGG;
  if (n >= 4 && (!memcmp(d, "RIFF", 4) || !memcmp(d, "RIFX", 4) || !memcmp(d, "FORM", 4) || !memcmp(d, "RF64", 4))) return FMT_WAV;
  if (n >= 4 && !memcmp(d, "fLaC", 4)) return FMT_FLAC;
  if (n >= 3 && !memcmp(d, "ID3", 3)) return FMT_MP3;
  if (n >= 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0) return FMT_MP3;
  // scan a little for an mp3 sync word (some files have junk before the first frame)
  for (size_t i = 0; i + 1 < n && i < 65536; i++) if (d[i] == 0xFF && (d[i + 1] & 0xE0) == 0xE0 && (d[i + 1] & 0x06)) return FMT_MP3;
  return FMT_NONE;
}

static int open_decoder(Chan* c) {
  c->fmt = detect_format(c->data, c->size);
  switch (c->fmt) {
    case FMT_MP3: {
      if (!drmp3_init_memory(&c->mp3, c->data, c->size, NULL)) return 0;
      c->freq = c->mp3.sampleRate; c->chans = c->mp3.channels;
      drmp3_uint32 nseek = 0;
      drmp3_uint64 frames = 0, pcm = 0;
      if (drmp3_get_mp3_and_pcm_frame_count(&c->mp3, &frames, &pcm)) {
        c->len = (int64_t)pcm;
        nseek = (drmp3_uint32)(frames / 8 + 1);
        c->seekPoints = (drmp3_seek_point*)malloc(sizeof(drmp3_seek_point) * nseek);
        if (c->seekPoints && drmp3_calculate_seek_points(&c->mp3, &nseek, c->seekPoints)) drmp3_bind_seek_table(&c->mp3, nseek, c->seekPoints);
      }
      drmp3_seek_to_pcm_frame(&c->mp3, 0);
      break;
    }
    case FMT_OGG: {
      int err = 0;
      c->ogg = stb_vorbis_open_memory(c->data, (int)c->size, &err, NULL);
      if (!c->ogg) return 0;
      stb_vorbis_info i = stb_vorbis_get_info(c->ogg);
      c->freq = (int)i.sample_rate; c->chans = i.channels;
      c->len = stb_vorbis_stream_length_in_samples(c->ogg);
      break;
    }
    case FMT_WAV:
      if (!drwav_init_memory(&c->wav, c->data, c->size, NULL)) return 0;
      c->freq = (int)c->wav.sampleRate; c->chans = c->wav.channels; c->len = (int64_t)c->wav.totalPCMFrameCount;
      break;
    case FMT_FLAC:
      c->flac = drflac_open_memory(c->data, c->size, NULL);
      if (!c->flac) return 0;
      c->freq = (int)c->flac->sampleRate; c->chans = c->flac->channels; c->len = (int64_t)c->flac->totalPCMFrameCount;
      break;
    default: return 0;
  }
  if (c->chans < 1 || c->freq <= 0) return 0;
  double secs = c->len > 0 ? (double)c->len / c->freq : 0;
  c->bitrate = secs > 0 ? (int)(c->size * 8 / secs / 1000) : 0;
  c->freqAttr = (float)c->freq;
  return 1;
}

static void close_decoder(Chan* c) {
  switch (c->fmt) {
    case FMT_MP3: drmp3_uninit(&c->mp3); break;
    case FMT_OGG: if (c->ogg) stb_vorbis_close(c->ogg); break;
    case FMT_WAV: drwav_uninit(&c->wav); break;
    case FMT_FLAC: if (c->flac) drflac_close(c->flac); break;
  }
  free(c->seekPoints); c->seekPoints = NULL;
  c->fmt = FMT_NONE;
}

// interleaved decode in source channel count, float
static int dec_raw(Chan* c, float* out, int frames) {
  switch (c->fmt) {
    case FMT_MP3: return (int)drmp3_read_pcm_frames_f32(&c->mp3, (drmp3_uint64)frames, out);
    case FMT_OGG: return stb_vorbis_get_samples_float_interleaved(c->ogg, c->chans, out, frames * c->chans);
    case FMT_WAV: return (int)drwav_read_pcm_frames_f32(&c->wav, (drwav_uint64)frames, out);
    case FMT_FLAC: return (int)drflac_read_pcm_frames_f32(c->flac, (drflac_uint64)frames, out);
  }
  return 0;
}

static int dec_seek(Chan* c, int64_t frame) {
  if (frame < 0) frame = 0;
  if (c->len > 0 && frame > c->len) frame = c->len;
  int okk = 0;
  switch (c->fmt) {
    case FMT_MP3: okk = drmp3_seek_to_pcm_frame(&c->mp3, (drmp3_uint64)frame); break;
    case FMT_OGG: okk = stb_vorbis_seek(c->ogg, (unsigned)frame); if (frame == 0) { stb_vorbis_seek_start(c->ogg); okk = 1; } break;
    case FMT_WAV: okk = drwav_seek_to_pcm_frame(&c->wav, (drwav_uint64)frame); break;
    case FMT_FLAC: okk = drflac_seek_to_pcm_frame(c->flac, (drflac_uint64)frame); break;
  }
  if (okk) { c->pos = frame; c->ended = 0; }
  return okk;
}

// to stereo
static void to_stereo(const float* in, float* out, int frames, int chans) {
  if (chans == 2) { if (in != out) memmove(out, in, sizeof(float) * frames * 2); return; }
  if (chans == 1) { for (int i = frames - 1; i >= 0; i--) { float s = in[i]; out[i * 2] = s; out[i * 2 + 1] = s; } return; }
  for (int i = 0; i < frames; i++) {
    // downmix: front L/R + centre/surround at -3dB
    float l = in[i * chans], r = in[i * chans + 1], extra = 0;
    for (int k = 2; k < chans; k++) extra += in[i * chans + k];
    extra *= 0.7071f / (chans - 2);
    out[i * 2] = l + extra; out[i * 2 + 1] = r + extra;
  }
}

static _Thread_local float* t_scratch; static _Thread_local int t_scratchCap;
static float* scratch(int samples) {
  if (t_scratchCap < samples) { free(t_scratch); t_scratchCap = samples + 1024; t_scratch = (float*)malloc(sizeof(float) * t_scratchCap); }
  return t_scratch;
}

// ------------------------------------------------------------------------------------------------ channel reading (stereo float)
static int chan_read(Chan* c, float* out, int frames);
static double chan_pos(Chan* c);
static double chan_rate(Chan* c);
static int chan_seek(Chan* c, int64_t frame);
static void mixer_mix(Chan* m, float* out, int frames);

static int decoder_read(Chan* c, float* out, int frames) {
  int done = 0;
  while (done < frames) {
    int want = frames - done;
    float* tmp = c->chans <= 2 ? out + done * 2 : scratch(want * c->chans);
    int got = dec_raw(c, tmp, want);
    if (got > 0) { to_stereo(tmp, out + done * 2, got, c->chans); done += got; c->pos += got; }
    if (got < want) {
      if (c->len <= 0 || c->pos < c->len) c->len = c->pos; // correct length estimate on early end
      if ((c->flags & F_LOOP) && c->pos > 0) { dec_seek(c, 0); continue; }
      c->ended = 1;
      break;
    }
  }
  return done;
}

static int sampchan_read(Chan* c, float* out, int frames) {
  Sample* s = c->sample;
  if (!s) { c->ended = 1; return 0; }
  int done = 0;
  while (done < frames) {
    int64_t avail = s->frames - c->pos;
    if (avail <= 0) {
      if (c->flags & F_LOOP && s->frames > 0) { c->pos = 0; continue; }
      c->ended = 1; break;
    }
    int n = (int)(avail < frames - done ? avail : frames - done);
    memcpy(out + done * 2, s->pcm + c->pos * 2, sizeof(float) * n * 2);
    c->pos += n; done += n;
  }
  return done;
}

// --- tempo (Signalsmith)
void* xs_create(int sampleRate); void xs_destroy(void*); int xs_input_latency(void*); int xs_output_latency(void*);
void xs_set_transpose(void*, float); void xs_output_seek(void*, const float*, int); int xs_output_seek_length(void*, float);
void xs_process(void*, const float*, int, float*, int); void xs_reset(void*);

static float tempo_ratio(Chan* c) {
  float r = 1.0f + c->tempo / 100.0f;
  if (r < 0.05f) r = 0.05f;
  return r;
}
static float tempo_transpose(Chan* c) { return powf(2.0f, c->pitch / 12.0f); }

static int tempo_bypass(Chan* c) { return fabsf(tempo_ratio(c) - 1.0f) < 1e-6f && fabsf(c->pitch) < 1e-6f; }

static void tempo_tbuf(Chan* c, int frames) {
  if (c->tcap < frames) { free(c->tbuf); c->tcap = frames + 1024; c->tbuf = (float*)malloc(sizeof(float) * c->tcap * 2); }
}

static double tempo_latency_src(Chan* c) {
  if (!c->stActive || !c->st) return 0;
  return xs_input_latency(c->st) + (double)c->stRate * xs_output_latency(c->st);
}

static void tempo_activate(Chan* c) {
  if (!c->st) c->st = xs_create(c->src->freq > 0 ? c->src->freq : 44100);
  float r = tempo_ratio(c);
  double startPos = chan_pos(c->src);
  xs_set_transpose(c->st, tempo_transpose(c));
  int need = xs_output_seek_length(c->st, r);
  tempo_tbuf(c, need);
  int got = chan_read(c->src, c->tbuf, need);
  if (got < need) memset(c->tbuf + got * 2, 0, sizeof(float) * (need - got) * 2);
  xs_output_seek(c->st, c->tbuf, need);
  c->stActive = 1; c->stRate = r; c->stTranspose = tempo_transpose(c); c->inAcc = 0;
  (void)startPos;
}

static void tempo_deactivate(Chan* c) {
  if (!c->stActive) return;
  // re-align the source with what would have been output next
  double p = chan_pos(c);
  c->stActive = 0;
  chan_seek(c->src, (int64_t)llround(p));
}

static int tempo_read(Chan* c, float* out, int frames) {
  if (tempo_bypass(c)) {
    if (c->stActive) tempo_deactivate(c);
    int got = chan_read(c->src, out, frames);
    c->ended = c->src->ended;
    return got;
  }
  if (!c->stActive) tempo_activate(c);
  float r = tempo_ratio(c);
  float tr = tempo_transpose(c);
  if (tr != c->stTranspose) { xs_set_transpose(c->st, tr); c->stTranspose = tr; }
  c->stRate = r;
  c->inAcc += frames * (double)r;
  int inFrames = (int)c->inAcc;
  c->inAcc -= inFrames;
  tempo_tbuf(c, inFrames);
  int got = inFrames ? chan_read(c->src, c->tbuf, inFrames) : 0;
  if (got < inFrames) memset(c->tbuf + got * 2, 0, sizeof(float) * (inFrames - got) * 2);
  xs_process(c->st, c->tbuf, inFrames, out, frames);
  if (c->src->ended && chan_pos(c) >= (double)root_of(c)->len) { c->ended = 1; }
  return frames;
}

// --- reverse
static int reverse_read(Chan* c, float* out, int frames) {
  if (c->dir >= 0) { int got = chan_read(c->src, out, frames); c->ended = c->src->ended; return got; }
  Chan* r = root_of(c);
  int64_t p = r->pos;
  int n = (int)(p < frames ? p : frames);
  if (n <= 0) { c->ended = 1; return 0; }
  dec_seek(r, p - n);
  int got = decoder_read(r, out, n);
  for (int i = 0; i < got / 2; i++) for (int k = 0; k < 2; k++) {
    float t = out[i * 2 + k]; out[i * 2 + k] = out[(got - 1 - i) * 2 + k]; out[(got - 1 - i) * 2 + k] = t;
  }
  dec_seek(r, p - n);
  c->ended = (p - n) <= 0;
  return got;
}

static int chan_read(Chan* c, float* out, int frames) {
  int got = 0;
  pthread_mutex_lock(&c->m);
  switch (c->type) {
    case T_DECODER: got = c->fmt ? decoder_read(c, out, frames) : 0; if (!c->fmt) c->ended = 1; break;
    case T_SAMPCHAN: got = sampchan_read(c, out, frames); break;
    case T_TEMPO: got = tempo_read(c, out, frames); break;
    case T_REVERSE: got = reverse_read(c, out, frames); break;
    case T_MIXER: mixer_mix(c, out, frames); got = frames; break;
    case T_PUSH: memset(out, 0, sizeof(float) * frames * 2); got = frames; break;
  }
  pthread_mutex_unlock(&c->m);
  return got;
}

static double chan_pos(Chan* c) {
  switch (c->type) {
    case T_DECODER: case T_SAMPCHAN: return (double)c->pos;
    case T_TEMPO: {
      double p = chan_pos(c->src);
      if (c->stActive) { p -= tempo_latency_src(c); if (p < 0) p = 0; }
      return p;
    }
    case T_REVERSE: return chan_pos(c->src);
    case T_MIXER: return (double)c->mpos;
  }
  return 0;
}

static double chan_rate(Chan* c) {
  switch (c->type) {
    case T_TEMPO: return (tempo_bypass(c) ? 1.0 : tempo_ratio(c)) * chan_rate(c->src);
    case T_REVERSE: return (c->dir < 0 ? -1.0 : 1.0) * chan_rate(c->src);
  }
  return 1.0;
}

static int chan_seek(Chan* c, int64_t frame) {
  int r = 1;
  pthread_mutex_lock(&c->m);
  switch (c->type) {
    case T_DECODER: r = dec_seek(c, frame); break;
    case T_SAMPCHAN: voice_stop(c); c->pos = frame < 0 ? 0 : frame; if (c->sample && c->pos > c->sample->frames) c->pos = c->sample->frames; c->ended = 0; break;
    case T_TEMPO:
      if (c->st) xs_reset(c->st);
      c->stActive = 0; c->inAcc = 0;
      r = chan_seek(c->src, frame); c->ended = 0;
      break;
    case T_REVERSE: r = chan_seek(c->src, frame); c->ended = 0; break;
    case T_MIXER: c->mpos = frame; break;
  }
  pthread_mutex_unlock(&c->m);
  return r;
}

// ------------------------------------------------------------------------------------------------ FX (biquads)
static void fx_compute(Fx* f, double rate) {
  f->dirty = 0;
  if (f->type == FX_BQF) {
    BASS_BFX_BQF* p = &f->bqf;
    double fc = p->fCenter; if (fc <= 0) fc = 1; if (fc > rate * 0.4999) fc = rate * 0.4999;
    double w0 = 2 * M_PI * fc / rate, cw = cos(w0), sw = sin(w0);
    double A = pow(10.0, p->fGain / 40.0);
    double alpha;
    if (p->fQ > 0) alpha = sw / (2 * p->fQ);
    else if (p->fBandwidth > 0) alpha = sw * sinh(M_LN2 / 2 * p->fBandwidth * w0 / sw);
    else if (p->fS > 0) alpha = sw / 2 * sqrt((A + 1 / A) * (1 / p->fS - 1) + 2);
    else alpha = sw / (2 * 0.7071);
    double b0 = 1, b1 = 0, b2 = 0, a0 = 1, a1 = 0, a2 = 0;
    switch (p->lFilter) {
      case 0: b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // lowpass
      case 1: b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // highpass
      case 2: b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // bandpass (0dB peak)
      case 3: b0 = sw / 2; b1 = 0; b2 = -sw / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // bandpass (Q peak)
      case 4: b0 = 1; b1 = -2 * cw; b2 = 1; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // notch
      case 5: b0 = 1 - alpha; b1 = -2 * cw; b2 = 1 + alpha; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break; // allpass
      case 6: b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A; break; // peaking
      case 7: { double s = 2 * sqrt(A) * alpha; // low shelf
        b0 = A * ((A + 1) - (A - 1) * cw + s); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - s);
        a0 = (A + 1) + (A - 1) * cw + s; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - s; break; }
      case 8: { double s = 2 * sqrt(A) * alpha; // high shelf
        b0 = A * ((A + 1) + (A - 1) * cw + s); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - s);
        a0 = (A + 1) - (A - 1) * cw + s; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - s; break; }
    }
    f->b0 = b0 / a0; f->b1 = b1 / a0; f->b2 = b2 / a0; f->a1 = a1 / a0; f->a2 = a2 / a0;
  } else if (f->type == FX_PEAKEQ) {
    BASS_BFX_PEAKEQ* p = &f->peq;
    double fc = p->fCenter > 0 ? p->fCenter : 1000; if (fc > rate * 0.4999) fc = rate * 0.4999;
    double w0 = 2 * M_PI * fc / rate, cw = cos(w0), sw = sin(w0), A = pow(10.0, p->fGain / 40.0);
    double alpha = p->fQ > 0 ? sw / (2 * p->fQ) : sw * sinh(M_LN2 / 2 * (p->fBandwidth > 0 ? p->fBandwidth : 1) * w0 / sw);
    double a0 = 1 + alpha / A;
    f->b0 = (1 + alpha * A) / a0; f->b1 = -2 * cw / a0; f->b2 = (1 - alpha * A) / a0; f->a1 = -2 * cw / a0; f->a2 = (1 - alpha / A) / a0;
  }
}

static void fx_apply(Chan* c, float* buf, int frames, double rate) {
  for (Fx* f = c->fx; f; f = f->next) {
    if (f->type == FX_VOLUME) { for (int i = 0; i < frames * 2; i++) buf[i] *= f->volume; continue; }
    if (f->type != FX_BQF && f->type != FX_PEAKEQ) continue;
    if (f->dirty) fx_compute(f, rate);
    int chmask = f->type == FX_BQF ? f->bqf.lChannel : f->peq.lChannel;
    for (int ch = 0; ch < 2; ch++) {
      if (chmask != -1 && chmask != 0 && !(chmask & (1 << ch))) continue;
      double z1 = f->z1[ch], z2 = f->z2[ch];
      for (int i = 0; i < frames; i++) {
        double x = buf[i * 2 + ch];
        double y = f->b0 * x + z1;
        z1 = f->b1 * x - f->a1 * y + z2;
        z2 = f->b2 * x - f->a2 * y;
        buf[i * 2 + ch] = (float)y;
      }
      if (!isfinite(z1) || !isfinite(z2)) z1 = z2 = 0;
      f->z1[ch] = z1; f->z2[ch] = z2;
    }
  }
}

// ------------------------------------------------------------------------------------------------ mixer
static void hist_push(Chan* k, int64_t g, double pos, double rate) {
  int i = (k->histHead + k->histCount) % HIST;
  if (k->histCount == HIST) { k->histHead = (k->histHead + 1) % HIST; i = (k->histHead + HIST - 1) % HIST; }
  else k->histCount++;
  k->hist[i] = (HistEntry){ g, pos, rate };
}

static void kid_reset(Chan* k) {
  k->rbLen = 0; k->rpos = 0; k->rbEof = 0; k->histCount = 0; k->histHead = 0;
}

static void lvl_store(Chan* k, const float* buf, int frames, int64_t g) {
  if (!k->lvl) return;
  for (int i = 0; i < frames; i++) {
    int64_t gi = g + i;
    int idx = (int)(gi & (LVL_FRAMES - 1));
    k->lvl[idx * 2] = buf[i * 2]; k->lvl[idx * 2 + 1] = buf[i * 2 + 1];
  }
  k->lvlEnd = g + frames;
}

// produce `frames` frames of kid output at mixer rate. returns frames produced (short = ended)
static int kid_render(Chan* m, Chan* k, float* out, int frames) {
  double ratio = (double)k->freqAttr / (double)m->freq;
  if (ratio <= 0) ratio = 1;
  int64_t g = g_blockG;

  // fast path: no resampling and nothing buffered
  if (fabs(ratio - 1.0) < 1e-9 && k->rbLen == 0) {
    double p = chan_pos(k), r = chan_rate(k);
    int got = chan_read(k, out, frames);
    hist_push(k, g, p, r);
    if (got < frames) memset(out + got * 2, 0, sizeof(float) * (frames - got) * 2);
    return got;
  }

  if (!k->rb) { k->rb = (float*)calloc((MAX_KIDS_TMP + 8) * 2, sizeof(float)); k->rbLen = 0; k->rpos = 0; }
  if (k->rbLen == 0) { // prime with one frame of history so cubic interpolation has a left neighbour
    k->rb[0] = k->rb[1] = 0; k->rbLen = 1; k->rpos = 1;
  }

  double readPos = chan_pos(k) - (k->rbLen - k->rpos) * chan_rate(k);
  hist_push(k, g, readPos, chan_rate(k) * ratio);

  int produced = 0;
  for (int i = 0; i < frames; i++) {
    int ip = (int)k->rpos;
    if (ip + 2 >= k->rbLen) {
      if (k->rbEof && ip >= k->rbLen - 1) break;
      // compact: keep from ip-1
      int keep = ip - 1; if (keep < 0) keep = 0;
      int rem = k->rbLen - keep;
      memmove(k->rb, k->rb + keep * 2, sizeof(float) * rem * 2);
      k->rbLen = rem; k->rpos -= keep; ip -= keep;
      if (!k->rbEof) {
        // Read only what the rest of this block consumes. This used to pull up to 1024 source frames at once: a nested
        // mixer running at another rate than the device (osu! mixes at 44.1 kHz, the browser plays 48 kHz) was rendered
        // ~21 ms ahead, so every sample started inside it waited for the next chunk and then sat in this buffer.
        int want = MAX_KIDS_TMP - k->rbLen; if (want > 1024) want = 1024;
        int need = (int)((frames - i) * ratio) + 4 - (k->rbLen - ip);
        if (need < 4) need = 4;
        if (want > need) want = need;
        int got = chan_read(k, k->rb + k->rbLen * 2, want);
        k->rbLen += got;
        if (got < want) {
          k->rbEof = 1;
          // pad 3 zero frames so interpolation near the end is defined
          for (int z = 0; z < 3; z++) { k->rb[(k->rbLen + z) * 2] = 0; k->rb[(k->rbLen + z) * 2 + 1] = 0; }
        }
      }
      if (ip + 1 >= k->rbLen + (k->rbEof ? 1 : 0)) { if (k->rbEof) break; }
    }
    double t = k->rpos - ip;
    const float* p0 = k->rb + (ip - 1) * 2; const float* p1 = p0 + 2; const float* p2 = p0 + 4; const float* p3 = p0 + 6;
    for (int ch = 0; ch < 2; ch++) {
      // 4-point Hermite (Catmull-Rom)
      float y0 = p0[ch], y1 = p1[ch], y2 = p2[ch], y3 = (ip + 2 < k->rbLen + 3) ? p3[ch] : y2;
      float c1 = 0.5f * (y2 - y0), c2 = y0 - 2.5f * y1 + 2.f * y2 - 0.5f * y3, c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
      out[i * 2 + ch] = ((c3 * (float)t + c2) * (float)t + c1) * (float)t + y1;
    }
    k->rpos += ratio;
    produced++;
  }
  if (produced < frames) memset(out + produced * 2, 0, sizeof(float) * (frames - produced) * 2);
  return produced;
}

static void apply_volpan(Chan* k, float* buf, int frames) {
  float pan = k->pan < -1 ? -1 : k->pan > 1 ? 1 : k->pan;
  float tl = k->vol * (pan > 0 ? 1 - pan : 1), tr = k->vol * (pan < 0 ? 1 + pan : 1);
  if (!k->volInit) { k->lastVolL = tl; k->lastVolR = tr; k->volInit = 1; }
  float l0 = k->lastVolL, r0 = k->lastVolR;
  if (l0 == tl && r0 == tr) {
    if (tl != 1 || tr != 1) for (int i = 0; i < frames; i++) { buf[i * 2] *= tl; buf[i * 2 + 1] *= tr; }
  } else {
    float dl = (tl - l0) / frames, dr = (tr - r0) / frames;
    for (int i = 0; i < frames; i++) { buf[i * 2] *= l0 + dl * i; buf[i * 2 + 1] *= r0 + dr * i; }
  }
  k->lastVolL = tl; k->lastVolR = tr;
}

static void probe_request(Chan* k) {
  if (k->type != T_SAMPCHAN) return;
  double req = xc_abs_now();
  k->reqMs = req;
  // the first sample asked for after a key / button press is taken as the answer to that press
  if (g_ctrl && __atomic_exchange_n(&g_ctrl[9], 0, __ATOMIC_ACQ_REL) == 1) {
    double in = fmod(req, 1e6) * 1000 - (double)__atomic_load_n(&g_ctrl[8], __ATOMIC_RELAXED);
    if (in < 0) in += 1e9;
    if (in < 300000) { __atomic_add_fetch(&g_ctrl[11], (int32_t)in, __ATOMIC_RELAXED); __atomic_add_fetch(&g_ctrl[12], 1, __ATOMIC_RELAXED); }
  }
  voice_start(k);
}
static void probe_mixed(Chan* k) {
  double req = k->reqMs, now = xc_abs_now();
  k->reqMs = 0;
  if (!g_ctrl) return;
  int64_t depth = g_blockG - g_consumed; if (depth < 0) depth = 0;
  double wait = (now - req) * 1000; if (wait < 0) wait = 0; if (wait > 1e6) return; // paused for a long time: not a start
  __atomic_add_fetch(&g_ctrl[10], 1, __ATOMIC_RELAXED);
  __atomic_add_fetch(&g_ctrl[13], (int32_t)wait, __ATOMIC_RELAXED);
  __atomic_add_fetch(&g_ctrl[14], (int32_t)depth, __ATOMIC_RELAXED);
}

static void voice_params(Chan* k, float* l, float* r, float* step) {
  float vol = k->vol * g_masterVolume;
  for (Chan* m = k->mixer; m; m = m->mixer) vol *= m->vol;
  float pan = k->pan < -1 ? -1 : k->pan > 1 ? 1 : k->pan;
  *l = vol * (pan > 0 ? 1 - pan : 1); *r = vol * (pan < 0 ? 1 + pan : 1);
  *step = k->freqAttr / (float)g_rate;
}
static int32_t* voice_slot(Chan* k) {
  if (!k->voice || !g_voices) return NULL;
  int32_t* v = g_voices + 8 + (k->voice - 1) * 8;
  return __atomic_load_n(&v[1], __ATOMIC_ACQUIRE) == k->voiceGen ? v : NULL; // the slot may have been reused since
}
// Only what the worklet reproduces exactly: a one-shot sample, no effect anywhere on its way to the device, every
// mixer above it playing. Anything else keeps the regular (ring) path.
static void voice_start(Chan* k) {
  k->voice = 0;
  if (!g_voices || !g_ctrl || k->type != T_SAMPCHAN || !k->sample || (k->flags & F_LOOP) || k->fx || !k->mixer) return;
  if (!__atomic_load_n(&g_ctrl[6], __ATOMIC_RELAXED)) return; // output not running: nothing would consume the voice
  Chan* m = k->mixer;
  for (;; m = m->mixer) {
    if (m->type != T_MIXER || m->fx) return;
    if (!m->mixer) break;
    if (m->mflags & MIX_CHAN_PAUSE) return;
  }
  if (m->playing != 1 || (m->flags & F_DECODE)) return;
  Sample* s = k->sample;
  int64_t pos = k->pos;
  if (pos < 0 || pos >= s->frames || k->freqAttr <= 0) return;
  uintptr_t end = (uintptr_t)(s->pcm + s->frames * 2);
  if ((end >> 16) >= (uintptr_t)__atomic_load_n(&g_voices[0], __ATOMIC_RELAXED)) return; // memory grew past what the worklet sees
  for (int i = 0; i < NVOICE; i++) {
    int32_t* v = g_voices + 8 + i * 8;
    int32_t expected = 0;
    if (!__atomic_compare_exchange_n(&v[0], &expected, 3, 0, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED)) continue;
    float l, r, step; voice_params(k, &l, &r, &step);
    v[2] = (int32_t)(uintptr_t)(s->pcm + pos * 2); v[3] = (int32_t)(s->frames - pos);
    memcpy(&v[4], &step, 4); memcpy(&v[5], &l, 4); memcpy(&v[6], &r, 4);
    v[7] = (int32_t)(fmod(k->reqMs, 1e6) * 1000);
    k->voiceGen = __atomic_add_fetch(&v[1], 1, __ATOMIC_ACQ_REL);
    __atomic_store_n(&v[0], 1, __ATOMIC_RELEASE);
    k->voice = i + 1; k->reqMs = 0;
    return;
  }
}
static void voice_stop(Chan* k) {
  int32_t* v = voice_slot(k);
  k->voice = 0;
  if (!v) return;
  int32_t expected = 1;
  __atomic_compare_exchange_n(&v[0], &expected, 2, 0, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED);
}
static void voice_update(Chan* k) {
  int32_t* v = voice_slot(k);
  if (!v || __atomic_load_n(&v[0], __ATOMIC_ACQUIRE) != 1) return;
  float l, r, step; voice_params(k, &l, &r, &step);
  memcpy(&v[4], &step, 4); memcpy(&v[5], &l, 4); memcpy(&v[6], &r, 4);
}
// The worklet reads the PCM without any lock: wait until no voice still points into a block before freeing it.
static void voice_release_pcm(const float* pcm, int64_t frames) {
  if (!g_voices || !pcm) return;
  uintptr_t a = (uintptr_t)pcm, b = (uintptr_t)(pcm + frames * 2);
  for (int tries = 0; tries < 60; tries++) {
    int busy = 0;
    for (int i = 0; i < NVOICE; i++) {
      int32_t* v = g_voices + 8 + i * 8;
      int32_t st = __atomic_load_n(&v[0], __ATOMIC_ACQUIRE);
      uintptr_t p = (uintptr_t)(uint32_t)v[2];
      if (st == 0 || p < a || p >= b) continue;
      if (st == 1) { int32_t expected = 1; __atomic_compare_exchange_n(&v[0], &expected, 2, 0, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED); }
      busy = 1;
    }
    if (!busy) return;
    // no worklet running (context suspended): nobody reads, nobody will acknowledge
    if (!__atomic_load_n(&g_ctrl[6], __ATOMIC_RELAXED) && tries >= 3) break;
    emscripten_thread_sleep(1);
  }
  for (int i = 0; i < NVOICE; i++) {
    int32_t* v = g_voices + 8 + i * 8;
    uintptr_t p = (uintptr_t)(uint32_t)v[2];
    if (p >= a && p < b) __atomic_store_n(&v[0], 0, __ATOMIC_RELEASE);
  }
}

static _Thread_local float* t_kidbuf;
static void mixer_mix(Chan* m, float* out, int frames) {
  memset(out, 0, sizeof(float) * frames * 2);
  if (!t_kidbuf) t_kidbuf = (float*)malloc(sizeof(float) * (MAX_KIDS_TMP + 8) * 2);
  // nested mixers may recurse: use a local buffer per depth
  float* kb = (float*)malloc(sizeof(float) * frames * 2);
  for (int i = 0; i < m->nkids; i++) {
    Chan* k = m->kids[i];
    if (k->mflags & MIX_CHAN_PAUSE) continue;
    if (k->ended && k->type != T_MIXER) continue;
    if (k->reqMs != 0) probe_mixed(k);
    int got = kid_render(m, k, kb, frames);
    fx_apply(k, kb, frames, m->freq);
    apply_volpan(k, kb, frames);
    lvl_store(k, kb, frames, g_blockG);
    if (!k->voice) for (int s = 0; s < frames * 2; s++) out[s] += kb[s];
    if (got < frames && k->type != T_MIXER) {
      k->ended = 1; k->endG = g_blockG + got;
      queue_syncs(k, SYNC_END, 0, k->endG);
    }
  }
  free(kb);
  m->mpos += frames;
}

// ------------------------------------------------------------------------------------------------ device output
EM_JS(void, xc_audio_js_init, (float* ring, int32_t* ctrl, int cap, int rate), {
  // the AudioContext must live on the page. Under .NET's multithreaded runtime this may run on a worker,
  // so hand the ring over to the page via a BroadcastChannel when xcAudioInit is not in scope.
  if (typeof globalThis.xcAudioInit === 'function') {
    globalThis.xcAudioInit((typeof wasmMemory !== 'undefined') ? wasmMemory : null, ring, ctrl, cap, rate);
    return;
  }
  var ch = new BroadcastChannel('xc-audio');
  ch.postMessage({ ring: ring, ctrl: ctrl, cap: cap, rate: rate });
  ch.close();
});

static void js_init_main(void) { xc_audio_js_init(g_ring, g_ctrl, RING_FRAMES, g_rate); }

static Chan g_devMix; // pseudo-mixer standing for the output device (only .freq is used by kid_render)

static void device_block(float* out, int frames) {
  memset(out, 0, sizeof(float) * frames * 2);
  float* kb = (float*)malloc(sizeof(float) * frames * 2);
  for (int i = 1; i < MAXH; i++) {
    if (g_slots[i].type == T_FREE || g_slots[i].type == T_FX || g_slots[i].type == T_SYNC || g_slots[i].type == T_SAMPLE) continue;
    Chan* c = (Chan*)g_slots[i].p;
    if (!c || c->playing != 1 || (c->flags & F_DECODE) || c->mixer) continue;
    // resample to the device rate (e.g. osu-framework's 44.1 kHz BASSmix mixer on a 48 kHz output)
    g_devMix.freq = g_rate;
    int got = kid_render(&g_devMix, c, kb, frames);
    fx_apply(c, kb, frames, g_rate);
    apply_volpan(c, kb, frames);
    lvl_store(c, kb, frames, g_blockG);
    for (int s = 0; s < frames * 2; s++) out[s] += kb[s];
    if (got < frames && c->type != T_MIXER) { c->playing = 0; queue_syncs(c, SYNC_END, 0, g_blockG + got); }
  }
  free(kb);
  if (g_masterVolume != 1) for (int s = 0; s < frames * 2; s++) out[s] *= g_masterVolume;
  // soft clip protection
  for (int s = 0; s < frames * 2; s++) { float x = out[s]; if (x > 1.5f) x = 1.5f; else if (x < -1.5f) x = -1.5f; out[s] = x; }
}

static void* mix_thread(void* arg) {
  (void)arg;
  float block[BLOCK * 2];
  while (g_threadRun) {
    lock();
    update_consumed();
    uint32_t r = (uint32_t)__atomic_load_n(&g_ctrl[0], __ATOMIC_ACQUIRE);
    uint32_t w = (uint32_t)__atomic_load_n(&g_ctrl[1], __ATOMIC_RELAXED);
    int fill = (int)(w - r);
    int under = __atomic_load_n(&g_ctrl[5], __ATOMIC_RELAXED);
    // Ring depth = latency of everything on this path that cannot be scheduled ahead (looping samples, sounds behind an
    // effect; one-shot samples bypass the ring, see "direct voices"). It used to grow by 256 frames on every underrun,
    // up to 85 ms, and never came back: a few stalls while loading left the whole session late.
    // Now: an underrun adds headroom, clean playback gives it back, and a device that underruns again right after a
    // reduction has its steady-state depth (g_base) raised instead of being probed forever. The floor is a comfortable
    // 21 ms: music is reported as heard whatever the depth, so a deeper ring only buys fewer dropouts.
    if (under != g_lastUnderruns) {
      g_lastUnderruns = under;
      if (g_sinceDecay < 10 * g_rate && g_base < 2048) { g_base = g_target + 128 > 2048 ? 2048 : g_target + 128; g_baseClean = 0; }
      if (g_target < 2048) g_target += 256;
      if (g_target < g_base) g_target = g_base;
      g_cleanFrames = 0; g_sinceDecay = 1 << 30;
    } else if (g_cleanFrames >= 3 * g_rate) {
      g_cleanFrames = 0;
      if (g_target > g_base) { g_target -= 128; if (g_target < g_base) g_target = g_base; g_sinceDecay = 0; }
      else if (g_baseClean >= 300 * g_rate && g_base > 1024) { g_baseClean = 0; g_base -= 64; g_target = g_base; g_sinceDecay = 0; }
    }
    __atomic_store_n(&g_ctrl[15], g_target, __ATOMIC_RELAXED);
    if (fill <= g_target - BLOCK) {
      g_blockG = g_mixG;
      device_block(block, BLOCK);
      for (int i = 0; i < BLOCK; i++) {
        uint32_t idx = (w + i) & (RING_FRAMES - 1);
        g_ring[idx * 2] = block[i * 2]; g_ring[idx * 2 + 1] = block[i * 2 + 1];
      }
      __atomic_store_n(&g_ctrl[1], (int32_t)(w + BLOCK), __ATOMIC_RELEASE);
      g_mixG += BLOCK;
      g_cleanFrames += BLOCK; if (g_sinceDecay < (1 << 30)) g_sinceDecay += BLOCK; if (g_baseClean < (1 << 30)) g_baseClean += BLOCK;
      unlock();
      continue;
    }
    unlock();
    emscripten_futex_wait(&g_ctrl[0], (uint32_t)r, 4.0);
  }
  return NULL;
}

static void start_output(void) {
  if (g_ring) return;
  g_ring = (float*)calloc(RING_FRAMES * 2, sizeof(float));
  g_ctrl = (int32_t*)calloc(16, sizeof(int32_t));
  g_ctrl[2] = RING_FRAMES;
  g_voices = (int32_t*)calloc(8 + NVOICE * 8, sizeof(int32_t));
  g_ctrl[7] = (int32_t)(uintptr_t)g_voices;
  int ms = (int)g_config[0x1B];
  g_base = ms > 0 ? ms * g_rate / 1000 + BLOCK : 1024;
  if (g_base < 1024) g_base = 1024; // a 10 ms device callback pulls four to five 128-frame quanta at once
  g_target = g_base + 256;        // headroom while the game loads; given back after a few clean seconds
  emscripten_sync_run_in_main_runtime_thread(EM_FUNC_SIG_V, js_init_main);
  g_threadRun = 1;
  pthread_attr_t a; pthread_attr_init(&a); pthread_attr_setstacksize(&a, 1 << 20);
  pthread_create(&g_thread, &a, mix_thread, NULL);
}

// ------------------------------------------------------------------------------------------------ freeing
static void detach_from_mixer(Chan* k) {
  Chan* m = k->mixer;
  if (!m) return;
  pthread_mutex_lock(&m->m);
  for (int i = 0; i < m->nkids; i++) if (m->kids[i] == k) { memmove(m->kids + i, m->kids + i + 1, sizeof(Chan*) * (m->nkids - i - 1)); m->nkids--; break; }
  pthread_mutex_unlock(&m->m);
  k->mixer = NULL; k->mflags = 0;
  voice_stop(k);
  kid_reset(k);
}

static void free_chan(Chan* c) {
  if (!c) return;
  queue_syncs(c, SYNC_FREE, 0, 0);
  detach_from_mixer(c);
  if (c->type == T_MIXER) { for (int i = 0; i < c->nkids; i++) { c->kids[i]->mixer = NULL; kid_reset(c->kids[i]); } free(c->kids); }
  for (Fx* f = c->fx; f;) { Fx* n = f->next; free_handle(f->handle); free(f); f = n; }
  for (Sync* s = c->syncs; s;) { Sync* n = s->next; free_handle(s->handle); free(s); s = n; }
  // drop pending syncs for this channel
  int w = 0; for (int i = 0; i < g_npending; i++) if (g_pending[i].chan != c->handle || (g_pending[i].proc && 0)) g_pending[w++] = g_pending[i];
  g_npending = w;
  free_handle(c->handle);
  pthread_mutex_lock(&c->m);
  if (c->type == T_DECODER) {
    close_decoder(c);
    free(c->data);
    if (c->hasProcs && c->procs.close) c->procs.close(c->procsUser);
  }
  if (c->type == T_SAMPCHAN && c->sample) c->sample->nchan--;
  if ((c->type == T_TEMPO || c->type == T_REVERSE) && c->freeSrc && c->src) free_chan(c->src);
  if (c->st) xs_destroy(c->st);
  free(c->tbuf); free(c->rb); free(c->lvl);
  pthread_mutex_unlock(&c->m);
  pthread_mutex_destroy(&c->m);
  free(c);
}

// ================================================================================================ BASS API
EXPORT DWORD BASS_GetVersion(void) { return 0x02041100; }
EXPORT int BASS_ErrorGetCode(void) { return t_err; }

EXPORT BOOL BASS_SetConfig(DWORD option, DWORD value) {
  lock();
  if (option < 0x80) g_config[option] = value;
  if (option == 0x1B && value > 0 && g_ring) { int b = (int)value * g_rate / 1000 + BLOCK; if (b < 1024) b = 1024; if (b > g_base) { g_base = b; if (g_target < b) g_target = b; } }
  unlock(); ok(); return TRUE;
}
EXPORT DWORD BASS_GetConfig(DWORD option) { ok(); return option < 0x80 ? g_config[option] : 0; }
EXPORT BOOL BASS_SetConfigPtr(DWORD option, const void* value) { if (option < 0x80) g_configPtr[option] = (void*)value; ok(); return TRUE; }
EXPORT const void* BASS_GetConfigPtr(DWORD option) { ok(); return option < 0x80 ? g_configPtr[option] : NULL; }

EXPORT BOOL BASS_GetDeviceInfo(DWORD device, BASS_DEVICEINFO* info) {
  if (device > 2) return fail(E_DEVICE);
  if (info) {
    info->name = device == 0 ? "No sound" : device == 1 ? "Default" : "Web Audio (browser output)";
    info->driver = device == 0 ? "" : "xc-osu! Web Audio";
    info->flags = 1 | (device >= 1 ? 2 : 0) | (g_inited[device] ? 4 : 0);
  }
  ok(); return TRUE;
}

EXPORT BOOL BASS_Init(int device, DWORD freq, DWORD flags, void* win, const void* clsid) {
  (void)freq; (void)flags; (void)win; (void)clsid;
  lock();
  if (device == -1) device = 1;
  if (device < 0 || device > 2) { unlock(); return fail(E_DEVICE); }
  if (g_inited[device]) { unlock(); return fail(E_ALREADY); }
  g_inited[device] = 1;
  g_curDevice = device;
  if (device >= 1) start_output();
  unlock(); ok(); return TRUE;
}

EXPORT BOOL BASS_Free(void) {
  lock();
  g_inited[g_curDevice] = 0;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_SetDevice(DWORD device) { if (device > 2) return fail(E_DEVICE); if (!g_inited[device]) return fail(E_INIT); g_curDevice = (int)device; ok(); return TRUE; }
EXPORT DWORD BASS_GetDevice(void) { if (!g_inited[g_curDevice]) { fail(E_INIT); return (DWORD)-1; } ok(); return (DWORD)g_curDevice; }
EXPORT BOOL BASS_GetInfo(BASS_INFO* info) {
  if (!g_inited[g_curDevice]) return fail(E_INIT);
  memset(info, 0, sizeof(*info));
  info->minrate = info->maxrate = info->freq = (DWORD)g_rate; info->speakers = 2;
  info->latency = (DWORD)((g_ctrl ? g_ctrl[3] + g_target : 0) * 1000 / g_rate); info->minbuf = info->latency;
  ok(); return TRUE;
}
EXPORT BOOL BASS_Start(void) { if (g_ctrl) g_ctrl[4] = 1; ok(); return TRUE; }
EXPORT BOOL BASS_Stop(void) { ok(); return TRUE; }
EXPORT BOOL BASS_Pause(void) { ok(); return TRUE; }
EXPORT BOOL BASS_Update(DWORD length) { (void)length; deliver_syncs(); ok(); return TRUE; }
EXPORT float BASS_GetCPU(void) { return 0; }
EXPORT BOOL BASS_SetVolume(float v) { g_masterVolume = v < 0 ? 0 : v > 1 ? 1 : v; ok(); return TRUE; }
EXPORT float BASS_GetVolume(void) { ok(); return g_masterVolume; }

// --- streams
static Chan* stream_from_memory(uint8_t* data, size_t size, DWORD flags) {
  Chan* c = new_chan(T_DECODER);
  if (!c) { free(data); fail(E_MEM); return NULL; }
  c->data = data; c->size = size; c->flags = flags; c->device = g_curDevice;
  if (!open_decoder(c)) { free_handle(c->handle); close_decoder(c); free(c->data); free(c); fail(E_FILEFORM); return NULL; }
  return c;
}

EXPORT DWORD BASS_StreamCreateFileUser(DWORD system, DWORD flags, const BASS_FILEPROCS* procs, void* user) {
  (void)system;
  if (!procs || !procs->read) return fail(E_ILLPARAM);
  // read everything up-front on the calling (managed) thread; callbacks are never invoked from the mixer thread
  QWORD total = procs->length ? procs->length(user) : 0;
  size_t cap = total > 0 ? (size_t)total : 1 << 20, n = 0;
  uint8_t* buf = (uint8_t*)malloc(cap + 16);
  if (!buf) return fail(E_MEM);
  for (;;) {
    if (n == cap) { cap *= 2; uint8_t* nb = (uint8_t*)realloc(buf, cap + 16); if (!nb) { free(buf); return fail(E_MEM); } buf = nb; }
    DWORD want = (DWORD)(cap - n); if (want > (1 << 20)) want = 1 << 20;
    DWORD got = procs->read(buf + n, want, user);
    if (got == 0 || got == (DWORD)-1) break;
    n += got;
  }
  lock();
  Chan* c = stream_from_memory(buf, n, flags);
  if (c) { c->procs = *procs; c->procsUser = user; c->hasProcs = 1; }
  unlock();
  if (!c) { if (procs->close) procs->close(user); return 0; }
  ok(); return c->handle;
}

EXPORT DWORD BASS_StreamCreateFile(BOOL mem, const void* file, QWORD offset, QWORD length, DWORD flags) {
  uint8_t* data; size_t size;
  if (mem) {
    if (!file || !length) return fail(E_ILLPARAM);
    size = (size_t)length; data = (uint8_t*)malloc(size + 16);
    if (!data) return fail(E_MEM);
    memcpy(data, (const uint8_t*)file + offset, size);
  } else {
    FILE* f = fopen((const char*)file, "rb");
    if (!f) return fail(E_FILEOPEN);
    fseek(f, 0, SEEK_END); long sz = ftell(f);
    if (sz < 0) { fclose(f); return fail(E_FILEOPEN); }
    size = length ? (size_t)length : (size_t)(sz - (long)offset);
    data = (uint8_t*)malloc(size + 16);
    fseek(f, (long)offset, SEEK_SET);
    size = fread(data, 1, size, f); fclose(f);
  }
  lock(); Chan* c = stream_from_memory(data, size, flags); unlock();
  if (!c) return 0;
  ok(); return c->handle;
}

EXPORT DWORD BASS_StreamCreateURL(const char* url, DWORD offset, DWORD flags, void* proc, void* user) { (void)url; (void)offset; (void)flags; (void)proc; (void)user; return fail(E_NOTAVAIL); }

EXPORT DWORD BASS_StreamCreate(DWORD freq, DWORD chans, DWORD flags, void* proc, void* user) {
  (void)proc; (void)user;
  lock();
  Chan* c = new_chan(T_PUSH);
  if (!c) { unlock(); return fail(E_MEM); }
  c->freq = (int)freq; c->chans = (int)chans; c->flags = flags; c->freqAttr = (float)freq; c->device = g_curDevice;
  unlock(); ok(); return c->handle;
}
EXPORT DWORD BASS_StreamPutData(DWORD h, const void* buf, DWORD len) { (void)h; (void)buf; ok(); return len & 0xffffff; }
EXPORT DWORD BASS_StreamPutFileData(DWORD h, const void* buf, DWORD len) { (void)h; (void)buf; ok(); return len; }
EXPORT QWORD BASS_StreamGetFilePosition(DWORD h, DWORD mode) {
  lock(); Chan* c = get_chan(h); QWORD r = (QWORD)-1;
  if (c && c->type == T_DECODER) r = (mode == 2 || mode == 1 || mode == 4) ? c->size : 0;
  unlock(); if (r == (QWORD)-1) fail(E_HANDLE); else ok(); return r;
}

EXPORT BOOL BASS_StreamFree(DWORD h) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  free_chan(c);
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_MusicFree(DWORD h) { return BASS_StreamFree(h); }
EXPORT DWORD BASS_MusicLoad(BOOL mem, const void* f, QWORD o, DWORD l, DWORD fl, DWORD freq) { (void)mem; (void)f; (void)o; (void)l; (void)fl; (void)freq; return fail(E_FILEFORM); }

// --- samples
static float* decode_all(const uint8_t* data, size_t size, int* freq, int* chans, int64_t* frames) {
  Chan tmp; memset(&tmp, 0, sizeof(tmp));
  tmp.data = (uint8_t*)data; tmp.size = size;
  if (!open_decoder(&tmp)) { close_decoder(&tmp); return NULL; }
  int64_t cap = tmp.len > 0 ? tmp.len + 1024 : tmp.freq * 10;
  float* pcm = (float*)malloc(sizeof(float) * cap * 2);
  int64_t n = 0;
  float* raw = (float*)malloc(sizeof(float) * 4096 * (tmp.chans > 2 ? tmp.chans : 2));
  for (;;) {
    int got = dec_raw(&tmp, raw, 4096);
    if (got <= 0) break;
    if (n + got > cap) { cap = (n + got) * 2; pcm = (float*)realloc(pcm, sizeof(float) * cap * 2); }
    to_stereo(raw, pcm + n * 2, got, tmp.chans);
    n += got;
  }
  free(raw);
  *freq = tmp.freq; *chans = tmp.chans; *frames = n;
  close_decoder(&tmp);
  return pcm;
}

EXPORT DWORD BASS_SampleLoad(BOOL mem, const void* file, QWORD offset, DWORD length, DWORD max, DWORD flags) {
  const uint8_t* data; uint8_t* owned = NULL; size_t size;
  if (mem) { data = (const uint8_t*)file + offset; size = length; }
  else {
    FILE* f = fopen((const char*)file, "rb");
    if (!f) return fail(E_FILEOPEN);
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, (long)offset, SEEK_SET);
    size = length ? length : (size_t)(sz - (long)offset);
    owned = (uint8_t*)malloc(size); size = fread(owned, 1, size, f); fclose(f); data = owned;
  }
  int freq, chans; int64_t frames;
  float* pcm = decode_all(data, size, &freq, &chans, &frames); // outside the lock: can take a while
  free(owned);
  if (!pcm) return fail(E_FILEFORM);
  Sample* s = (Sample*)calloc(1, sizeof(Sample));
  s->pcm = pcm; s->frames = frames; s->freq = freq; s->chans = chans; s->flags = flags; s->max = max ? (int)max : 1; s->vol = 1;
  lock(); s->handle = alloc_handle(T_SAMPLE, s); unlock();
  if (!s->handle) { free(pcm); free(s); return fail(E_MEM); }
  ok(); return s->handle;
}

EXPORT DWORD BASS_SampleCreate(DWORD length, DWORD freq, DWORD chans, DWORD max, DWORD flags) {
  Sample* s = (Sample*)calloc(1, sizeof(Sample));
  int bps = sample_bytes(flags);
  s->frames = length / (bps * chans); s->pcm = (float*)calloc((size_t)s->frames * 2 + 2, sizeof(float));
  s->freq = (int)freq; s->chans = (int)chans; s->flags = flags; s->max = max ? (int)max : 1; s->vol = 1;
  lock(); s->handle = alloc_handle(T_SAMPLE, s); unlock();
  ok(); return s->handle;
}
EXPORT BOOL BASS_SampleSetData(DWORD h, const void* buf) {
  lock(); int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  int bps = sample_bytes(s->flags);
  for (int64_t i = 0; i < s->frames; i++) for (int k = 0; k < 2; k++) {
    int ch = s->chans == 1 ? 0 : k; int64_t si = i * s->chans + ch;
    float v = bps == 4 ? ((const float*)buf)[si] : bps == 2 ? ((const int16_t*)buf)[si] / 32768.f : (((const uint8_t*)buf)[si] - 128) / 128.f;
    s->pcm[i * 2 + k] = v;
  }
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_SampleGetData(DWORD h, void* buf) {
  lock(); int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  float* o = (float*)buf;
  for (int64_t i = 0; i < s->frames; i++) { if (s->chans == 1) o[i] = s->pcm[i * 2]; else { o[i * 2] = s->pcm[i * 2]; o[i * 2 + 1] = s->pcm[i * 2 + 1]; } }
  unlock(); ok(); return TRUE;
}

EXPORT BOOL BASS_SampleFree(DWORD h) {
  lock();
  int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  // free channels of this sample
  for (int i = 1; i < MAXH; i++) if (g_slots[i].type == T_SAMPCHAN && ((Chan*)g_slots[i].p)->sample == s) free_chan((Chan*)g_slots[i].p);
  free_handle(h); voice_release_pcm(s->pcm, s->frames); free(s->pcm); free(s);
  unlock(); ok(); return TRUE;
}

EXPORT BOOL BASS_SampleGetInfo(DWORD h, BASS_SAMPLE* info) {
  lock(); int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  memset(info, 0, sizeof(*info));
  info->freq = (DWORD)s->freq; info->volume = s->vol; info->pan = s->pan; info->flags = s->flags | F_FLOAT;
  info->length = (DWORD)(s->frames * s->chans * 4); info->max = (DWORD)s->max; info->origres = 32; info->chans = (DWORD)s->chans;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_SampleSetInfo(DWORD h, const BASS_SAMPLE* info) {
  lock(); int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  s->freq = (int)info->freq; s->vol = info->volume; s->pan = info->pan; s->max = info->max ? (int)info->max : 1;
  unlock(); ok(); return TRUE;
}

EXPORT DWORD BASS_SampleGetChannel(DWORD h, DWORD flags) {
  lock();
  int t; Sample* s = (Sample*)get_handle(h, &t);
  if (!s || t != T_SAMPLE) { unlock(); return fail(E_HANDLE); }
  Chan* c = new_chan(T_SAMPCHAN);
  if (!c) { unlock(); return fail(E_MEM); }
  c->sample = s; s->nchan++;
  c->freq = s->freq; c->freqAttr = (float)s->freq; c->chans = s->chans; c->vol = s->vol; c->pan = s->pan; c->device = g_curDevice;
  c->flags = (s->flags & ~(F_DECODE)) | ((flags & F_SAMCHAN_STREAM) ? F_DECODE & flags : 0) | (flags & (F_DECODE | F_LOOP)) | F_FLOAT;
  if (!(flags & F_DECODE)) c->flags &= ~F_DECODE;
  c->len = s->frames;
  unlock(); ok(); return c->handle;
}
EXPORT DWORD BASS_SampleGetChannels(DWORD h, DWORD* chans) {
  lock(); int n = 0;
  for (int i = 1; i < MAXH; i++) if (g_slots[i].type == T_SAMPCHAN && ((Chan*)g_slots[i].p)->sample && ((Chan*)g_slots[i].p)->sample->handle == h) { if (chans) chans[n] = ((Chan*)g_slots[i].p)->handle; n++; }
  unlock(); ok(); return (DWORD)n;
}
EXPORT BOOL BASS_SampleStop(DWORD h) {
  lock();
  for (int i = 1; i < MAXH; i++) if (g_slots[i].type == T_SAMPCHAN && ((Chan*)g_slots[i].p)->sample && ((Chan*)g_slots[i].p)->sample->handle == h) ((Chan*)g_slots[i].p)->playing = 0;
  unlock(); ok(); return TRUE;
}

// --- channel info / attributes
EXPORT BOOL BASS_ChannelGetInfo(DWORD h, BASS_CHANNELINFO* info) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { int t; Sample* s = (Sample*)get_handle(h, &t); if (s && t == T_SAMPLE) { memset(info, 0, sizeof(*info)); info->freq = s->freq; info->chans = s->chans; info->flags = s->flags | F_FLOAT; info->sample = h; unlock(); ok(); return TRUE; } unlock(); return fail(E_HANDLE); }
  memset(info, 0, sizeof(*info));
  Chan* r = root_of(c);
  info->freq = (DWORD)(c->type == T_MIXER ? c->freq : r->freq);
  info->chans = (DWORD)(c->type == T_MIXER ? c->chans : r->chans);
  info->flags = c->flags;
  info->ctype = c->type == T_MIXER ? 0x10800 : (r->fmt == FMT_MP3 ? 0x10005 : r->fmt == FMT_OGG ? 0x10002 : r->fmt == FMT_WAV ? 0x40001 : r->fmt == FMT_FLAC ? 0x10900 : 0x10000);
  info->origres = 16;
  info->sample = c->type == T_SAMPCHAN && c->sample ? c->sample->handle : 0;
  unlock(); ok(); return TRUE;
}

EXPORT BOOL BASS_ChannelSetAttribute(DWORD h, DWORD attrib, float value) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  pthread_mutex_lock(&c->m);
  BOOL r = TRUE;
  switch (attrib) {
    case A_FREQ: c->freqAttr = value <= 0 ? (float)root_of(c)->freq : value; break;
    case A_VOL: c->vol = value < 0 ? 0 : value; break;
    case A_PAN: c->pan = value; break;
    case A_TEMPO: if (c->type == T_TEMPO) c->tempo = value < -95 ? -95 : value > 5000 ? 5000 : value; else r = FALSE; break;
    case A_TEMPO_PITCH: if (c->type == T_TEMPO) c->pitch = value; else r = FALSE; break;
    case A_TEMPO_FREQ: if (c->type == T_TEMPO) { c->tempoFreq = value; c->freqAttr = value > 0 ? value : (float)root_of(c)->freq; } else r = FALSE; break;
    case A_REVERSE_DIR: if (c->type == T_REVERSE) c->dir = value < 0 ? -1 : 1; else r = FALSE; break;
    default: break; // tempo algorithm options, buffer, noramp etc. are accepted and ignored
  }
  if (c->voice) voice_update(c);
  pthread_mutex_unlock(&c->m);
  unlock();
  if (!r) return fail(E_ILLTYPE);
  ok(); return TRUE;
}

EXPORT BOOL BASS_ChannelGetAttribute(DWORD h, DWORD attrib, float* value) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  float v = 0;
  switch (attrib) {
    case A_FREQ: v = c->freqAttr > 0 ? c->freqAttr : (float)root_of(c)->freq; break;
    case A_VOL: v = c->vol; break;
    case A_PAN: v = c->pan; break;
    case A_BITRATE: v = (float)root_of(c)->bitrate; break;
    case A_CPU: v = 0; break;
    case A_TEMPO: v = c->tempo; break;
    case A_TEMPO_PITCH: v = c->pitch; break;
    case A_TEMPO_FREQ: v = c->tempoFreq > 0 ? c->tempoFreq : (float)root_of(c)->freq; break;
    case A_REVERSE_DIR: v = (float)c->dir; break;
    case A_BUFFER: v = 0; break;
  }
  if (value) *value = v;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_ChannelSetAttributeEx(DWORD h, DWORD a, void* v, DWORD s) { (void)h; (void)a; (void)v; (void)s; return fail(E_ILLTYPE); }
EXPORT DWORD BASS_ChannelGetAttributeEx(DWORD h, DWORD a, void* v, DWORD s) { (void)h; (void)a; (void)v; (void)s; fail(E_ILLTYPE); return 0; }
EXPORT BOOL BASS_ChannelSlideAttribute(DWORD h, DWORD a, float v, DWORD t) { (void)t; return BASS_ChannelSetAttribute(h, a, v); }
EXPORT BOOL BASS_ChannelIsSliding(DWORD h, DWORD a) { (void)h; (void)a; return FALSE; }

EXPORT DWORD BASS_ChannelFlags(DWORD h, DWORD flags, DWORD mask) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); fail(E_HANDLE); return (DWORD)-1; }
  c->flags = (c->flags & ~mask) | (flags & mask);
  // propagate loop to the root decoder so tempo/reverse chains loop seamlessly
  if (mask & F_LOOP) { Chan* r = root_of(c); if (r != c) r->flags = (r->flags & ~F_LOOP) | (flags & F_LOOP); }
  DWORD f = c->flags;
  unlock(); ok(); return f;
}

EXPORT BOOL BASS_ChannelSetDevice(DWORD h, DWORD device) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  c->device = (int)device; unlock(); ok(); return TRUE;
}
EXPORT DWORD BASS_ChannelGetDevice(DWORD h) { lock(); Chan* c = get_chan(h); DWORD d = c ? (DWORD)c->device : (DWORD)-1; unlock(); if (!c) fail(E_HANDLE); else ok(); return d; }

// --- byte <-> seconds
EXPORT double BASS_ChannelBytes2Seconds(DWORD h, QWORD pos) {
  lock();
  Chan* c = get_chan(h);
  double r = -1;
  if (c) { Chan* rt = c->type == T_MIXER ? c : root_of(c); int b = (c->type == T_MIXER ? 2 : rt->chans) * sample_bytes(c->flags); r = (double)pos / b / (rt->freq > 0 ? rt->freq : 44100); }
  else { int t; Sample* s = (Sample*)get_handle(h, &t); if (s && t == T_SAMPLE) r = (double)pos / (s->chans * 4) / s->freq; }
  unlock();
  if (r < 0) fail(E_HANDLE); else ok();
  return r;
}
EXPORT QWORD BASS_ChannelSeconds2Bytes(DWORD h, double secs) {
  lock();
  Chan* c = get_chan(h);
  QWORD r = (QWORD)-1;
  if (c) { Chan* rt = c->type == T_MIXER ? c : root_of(c); int b = (c->type == T_MIXER ? 2 : rt->chans) * sample_bytes(c->flags); r = (QWORD)((int64_t)(secs * (rt->freq > 0 ? rt->freq : 44100)) * b); }
  else { int t; Sample* s = (Sample*)get_handle(h, &t); if (s && t == T_SAMPLE) r = (QWORD)((int64_t)(secs * s->freq) * s->chans * 4); }
  unlock();
  if (r == (QWORD)-1) fail(E_HANDLE); else ok();
  return r;
}

EXPORT QWORD BASS_ChannelGetLength(DWORD h, DWORD mode) {
  (void)mode;
  lock();
  Chan* c = get_chan(h);
  QWORD r = (QWORD)-1;
  if (c && c->type != T_MIXER && c->type != T_PUSH) { Chan* rt = root_of(c); int64_t len = rt->type == T_SAMPCHAN ? rt->len : rt->len; r = (QWORD)(len * rt->chans * sample_bytes(c->flags)); }
  else if (!c) { int t; Sample* s = (Sample*)get_handle(h, &t); if (s && t == T_SAMPLE) r = (QWORD)(s->frames * s->chans * 4); }
  unlock();
  if (r == (QWORD)-1) fail(c ? E_NOTAVAIL : E_HANDLE); else ok();
  return r;
}

// --- position
static QWORD pos_to_bytes(Chan* c, double frames) {
  if (frames < 0) frames = 0;
  Chan* rt = c->type == T_MIXER ? c : root_of(c);
  int ch = c->type == T_MIXER ? 2 : rt->chans;
  return (QWORD)((int64_t)frames * ch * sample_bytes(c->flags));
}
static int64_t bytes_to_frames(Chan* c, QWORD bytes) {
  Chan* rt = c->type == T_MIXER ? c : root_of(c);
  int ch = c->type == T_MIXER ? 2 : rt->chans;
  return (int64_t)(bytes / (QWORD)(ch * sample_bytes(c->flags)));
}

EXPORT QWORD BASS_ChannelGetPosition(DWORD h, DWORD mode) {
  deliver_syncs();
  if ((mode & 0xff) != POS_BYTE) { fail(E_NOTAVAIL); return (QWORD)-1; }
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); fail(E_HANDLE); return (QWORD)-1; }
  pthread_mutex_lock(&c->m);
  QWORD r = pos_to_bytes(c, chan_pos(c));
  pthread_mutex_unlock(&c->m);
  unlock(); ok(); return r;
}

EXPORT BOOL BASS_ChannelSetPosition(DWORD h, QWORD pos, DWORD mode) {
  if ((mode & 0xff) != POS_BYTE) return fail(E_NOTAVAIL);
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  int64_t f = bytes_to_frames(c, pos);
  Chan* rt = root_of(c);
  if (rt->len > 0 && f > rt->len) { unlock(); return fail(E_POSITION); }
  chan_seek(c, f);
  c->ended = 0;
  if (c->mixer) kid_reset(c);
  unlock(); ok(); return TRUE;
}

EXPORT DWORD BASS_ChannelIsActive(DWORD h) {
  deliver_syncs();
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); fail(E_HANDLE); return ACTIVE_STOPPED; }
  DWORD r;
  if (c->flags & F_DECODE) r = (c->ended && c->type != T_MIXER) ? ACTIVE_STOPPED : ACTIVE_PLAYING;
  else r = c->playing == 1 ? ACTIVE_PLAYING : c->playing == 2 ? ACTIVE_PAUSED : ACTIVE_STOPPED;
  unlock(); ok(); return r;
}

EXPORT BOOL BASS_ChannelPlay(DWORD h, BOOL restart) {
  lock();
  Chan* c = get_chan(h);
  if (!c) {
    int t; Sample* s = (Sample*)get_handle(h, &t);
    unlock();
    if (s && t == T_SAMPLE) { DWORD ch = BASS_SampleGetChannel(h, 0); return ch ? BASS_ChannelPlay(ch, FALSE) : FALSE; }
    return fail(E_HANDLE);
  }
  if (c->flags & F_DECODE) { unlock(); return fail(E_DECODE); }
  if (restart) { chan_seek(c, 0); c->ended = 0; }
  if (c->ended && c->type != T_MIXER) { chan_seek(c, 0); c->ended = 0; }
  c->playing = 1;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_ChannelPause(DWORD h) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  if (c->flags & F_DECODE) { unlock(); return fail(E_DECODE); }
  if (c->playing != 1) { unlock(); return fail(E_NOPLAY); }
  c->playing = 2; unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_ChannelStop(DWORD h) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  c->playing = 0;
  queue_syncs(c, SYNC_STOP, 0, 0);
  if (c->flags & F_AUTOFREE) free_chan(c);
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_ChannelUpdate(DWORD h, DWORD length) { (void)h; (void)length; deliver_syncs(); ok(); return TRUE; }
EXPORT BOOL BASS_ChannelLock(DWORD h, BOOL l) {
  Chan* c; lock(); c = get_chan(h); unlock();
  if (!c) return fail(E_HANDLE);
  if (l) pthread_mutex_lock(&c->m); else pthread_mutex_unlock(&c->m);
  ok(); return TRUE;
}

// --- levels / data
static void fft_mag(const float* in, int n, float* out, DWORD flags) {
  // radix-2 complex FFT on n real samples (Hann window unless disabled); out gets n/2 magnitudes
  float* re = (float*)malloc(sizeof(float) * n); float* im = (float*)calloc(n, sizeof(float));
  double mean = 0;
  if (flags & DATA_FFT_REMOVEDC) { for (int i = 0; i < n; i++) mean += in[i]; mean /= n; }
  for (int i = 0; i < n; i++) {
    float w = (flags & DATA_FFT_NOWINDOW) ? 1.f : 0.5f * (1 - cosf(2 * (float)M_PI * i / (n - 1)));
    re[i] = (float)(in[i] - mean) * w;
  }
  for (int i = 1, j = 0; i < n; i++) {
    int bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; }
  }
  for (int len = 2; len <= n; len <<= 1) {
    double ang = -2 * M_PI / len; float wr = (float)cos(ang), wi = (float)sin(ang);
    for (int i = 0; i < n; i += len) {
      float cr = 1, ci = 0;
      for (int j = 0; j < len / 2; j++) {
        float ur = re[i + j], ui = im[i + j];
        float vr = re[i + j + len / 2] * cr - im[i + j + len / 2] * ci, vi = re[i + j + len / 2] * ci + im[i + j + len / 2] * cr;
        re[i + j] = ur + vr; im[i + j] = ui + vi; re[i + j + len / 2] = ur - vr; im[i + j + len / 2] = ui - vi;
        float nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
      }
    }
  }
  float scale = 2.f / n;
  for (int i = 0; i < n / 2; i++) out[i] = sqrtf(re[i] * re[i] + im[i] * im[i]) * scale;
  free(re); free(im);
}

static int fft_size(DWORD length) { return 256 << (length & 0x7); }

// window of `frames` frames ending at the heard position from the level history (stereo)
static int lvl_window(Chan* c, float* out, int frames) {
  if (!c->lvl) { c->lvl = (float*)calloc(LVL_FRAMES * 2, sizeof(float)); c->lvlEnd = g_mixG; return 0; }
  int64_t end = heard_g(); if (end > c->lvlEnd) end = c->lvlEnd;
  int64_t start = end - frames;
  int64_t oldest = c->lvlEnd - LVL_FRAMES + BLOCK;
  for (int i = 0; i < frames; i++) {
    int64_t g = start + i;
    if (g < oldest || g < 0) { out[i * 2] = out[i * 2 + 1] = 0; continue; }
    int idx = (int)(g & (LVL_FRAMES - 1));
    out[i * 2] = c->lvl[idx * 2]; out[i * 2 + 1] = c->lvl[idx * 2 + 1];
  }
  return frames;
}

static DWORD get_data_live(Chan* c, void* buffer, DWORD length) {
  if (length == DATA_AVAILABLE) return 0;
  if (length & DATA_FFT256) {
    int n = fft_size(length);
    float* w = (float*)malloc(sizeof(float) * n * 2); float* mono = (float*)malloc(sizeof(float) * n);
    lvl_window(c, w, n);
    if (length & DATA_FFT_INDIVIDUAL) {
      float* o = (float*)buffer;
      float* tmp = (float*)malloc(sizeof(float) * n / 2);
      for (int ch = 0; ch < 2; ch++) { for (int i = 0; i < n; i++) mono[i] = w[i * 2 + ch]; fft_mag(mono, n, tmp, length); for (int i = 0; i < n / 2; i++) o[i * 2 + ch] = tmp[i]; }
      free(tmp);
    } else {
      for (int i = 0; i < n; i++) mono[i] = 0.5f * (w[i * 2] + w[i * 2 + 1]);
      fft_mag(mono, n, (float*)buffer, length);
    }
    free(w); free(mono);
    return (DWORD)(n * 2 * 4);
  }
  int bytes = (int)(length & 0x0fffffff);
  int isFloat = (length & DATA_FLOAT) || (c->flags & F_FLOAT);
  int bps = isFloat ? 4 : 2;
  int frames = bytes / (2 * bps);
  if (frames <= 0) return 0;
  float* w = (float*)malloc(sizeof(float) * frames * 2);
  lvl_window(c, w, frames);
  if (isFloat) memcpy(buffer, w, sizeof(float) * frames * 2);
  else { int16_t* o = (int16_t*)buffer; for (int i = 0; i < frames * 2; i++) { float v = w[i] * 32768.f; o[i] = (int16_t)(v > 32767 ? 32767 : v < -32768 ? -32768 : v); } }
  free(w);
  return (DWORD)(frames * 2 * bps);
}

static DWORD get_data_decode(Chan* c, void* buffer, DWORD length) {
  Chan* rt = root_of(c);
  int ch = rt->chans;
  if (length == DATA_AVAILABLE) return 0;
  if (length & DATA_FFT256) {
    int n = fft_size(length);
    float* st = (float*)malloc(sizeof(float) * n * 2);
    int got = chan_read(c, st, n);
    if (got <= 0) { free(st); fail(E_ENDED); return (DWORD)-1; }
    if (got < n) memset(st + got * 2, 0, sizeof(float) * (n - got) * 2);
    float* mono = (float*)malloc(sizeof(float) * n);
    for (int i = 0; i < n; i++) mono[i] = 0.5f * (st[i * 2] + st[i * 2 + 1]);
    fft_mag(mono, n, (float*)buffer, length);
    free(st); free(mono);
    return (DWORD)(n * ch * 4);
  }
  int isFloat = (length & DATA_FLOAT) || (c->flags & F_FLOAT);
  int bps = isFloat ? 4 : (c->flags & F_BYTE) ? 1 : 2;
  int bytes = (int)(length & 0x0fffffff);
  int frames = bytes / (ch * bps);
  if (frames <= 0) return 0;
  float* st = (float*)malloc(sizeof(float) * frames * 2);
  int got = chan_read(c, st, frames);
  if (got <= 0) { free(st); fail(E_ENDED); return (DWORD)-1; }
  for (int i = 0; i < got; i++) for (int k = 0; k < ch; k++) {
    float v = ch == 1 ? 0.5f * (st[i * 2] + st[i * 2 + 1]) : (k < 2 ? st[i * 2 + k] : 0);
    if (bps == 4) ((float*)buffer)[i * ch + k] = v;
    else if (bps == 2) { float s = v * 32768.f; ((int16_t*)buffer)[i * ch + k] = (int16_t)(s > 32767 ? 32767 : s < -32768 ? -32768 : s); }
    else { float s = v * 128.f + 128.f; ((uint8_t*)buffer)[i * ch + k] = (uint8_t)(s > 255 ? 255 : s < 0 ? 0 : s); }
  }
  free(st);
  return (DWORD)(got * ch * bps);
}

EXPORT DWORD BASS_ChannelGetData(DWORD h, void* buffer, DWORD length) {
  lock();
  Chan* c = get_chan(h);
  if (!c) { unlock(); fail(E_HANDLE); return (DWORD)-1; }
  int decodeOnly = (c->flags & F_DECODE) && !c->mixer;
  if (decodeOnly) {
    // decode-only stream (e.g. waveform generation): decode without holding the global lock
    pthread_mutex_lock(&c->m);
    unlock();
    DWORD r = get_data_decode(c, buffer, length);
    pthread_mutex_unlock(&c->m);
    if (r != (DWORD)-1) ok();
    return r;
  }
  DWORD r = get_data_live(c, buffer, length);
  unlock(); ok(); return r;
}

static void level_calc(Chan* c, float* levels, float seconds, DWORD flags) {
  int frames = (int)(seconds * (c->mixer ? c->mixer->freq : g_rate)); if (frames < 1) frames = 1; if (frames > LVL_FRAMES - BLOCK) frames = LVL_FRAMES - BLOCK;
  float* w = (float*)malloc(sizeof(float) * frames * 2);
  lvl_window(c, w, frames);
  double a[2] = { 0, 0 };
  for (int i = 0; i < frames; i++) for (int k = 0; k < 2; k++) {
    double v = fabs(w[i * 2 + k]);
    if (flags & LEVEL_RMS) a[k] += v * v; else if (v > a[k]) a[k] = v;
  }
  if (flags & LEVEL_RMS) { a[0] = sqrt(a[0] / frames); a[1] = sqrt(a[1] / frames); }
  if (flags & LEVEL_MONO) levels[0] = (float)((flags & LEVEL_RMS) ? sqrt((a[0] * a[0] + a[1] * a[1]) / 2) : (a[0] > a[1] ? a[0] : a[1]));
  else { levels[0] = (float)a[0]; levels[1] = (float)a[1]; }
  free(w);
}

EXPORT BOOL BASS_ChannelGetLevelEx(DWORD h, float* levels, float length, DWORD flags) {
  deliver_syncs();
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  level_calc(c, levels, length, flags);
  unlock(); ok(); return TRUE;
}
EXPORT DWORD BASS_ChannelGetLevel(DWORD h) {
  float l[2] = { 0, 0 };
  if (!BASS_ChannelGetLevelEx(h, l, 0.02f, LEVEL_STEREO)) return (DWORD)-1;
  DWORD L = (DWORD)(fminf(l[0], 1.f) * 32768), R = (DWORD)(fminf(l[1], 1.f) * 32768);
  return L | (R << 16);
}

// --- syncs
static DWORD add_sync(Chan* c, DWORD type, QWORD param, SYNCPROC proc, void* user, int mixer) {
  Sync* s = (Sync*)calloc(1, sizeof(Sync));
  s->type = type; s->param = param; s->proc = proc; s->user = user; s->owner = c; s->mixer = mixer;
  s->handle = alloc_handle(T_SYNC, s);
  s->next = c->syncs; c->syncs = s;
  return s->handle;
}
static BOOL remove_sync(DWORD h, DWORD sync) {
  Chan* c = get_chan(h);
  if (!c) return fail(E_HANDLE);
  for (Sync** pp = &c->syncs; *pp; pp = &(*pp)->next) if ((*pp)->handle == sync) {
    Sync* s = *pp; *pp = s->next;
    int w = 0; for (int i = 0; i < g_npending; i++) if (g_pending[i].sync != sync) g_pending[w++] = g_pending[i];
    g_npending = w;
    free_handle(s->handle); free(s); ok(); return TRUE;
  }
  return fail(E_HANDLE);
}
EXPORT DWORD BASS_ChannelSetSync(DWORD h, DWORD type, QWORD param, SYNCPROC proc, void* user) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  DWORD r = add_sync(c, type, param, proc, user, 0);
  unlock(); ok(); return r;
}
EXPORT BOOL BASS_ChannelRemoveSync(DWORD h, DWORD sync) { lock(); BOOL r = remove_sync(h, sync); unlock(); return r; }

EXPORT DWORD BASS_ChannelSetDSP(DWORD h, void* proc, void* user, int priority) { (void)h; (void)proc; (void)user; (void)priority; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_ChannelRemoveDSP(DWORD h, DWORD dsp) { (void)h; (void)dsp; return fail(E_HANDLE); }
EXPORT BOOL BASS_ChannelSetLink(DWORD h, DWORD c) { (void)h; (void)c; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_ChannelRemoveLink(DWORD h, DWORD c) { (void)h; (void)c; return fail(E_HANDLE); }
EXPORT const char* BASS_ChannelGetTags(DWORD h, DWORD tags) { (void)h; (void)tags; fail(E_NOTAVAIL); return NULL; }

// --- FX
EXPORT DWORD BASS_ChannelSetFX(DWORD h, DWORD type, int priority) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  Fx* f = (Fx*)calloc(1, sizeof(Fx));
  f->type = (int)type; f->priority = priority; f->owner = c; f->dirty = 1; f->volume = 1;
  f->bqf.fCenter = 1000; f->bqf.fQ = 0.7071f; f->bqf.lChannel = -1;
  f->handle = alloc_handle(T_FX, f);
  // insert by descending priority
  Fx** pp = &c->fx;
  while (*pp && (*pp)->priority >= priority) pp = &(*pp)->next;
  f->next = *pp; *pp = f;
  unlock(); ok(); return f->handle;
}
EXPORT BOOL BASS_ChannelRemoveFX(DWORD h, DWORD fx) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  for (Fx** pp = &c->fx; *pp; pp = &(*pp)->next) if ((*pp)->handle == fx) { Fx* f = *pp; *pp = f->next; free_handle(f->handle); free(f); unlock(); ok(); return TRUE; }
  unlock(); return fail(E_HANDLE);
}
EXPORT BOOL BASS_FXSetParameters(DWORD fx, const void* params) {
  lock(); int t; Fx* f = (Fx*)get_handle(fx, &t);
  if (!f || t != T_FX) { unlock(); return fail(E_HANDLE); }
  if (f->type == FX_BQF) f->bqf = *(const BASS_BFX_BQF*)params;
  else if (f->type == FX_PEAKEQ) f->peq = *(const BASS_BFX_PEAKEQ*)params;
  else if (f->type == FX_VOLUME) f->volume = ((const float*)params)[1];
  f->dirty = 1;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_FXGetParameters(DWORD fx, void* params) {
  lock(); int t; Fx* f = (Fx*)get_handle(fx, &t);
  if (!f || t != T_FX) { unlock(); return fail(E_HANDLE); }
  if (f->type == FX_BQF) *(BASS_BFX_BQF*)params = f->bqf;
  else if (f->type == FX_PEAKEQ) *(BASS_BFX_PEAKEQ*)params = f->peq;
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_FXReset(DWORD h) {
  lock(); int t; void* p = get_handle(h, &t);
  if (t == T_FX && p) { Fx* f = (Fx*)p; memset(f->z1, 0, sizeof f->z1); memset(f->z2, 0, sizeof f->z2); }
  else if (p) { for (Fx* f = ((Chan*)p)->fx; f; f = f->next) { memset(f->z1, 0, sizeof f->z1); memset(f->z2, 0, sizeof f->z2); } }
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_FXSetPriority(DWORD fx, int priority) {
  lock(); int t; Fx* f = (Fx*)get_handle(fx, &t);
  if (!f || t != T_FX) { unlock(); return fail(E_HANDLE); }
  f->priority = priority; unlock(); ok(); return TRUE;
}

// --- unsupported legacy features (recording, 3D, plugins)
EXPORT BOOL BASS_RecordInit(int d) { (void)d; return fail(E_DEVICE); }
EXPORT BOOL BASS_RecordFree(void) { return fail(E_INIT); }
EXPORT BOOL BASS_RecordGetDeviceInfo(DWORD d, BASS_DEVICEINFO* i) { (void)d; (void)i; return fail(E_DEVICE); }
EXPORT BOOL BASS_RecordSetDevice(DWORD d) { (void)d; return fail(E_DEVICE); }
EXPORT DWORD BASS_RecordGetDevice(void) { fail(E_INIT); return (DWORD)-1; }
EXPORT BOOL BASS_RecordGetInfo(void* i) { (void)i; return fail(E_INIT); }
EXPORT DWORD BASS_RecordGetInput(int input, float* volume) { (void)input; (void)volume; fail(E_INIT); return (DWORD)-1; }
EXPORT const char* BASS_RecordGetInputName(int input) { (void)input; fail(E_INIT); return NULL; }
EXPORT BOOL BASS_RecordSetInput(int input, DWORD flags, float volume) { (void)input; (void)flags; (void)volume; return fail(E_INIT); }
EXPORT DWORD BASS_RecordStart(DWORD f, DWORD c, DWORD fl, void* p, void* u) { (void)f; (void)c; (void)fl; (void)p; (void)u; return fail(E_INIT); }
EXPORT BOOL BASS_Apply3D(void) { return TRUE; }
EXPORT BOOL BASS_Set3DFactors(float a, float b, float c) { (void)a; (void)b; (void)c; return TRUE; }
EXPORT BOOL BASS_Get3DFactors(float* a, float* b, float* c) { if (a) *a = 1; if (b) *b = 1; if (c) *c = 1; return TRUE; }
EXPORT BOOL BASS_Set3DPosition(const void* a, const void* b, const void* c, const void* d) { (void)a; (void)b; (void)c; (void)d; return TRUE; }
EXPORT BOOL BASS_Get3DPosition(void* a, void* b, void* c, void* d) { (void)a; (void)b; (void)c; (void)d; return TRUE; }
EXPORT BOOL BASS_ChannelSet3DAttributes(DWORD h, int m, float mi, float ma, int ia, int oa, float ov) { (void)h; (void)m; (void)mi; (void)ma; (void)ia; (void)oa; (void)ov; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_ChannelGet3DAttributes(DWORD h, DWORD* m, float* mi, float* ma, DWORD* ia, DWORD* oa, float* ov) { (void)h; (void)m; (void)mi; (void)ma; (void)ia; (void)oa; (void)ov; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_ChannelSet3DPosition(DWORD h, const void* a, const void* b, const void* c) { (void)h; (void)a; (void)b; (void)c; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_ChannelGet3DPosition(DWORD h, void* a, void* b, void* c) { (void)h; (void)a; (void)b; (void)c; return fail(E_NOTAVAIL); }
EXPORT DWORD BASS_PluginLoad(const char* f, DWORD fl) { (void)f; (void)fl; return fail(E_FILEFORM); }
EXPORT BOOL BASS_PluginFree(DWORD h) { (void)h; return fail(E_HANDLE); }
EXPORT const void* BASS_PluginGetInfo(DWORD h) { (void)h; fail(E_HANDLE); return NULL; }

// ================================================================================================ BASS_FX
EXPORT DWORD BASS_FX_GetVersion(void) { return 0x02041200; }

EXPORT DWORD BASS_FX_TempoCreate(DWORD chan, DWORD flags) {
  lock();
  Chan* s = get_chan(chan);
  if (!s) { unlock(); return fail(E_HANDLE); }
  if (!(s->flags & F_DECODE)) { unlock(); return fail(E_DECODE); }
  Chan* c = new_chan(T_TEMPO);
  if (!c) { unlock(); return fail(E_MEM); }
  c->src = s; c->freeSrc = (flags & F_FX_FREESOURCE) != 0; c->flags = flags & ~F_FX_FREESOURCE; c->device = g_curDevice;
  Chan* r = root_of(s);
  c->freq = r->freq; c->chans = r->chans; c->freqAttr = (float)r->freq; c->len = r->len;
  unlock(); ok(); return c->handle;
}
EXPORT DWORD BASS_FX_TempoGetSource(DWORD h) { lock(); Chan* c = get_chan(h); DWORD r = (c && c->type == T_TEMPO) ? c->src->handle : 0; unlock(); if (!r) fail(E_HANDLE); else ok(); return r; }
EXPORT float BASS_FX_TempoGetRateRatio(DWORD h) { lock(); Chan* c = get_chan(h); float r = (c && c->type == T_TEMPO) ? tempo_ratio(c) * (c->freqAttr / (float)root_of(c)->freq) : 0; unlock(); return r; }

EXPORT DWORD BASS_FX_ReverseCreate(DWORD chan, float decBlock, DWORD flags) {
  (void)decBlock;
  lock();
  Chan* s = get_chan(chan);
  if (!s) { unlock(); return fail(E_HANDLE); }
  Chan* c = new_chan(T_REVERSE);
  if (!c) { unlock(); return fail(E_MEM); }
  c->src = s; c->freeSrc = (flags & F_FX_FREESOURCE) != 0; c->flags = flags & ~F_FX_FREESOURCE; c->device = g_curDevice;
  Chan* r = root_of(s);
  c->freq = r->freq; c->chans = r->chans; c->freqAttr = (float)r->freq; c->len = r->len;
  c->dir = 1; // osu-framework always sets the direction explicitly
  unlock(); ok(); return c->handle;
}
EXPORT DWORD BASS_FX_ReverseGetSource(DWORD h) { lock(); Chan* c = get_chan(h); DWORD r = (c && c->type == T_REVERSE) ? c->src->handle : 0; unlock(); if (!r) fail(E_HANDLE); else ok(); return r; }

EXPORT float BASS_FX_BPM_DecodeGet(DWORD c, double s, double e, DWORD mm, DWORD f, void* p, void* u) { (void)c; (void)s; (void)e; (void)mm; (void)f; (void)p; (void)u; fail(E_NOTAVAIL); return -1; }
EXPORT BOOL BASS_FX_BPM_CallbackSet(DWORD h, void* p, double per, DWORD mm, DWORD f, void* u) { (void)h; (void)p; (void)per; (void)mm; (void)f; (void)u; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_CallbackReset(DWORD h) { (void)h; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_Free(DWORD h) { (void)h; return TRUE; }
EXPORT BOOL BASS_FX_BPM_BeatCallbackSet(DWORD h, void* p, void* u) { (void)h; (void)p; (void)u; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_BeatCallbackReset(DWORD h) { (void)h; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_BeatDecodeGet(DWORD c, double s, double e, DWORD f, void* p, void* u) { (void)c; (void)s; (void)e; (void)f; (void)p; (void)u; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_BeatSetParameters(DWORD h, float b, float c, float r) { (void)h; (void)b; (void)c; (void)r; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_BeatGetParameters(DWORD h, float* b, float* c, float* r) { (void)h; (void)b; (void)c; (void)r; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_FX_BPM_BeatFree(DWORD h) { (void)h; return TRUE; }

// ================================================================================================ BASSmix
EXPORT DWORD BASS_Mixer_GetVersion(void) { return 0x02041000; }

EXPORT DWORD BASS_Mixer_StreamCreate(DWORD freq, DWORD chans, DWORD flags) {
  lock();
  Chan* c = new_chan(T_MIXER);
  if (!c) { unlock(); return fail(E_MEM); }
  c->freq = freq ? (int)freq : g_rate; c->chans = 2; c->flags = flags; c->freqAttr = (float)c->freq; c->device = g_curDevice;
  (void)chans;
  unlock(); ok(); return c->handle;
}

EXPORT BOOL BASS_Mixer_StreamAddChannelEx(DWORD mixer, DWORD chan, DWORD flags, QWORD start, QWORD length) {
  (void)start; (void)length;
  lock();
  Chan* m = get_chan(mixer); Chan* k = get_chan(chan);
  if (!m || m->type != T_MIXER || !k) { unlock(); return fail(E_HANDLE); }
  if (!(k->flags & F_DECODE)) { unlock(); return fail(E_DECODE); }
  if (k->mixer) detach_from_mixer(k);
  pthread_mutex_lock(&m->m);
  if (m->nkids == m->capkids) { m->capkids = m->capkids ? m->capkids * 2 : 16; m->kids = (Chan**)realloc(m->kids, sizeof(Chan*) * m->capkids); }
  m->kids[m->nkids++] = k;
  pthread_mutex_unlock(&m->m);
  k->mixer = m; k->mflags = flags; k->volInit = 0;
  if (!(flags & MIX_CHAN_PAUSE)) probe_request(k);
  kid_reset(k);
  if (!k->lvl) { k->lvl = (float*)calloc(LVL_FRAMES * 2, sizeof(float)); k->lvlEnd = g_mixG; }
  unlock(); ok(); return TRUE;
}
EXPORT BOOL BASS_Mixer_StreamAddChannel(DWORD mixer, DWORD chan, DWORD flags) { return BASS_Mixer_StreamAddChannelEx(mixer, chan, flags, 0, 0); }

EXPORT BOOL BASS_Mixer_ChannelRemove(DWORD h) {
  lock(); Chan* k = get_chan(h);
  if (!k || !k->mixer) { unlock(); return fail(E_HANDLE); }
  detach_from_mixer(k);
  unlock(); ok(); return TRUE;
}

EXPORT DWORD BASS_Mixer_ChannelGetMixer(DWORD h) { lock(); Chan* k = get_chan(h); DWORD r = (k && k->mixer) ? k->mixer->handle : 0; unlock(); if (!r) fail(E_HANDLE); else ok(); return r; }

EXPORT DWORD BASS_Mixer_StreamGetChannels(DWORD mixer, DWORD* chans, DWORD count) {
  lock(); Chan* m = get_chan(mixer);
  if (!m || m->type != T_MIXER) { unlock(); fail(E_HANDLE); return (DWORD)-1; }
  DWORD n = (DWORD)m->nkids;
  if (chans) for (DWORD i = 0; i < n && i < count; i++) chans[i] = m->kids[i]->handle;
  unlock(); ok(); return n;
}

EXPORT DWORD BASS_Mixer_ChannelFlags(DWORD h, DWORD flags, DWORD mask) {
  lock(); Chan* k = get_chan(h);
  if (!k || !k->mixer) { unlock(); fail(E_HANDLE); return (DWORD)-1; }
  DWORD before = k->mflags;
  k->mflags = (k->mflags & ~mask) | (flags & mask);
  // regular channel flags (loop etc.) can also be changed via this function
  DWORD chanMask = mask & (F_LOOP);
  if (chanMask) { k->flags = (k->flags & ~chanMask) | (flags & chanMask); Chan* r = root_of(k); if (r != k) r->flags = (r->flags & ~F_LOOP) | (flags & F_LOOP); }
  if ((before & MIX_CHAN_PAUSE) && !(k->mflags & MIX_CHAN_PAUSE)) probe_request(k);
  else if (!(before & MIX_CHAN_PAUSE) && (k->mflags & MIX_CHAN_PAUSE)) voice_stop(k);
  DWORD f = k->mflags | (k->flags & F_LOOP);
  unlock(); ok(); return f;
}

EXPORT QWORD BASS_Mixer_ChannelGetPositionEx(DWORD h, DWORD mode, DWORD delay) {
  deliver_syncs();
  (void)delay;
  if ((mode & 0xff) != POS_BYTE) { fail(E_NOTAVAIL); return (QWORD)-1; }
  lock();
  Chan* k = get_chan(h);
  if (!k || !k->mixer) { unlock(); fail(E_HANDLE); return (QWORD)-1; }
  pthread_mutex_lock(&k->m);
  double readPoint = chan_pos(k) - (k->rbLen > 0 ? (k->rbLen - k->rpos) * chan_rate(k) : 0);
  double p = readPoint;
  if ((mode & POS_DECODE) == 0 && k->histCount > 0) {
    int64_t heard = heard_g();
    int found = -1;
    for (int i = k->histCount - 1; i >= 0; i--) {
      HistEntry* e = &k->hist[(k->histHead + i) % HIST];
      if (e->g <= heard) { found = i; break; }
    }
    if (found < 0) p = k->hist[k->histHead].pos;
    else {
      HistEntry* e = &k->hist[(k->histHead + found) % HIST];
      p = e->pos + (double)(heard - e->g) * e->rate;
      if (e->rate >= 0 ? p > readPoint : p < readPoint) p = readPoint;
    }
  }
  Chan* r = root_of(k);
  if (r->len > 0 && p > r->len) p = (double)r->len;
  QWORD res = pos_to_bytes(k, p);
  pthread_mutex_unlock(&k->m);
  unlock(); ok(); return res;
}
EXPORT QWORD BASS_Mixer_ChannelGetPosition(DWORD h, DWORD mode) { return BASS_Mixer_ChannelGetPositionEx(h, mode, 0); }

EXPORT BOOL BASS_Mixer_ChannelSetPosition(DWORD h, QWORD pos, DWORD mode) {
  if ((mode & 0xff) != POS_BYTE) return fail(E_NOTAVAIL);
  lock();
  Chan* k = get_chan(h);
  if (!k) { unlock(); return fail(E_HANDLE); }
  int64_t f = bytes_to_frames(k, pos);
  Chan* r = root_of(k);
  if (r->len > 0 && f > r->len) { unlock(); return fail(E_POSITION); }
  chan_seek(k, f);
  k->ended = 0;
  kid_reset(k);
  // drop pending end syncs for this channel: it has been repositioned
  int w = 0; for (int i = 0; i < g_npending; i++) if (g_pending[i].chan != k->handle) g_pending[w++] = g_pending[i];
  g_npending = w;
  unlock(); ok(); return TRUE;
}

EXPORT DWORD BASS_Mixer_ChannelGetLevel(DWORD h) { return BASS_ChannelGetLevel(h); }
EXPORT BOOL BASS_Mixer_ChannelGetLevelEx(DWORD h, float* levels, float length, DWORD flags) { return BASS_ChannelGetLevelEx(h, levels, length, flags); }
EXPORT DWORD BASS_Mixer_ChannelGetData(DWORD h, void* buffer, DWORD length) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); fail(E_HANDLE); return (DWORD)-1; }
  DWORD r = get_data_live(c, buffer, length);
  unlock(); ok(); return r;
}

EXPORT DWORD BASS_Mixer_ChannelSetSync(DWORD h, DWORD type, QWORD param, SYNCPROC proc, void* user) {
  lock(); Chan* c = get_chan(h);
  if (!c) { unlock(); return fail(E_HANDLE); }
  DWORD r = add_sync(c, type, param, proc, user, 1);
  unlock(); ok(); return r;
}
EXPORT BOOL BASS_Mixer_ChannelRemoveSync(DWORD h, DWORD sync) { lock(); BOOL r = remove_sync(h, sync); unlock(); return r; }

EXPORT BOOL BASS_Mixer_ChannelSetMatrix(DWORD h, const void* m) { (void)h; (void)m; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_Mixer_ChannelSetMatrixEx(DWORD h, const void* m, float t) { (void)h; (void)m; (void)t; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_Mixer_ChannelGetMatrix(DWORD h, void* m) { (void)h; (void)m; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_Mixer_ChannelSetEnvelope(DWORD h, DWORD t, const void* n, DWORD c) { (void)h; (void)t; (void)n; (void)c; return fail(E_NOTAVAIL); }
EXPORT BOOL BASS_Mixer_ChannelSetEnvelopePos(DWORD h, DWORD t, QWORD p) { (void)h; (void)t; (void)p; return fail(E_NOTAVAIL); }
EXPORT QWORD BASS_Mixer_ChannelGetEnvelopePos(DWORD h, DWORD t, float* v) { (void)h; (void)t; (void)v; fail(E_NOTAVAIL); return (QWORD)-1; }
EXPORT DWORD BASS_Split_StreamCreate(DWORD c, DWORD f, const int* m) { (void)c; (void)f; (void)m; return fail(E_NOTAVAIL); }
EXPORT DWORD BASS_Split_StreamGetSource(DWORD h) { (void)h; return fail(E_HANDLE); }
EXPORT DWORD BASS_Split_StreamGetSplits(DWORD h, DWORD* s, DWORD c) { (void)h; (void)s; (void)c; fail(E_HANDLE); return (DWORD)-1; }
EXPORT BOOL BASS_Split_StreamReset(DWORD h) { (void)h; return fail(E_HANDLE); }
EXPORT BOOL BASS_Split_StreamResetEx(DWORD h, DWORD o) { (void)h; (void)o; return fail(E_HANDLE); }
EXPORT DWORD BASS_Split_StreamGetAvailable(DWORD h) { (void)h; fail(E_HANDLE); return (DWORD)-1; }

// ================================================================================================ BASSWASAPI (Windows only; never initialised on the web)
EXPORT BOOL BASS_WASAPI_Init(int d, DWORD f, DWORD c, DWORD fl, float b, float p, void* proc, void* u) { (void)d; (void)f; (void)c; (void)fl; (void)b; (void)p; (void)proc; (void)u; return fail(E_DEVICE); }
EXPORT BOOL BASS_WASAPI_Free(void) { return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_GetDeviceInfo(DWORD d, void* i) { (void)d; (void)i; return fail(E_DEVICE); }
EXPORT BOOL BASS_WASAPI_GetInfo(void* i) { (void)i; return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_SetNotify(void* p, void* u) { (void)p; (void)u; return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_Start(void) { return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_Stop(BOOL r) { (void)r; return fail(E_INIT); }
EXPORT DWORD BASS_WASAPI_CheckFormat(int d, DWORD f, DWORD c, DWORD fl) { (void)d; (void)f; (void)c; (void)fl; fail(E_DEVICE); return (DWORD)-1; }
EXPORT float BASS_WASAPI_GetCPU(void) { return 0; }
EXPORT DWORD BASS_WASAPI_GetData(void* b, DWORD l) { (void)b; (void)l; fail(E_INIT); return (DWORD)-1; }
EXPORT DWORD BASS_WASAPI_GetDevice(void) { fail(E_INIT); return (DWORD)-1; }
EXPORT float BASS_WASAPI_GetDeviceLevel(DWORD d, int c) { (void)d; (void)c; return -1; }
EXPORT DWORD BASS_WASAPI_GetLevel(void) { fail(E_INIT); return (DWORD)-1; }
EXPORT BOOL BASS_WASAPI_GetLevelEx(float* l, float len, DWORD f) { (void)l; (void)len; (void)f; return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_GetMute(DWORD m) { (void)m; return FALSE; }
EXPORT DWORD BASS_WASAPI_GetVersion(void) { return 0x02040400; }
EXPORT float BASS_WASAPI_GetVolume(DWORD m) { (void)m; return -1; }
EXPORT BOOL BASS_WASAPI_IsStarted(void) { return FALSE; }
EXPORT BOOL BASS_WASAPI_Lock(BOOL l) { (void)l; return fail(E_INIT); }
EXPORT DWORD BASS_WASAPI_PutData(void* b, DWORD l) { (void)b; (void)l; fail(E_INIT); return (DWORD)-1; }
EXPORT BOOL BASS_WASAPI_SetDevice(DWORD d) { (void)d; return fail(E_DEVICE); }
EXPORT BOOL BASS_WASAPI_SetMute(DWORD m, BOOL v) { (void)m; (void)v; return fail(E_INIT); }
EXPORT BOOL BASS_WASAPI_SetVolume(DWORD m, float v) { (void)m; (void)v; return fail(E_INIT); }
