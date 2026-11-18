// xc-osu! resource pack loader (xc softworks / kudasaixc)
//
// osu.Game.Resources (textures, samples, tracks, fonts: ~130 MB) is published as one pack file instead of inside its
// assembly (tools/respack). The .NET loader would copy the assembly whole into the wasm heap, which never shrinks; here
// the pack stays in Cache Storage and the engine reads single resources out of it (Native/xcweb.c xc_respack_read ->
// read() below -> Blob.slice). A Blob taken from Cache Storage is backed by the browser's disk cache, so the pack costs
// neither wasm nor JS memory.
//
// Layout: "XCRP" | u32 version | u32 index length | index JSON | data (offsets in the index are relative to the data).

const CACHE = 'xc-respack-v1';
// Reads from a disk-backed Blob cost 2-15 ms and up to 100+ ms under load, and some resources are read synchronously by
// the update / audio threads (skin elements). Small entries are therefore all held in memory once the pack is open
// (~1050 entries, 12 MB), and larger ones read again are kept in a small LRU.
const SMALL = 64 * 1024;
// first pages of the UI fonts (80-170 KB each): read synchronously by the update thread as soon as text is drawn
const EAGER = /\.Fonts\.(Torus|Torus_Alternate|Venera|Inter|Noto)\.[^.]+_0\.png$/;
const LRU_BYTES = 24 * 1024 * 1024;

/**
 * @param manifest { file, size } written by tools/respack into js/xc-respack-manifest.js
 * @param opts { fetchFile(url) -> Promise<Response>, onProgress(loaded, total) }
 */
export function openResPack(manifest, opts = {}) {
  const url = new URL('/_framework/' + manifest.file, document.baseURI).href;
  const local = new URL('_framework/' + manifest.file, document.baseURI).href;
  const stats = { source: null, ms: 0, bytes: manifest.size, reads: 0, readBytes: 0, readMs: 0, readMaxMs: 0, pending: 0, error: null };
  let blob = null;
  let dataStart = 0;
  const small = new Map(); // absolute offset -> Uint8Array (entries <= SMALL)
  const lru = new Map(); let lruBytes = 0;
  stats.hits = 0;

  const fetchFile = opts.fetchFile || ((u) => fetch(u, { credentials: 'same-origin' }));

  // counts the body as it streams through, for the launcher's download readout
  function counted(res) {
    if (!res.body || !opts.onProgress) return res;
    let loaded = 0;
    const total = manifest.size;
    const body = res.body.pipeThrough(new TransformStream({
      transform(chunk, ctl) { loaded += chunk.byteLength; opts.onProgress(loaded, total); ctl.enqueue(chunk); },
    }));
    return new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
  }

  const valid = async (b) => {
    if (!b || b.size !== manifest.size) return false;
    const head = new Uint8Array(await b.slice(0, 4).arrayBuffer());
    return head[0] === 0x58 && head[1] === 0x43 && head[2] === 0x52 && head[3] === 0x50; // XCRP
  };

  const ready = (async () => {
    const t0 = performance.now();
    let cache = null;
    try { cache = await caches.open(CACHE); } catch { cache = null; }
    // The cache key is always the root /_framework/ URL: the name is content-fingerprinted, so a test build served from a
    // sub-folder shares the copy the main site already kept.
    if (cache) {
      try {
        const hit = await cache.match(url);
        if (hit) {
          const b = await hit.blob();
          if (await valid(b)) { blob = b; stats.source = 'cache'; }
          else await cache.delete(url);
        }
      } catch { }
    }
    if (!blob) {
      const res = await fetchFile(local);
      if (!res.ok) throw new Error(`resource pack: HTTP ${res.status}`);
      if (cache) {
        try {
          await cache.put(url, counted(res));
          const kept = await cache.match(url);
          const b = kept && await kept.blob();
          if (!(await valid(b))) throw new Error('kept copy is incomplete');
          blob = b; stats.source = 'network+cache';
          for (const k of await cache.keys()) if (k.url !== url) await cache.delete(k);
        } catch (e) {
          console.warn('[respack] could not keep the pack offline (' + e + '), holding it in memory');
          try { await cache.delete(url); } catch { }
        }
      }
      if (!blob) {
        const again = cache ? await fetchFile(local) : res;
        blob = await counted(again).blob();
        if (!(await valid(blob))) throw new Error('resource pack: incomplete download');
        stats.source = 'network';
      }
    }
    // index (header) -> load every small entry in memory, reading the pack in large sequential slices
    const head = new DataView(await blob.slice(0, 12).arrayBuffer());
    const indexLength = head.getUint32(8, true);
    dataStart = 12 + indexLength;
    const index = JSON.parse(new TextDecoder().decode(await blob.slice(12, dataStart).arrayBuffer()));
    const wanted = index.entries.filter((e) => e[2] > 0 && (e[2] <= SMALL || (e[2] <= 512 * 1024 && EAGER.test(e[0])))).map((e) => [dataStart + e[1], e[2]]).sort((a, b) => a[0] - b[0]);
    const s0 = performance.now();
    for (let i = 0; i < wanted.length;) {
      // one slice covers consecutive small entries up to 4 MB apart
      let j = i, end = wanted[i][0] + wanted[i][1];
      while (j + 1 < wanted.length && wanted[j + 1][0] + wanted[j + 1][1] - wanted[i][0] <= 4 * 1048576) { j++; end = wanted[j][0] + wanted[j][1]; }
      const buf = new Uint8Array(await blob.slice(wanted[i][0], end).arrayBuffer());
      for (let k = i; k <= j; k++) small.set(wanted[k][0], buf.slice(wanted[k][0] - wanted[i][0], wanted[k][0] - wanted[i][0] + wanted[k][1]));
      i = j + 1;
    }
    stats.smallMs = Math.round(performance.now() - s0);
    stats.ms = Math.round(performance.now() - t0);
    console.log(`[launch] resource pack ${manifest.file}: ${(manifest.size / 1048576).toFixed(0)} MB from ${stats.source} in ${(stats.ms / 1000).toFixed(1)} s`);
    return true;
  })();
  ready.catch((e) => { stats.error = String(e?.message || e); });

  return {
    manifest, stats, ready,
    async read(offset, length) {
      stats.pending++;
      try {
        await ready;
        const hit = small.get(offset) || lru.get(offset);
        if (hit && hit.byteLength === length) {
          stats.hits++;
          if (lru.has(offset)) { lru.delete(offset); lru.set(offset, hit); }
          return hit.buffer; // every cached entry owns an exactly-sized buffer (slice / copy)
        }
        const t = performance.now();
        const buf = await blob.slice(offset, offset + length).arrayBuffer();
        if (length <= 1048576) {
          lru.set(offset, new Uint8Array(buf)); lruBytes += length;
          for (const [k, v] of lru) { if (lruBytes <= LRU_BYTES) break; lru.delete(k); lruBytes -= v.byteLength; }
        }
        const ms = performance.now() - t;
        stats.reads++; stats.readBytes += buf.byteLength; stats.readMs += ms;
        if (ms > stats.readMaxMs) stats.readMaxMs = ms;
        return buf;
      } finally { stats.pending--; }
    },
    takeStats() {
      const s = { source: stats.source, ms: stats.ms, smallMs: stats.smallMs, hits: stats.hits, reads: stats.reads, bytes: stats.readBytes, readMs: Math.round(stats.readMs), maxMs: Math.round(stats.readMaxMs * 10) / 10, pending: stats.pending };
      if (stats.error) s.error = stats.error;
      stats.reads = 0; stats.readBytes = 0; stats.readMs = 0; stats.readMaxMs = 0; stats.hits = 0;
      return s;
    },
  };
}
