// xc-osu! — export / import of everything this browser stores for the game, as one .xcbak file. xc softworks (kudasaixc)
//
// What is saved: the whole persistent file system (the IDBFS mount at /xc, i.e. the IndexedDB database "/xc": realm
// database, beatmaps, skins, game settings, login) and the launcher options. Both run from the launcher, before the
// runtime mounts the file system, so nothing in this tab holds the database open.
//
// The .xcbak container (all integers little endian):
//   "XCOSUBAK"  8 bytes   magic
//   u8          version   (1)
//   u8          flags     bit 0: the rest of the file is one gzip stream
//   body:
//     u32 n, n bytes      header, UTF-8 JSON: { app, version, created, build, entries, localStorage: { key: value } }
//     per entry:
//       u32 n, n bytes    absolute path, UTF-8 (n > 0)
//       u32               mode (emscripten st_mode: directory or regular file)
//       f64               modification time, ms since the epoch
//       u8                kind: 0 directory, 1 file
//       f64, bytes        file only: length, then the contents
//     u32 0               end of entries
//     u32                 number of entries written
// Files are streamed one at a time in both directions, so a multi-gigabyte library never sits in memory at once.

const DB_NAME = '/xc', DB_STORE = 'FILE_DATA', DB_VERSION = 21; // must match emscripten's IDBFS
const MAGIC = 'XCOSUBAK', VERSION = 1, FLAG_GZIP = 1;
const SETTINGS_KEYS = ['xc.launcher.v1'];
const MAX_NAME = 1 << 16, MAX_HEADER = 1 << 24;

export const BACKUP_EXTENSION = '.xcbak';

export class BackupError extends Error {
  /** `code`: 'empty' | 'format' | 'version' | 'truncated' | 'blocked' | 'unsupported' | 'aborted' */
  constructor(code, message) { super(message || code); this.code = code; }
}

const req = (r) => new Promise((res, rej) => { r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
const done = (tx) => new Promise((res, rej) => { tx.oncomplete = () => res(); tx.onerror = tx.onabort = () => rej(tx.error || new Error('transaction aborted')); });

function open(create) {
  return new Promise((res, rej) => {
    const r = create ? indexedDB.open(DB_NAME, DB_VERSION) : indexedDB.open(DB_NAME);
    let fresh = false;
    r.onupgradeneeded = () => {
      fresh = true;
      if (!create) return;
      const store = r.result.objectStoreNames.contains(DB_STORE) ? r.transaction.objectStore(DB_STORE) : r.result.createObjectStore(DB_STORE);
      if (!store.indexNames.contains('timestamp')) store.createIndex('timestamp', 'timestamp', { unique: false });
    };
    r.onsuccess = () => res({ db: r.result, fresh });
    r.onerror = () => rej(r.error);
    r.onblocked = () => rej(new BackupError('blocked'));
  });
}

function drop() {
  return new Promise((res, rej) => {
    const r = indexedDB.deleteDatabase(DB_NAME);
    r.onsuccess = () => res();
    r.onerror = () => rej(r.error);
    r.onblocked = () => rej(new BackupError('blocked'));
  });
}

const u32 = (v) => { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, v, true); return b; };
const f64 = (v) => { const b = new Uint8Array(8); new DataView(b.buffer).setFloat64(0, v, true); return b; };
const isDir = (mode) => (mode & 0o170000) === 0o040000;
const isFile = (mode) => (mode & 0o170000) === 0o100000;

/**
 * Writes a backup. Call from a click: where the browser has a save dialog it is opened first and the file is streamed
 * straight to disk, elsewhere the file is assembled and handed to the download manager.
 * `onProgress(doneEntries, totalEntries, bytes)`. Resolves to { entries, bytes, name }.
 */
export async function exportBackup({ build = '', onProgress } = {}) {
  if (typeof CompressionStream === 'undefined') throw new BackupError('unsupported');
  const d = new Date(), p2 = (n) => String(n).padStart(2, '0');
  const name = `xc-osu-${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}-${p2(d.getHours())}${p2(d.getMinutes())}${BACKUP_EXTENSION}`;

  let file = null;
  if (window.showSaveFilePicker) {
    try { file = await (await showSaveFilePicker({ suggestedName: name, types: [{ description: 'xc-osu! backup', accept: { 'application/octet-stream': [BACKUP_EXTENSION] } }] })).createWritable(); }
    catch (e) { if (e?.name === 'AbortError') throw new BackupError('aborted'); file = null; }
  }

  const { db, fresh } = await open(false);
  try {
    if (fresh || !db.objectStoreNames.contains(DB_STORE)) {
      // the game never ran here: do not leave an empty database behind
      db.close(); await drop().catch(() => { });
      throw new BackupError('empty');
    }
    const keys = (await req(db.transaction(DB_STORE).objectStore(DB_STORE).getAllKeys())).filter((k) => typeof k === 'string').sort();
    if (!keys.length) throw new BackupError('empty');

    const settings = {};
    for (const k of SETTINGS_KEYS) { try { const v = localStorage.getItem(k); if (v != null) settings[k] = v; } catch { } }
    const enc = new TextEncoder();
    const header = enc.encode(JSON.stringify({ app: 'xc-osu!', version: VERSION, created: d.toISOString(), build, entries: keys.length, localStorage: settings }));

    const preamble = new Uint8Array(10);
    preamble.set(enc.encode(MAGIC)); preamble[8] = VERSION; preamble[9] = FLAG_GZIP;
    const gz = new CompressionStream('gzip'), w = gz.writable.getWriter();
    const parts = [preamble];
    let bytes = preamble.length, sink;
    if (file) {
      await file.write(preamble);
      sink = gz.readable.pipeTo(new WritableStream({ write: (c) => { bytes += c.length; return file.write(c); } }));
    } else {
      sink = gz.readable.pipeTo(new WritableStream({ write: (c) => { bytes += c.length; parts.push(c); } }));
    }
    sink.catch(() => { }); // reported through the awaits below

    let written = 0;
    try {
      await w.write(u32(header.length)); await w.write(header);
      for (let i = 0; i < keys.length; i++) {
        const e = await req(db.transaction(DB_STORE).objectStore(DB_STORE).get(keys[i]));
        if (e && (isDir(e.mode) || (isFile(e.mode) && e.contents))) {
          const path = enc.encode(keys[i]), dir = isDir(e.mode);
          const meta = new Uint8Array(4 + path.length + 4 + 8 + 1 + (dir ? 0 : 8)), v = new DataView(meta.buffer);
          v.setUint32(0, path.length, true); meta.set(path, 4);
          let o = 4 + path.length;
          v.setUint32(o, e.mode >>> 0, true); o += 4;
          v.setFloat64(o, +new Date(e.timestamp) || Date.now(), true); o += 8;
          meta[o++] = dir ? 0 : 1;
          if (!dir) v.setFloat64(o, e.contents.byteLength, true);
          await w.write(meta);
          if (!dir && e.contents.byteLength) await w.write(e.contents instanceof Uint8Array ? e.contents : new Uint8Array(e.contents.buffer || e.contents, e.contents.byteOffset || 0, e.contents.byteLength));
          written++;
        }
        if (i % 16 === 0 || i === keys.length - 1) onProgress?.(i + 1, keys.length, bytes);
      }
      await w.write(u32(0)); await w.write(u32(written));
      await w.close();
      await sink;
      if (file) await file.close();
    } catch (e) {
      try { await w.abort(e); } catch { }
      try { await file?.abort(); } catch { }
      throw e;
    }

    if (!file) {
      const url = URL.createObjectURL(new Blob(parts, { type: 'application/octet-stream' }));
      const a = Object.assign(document.createElement('a'), { href: url, download: name });
      document.body.append(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    }
    return { entries: written, bytes, name };
  } finally { try { db.close(); } catch { } }
}

// Sequential reads over a byte stream.
class Reader {
  constructor(stream) { this.r = stream.getReader(); this.q = []; this.len = 0; }
  async fill(n) {
    while (this.len < n) {
      const { done: end, value } = await this.r.read();
      if (end) throw new BackupError('truncated');
      if (value?.length) { this.q.push(value); this.len += value.length; }
    }
  }
  async read(n) {
    await this.fill(n);
    const out = new Uint8Array(n);
    for (let o = 0; o < n;) {
      const c = this.q[0], take = Math.min(c.length, n - o);
      out.set(c.subarray(0, take), o); o += take;
      if (take === c.length) this.q.shift(); else this.q[0] = c.subarray(take);
    }
    this.len -= n;
    return out;
  }
  async skip(n) {
    while (n > 0) {
      await this.fill(1);
      const c = this.q[0], take = Math.min(c.length, n);
      if (take === c.length) this.q.shift(); else this.q[0] = c.subarray(take);
      this.len -= take; n -= take;
    }
  }
  async u32() { return new DataView((await this.read(4)).buffer).getUint32(0, true); }
  async f64() { return new DataView((await this.read(8)).buffer).getFloat64(0, true); }
  async end() {
    if (this.len) throw new BackupError('format');
    const { done: end, value } = await this.r.read();
    if (!end && value?.length) throw new BackupError('format');
  }
}

// Walks a backup; `entry(e)` receives { path, mode, timestamp, contents? } (contents only when `withContents`).
async function walk(file, withContents, entry) {
  const head = new Uint8Array(await file.slice(0, 10).arrayBuffer());
  if (head.length < 10 || new TextDecoder().decode(head.subarray(0, 8)) !== MAGIC) throw new BackupError('format');
  if (head[8] !== VERSION) throw new BackupError('version');
  let stream = file.slice(10).stream();
  if (head[9] & FLAG_GZIP) {
    if (typeof DecompressionStream === 'undefined') throw new BackupError('unsupported');
    stream = stream.pipeThrough(new DecompressionStream('gzip'));
  }
  const r = new Reader(stream), dec = new TextDecoder('utf-8', { fatal: true });
  try {
    const hl = await r.u32();
    if (!hl || hl > MAX_HEADER) throw new BackupError('format');
    const header = JSON.parse(dec.decode(await r.read(hl)));
    if (header?.app !== 'xc-osu!') throw new BackupError('format');
    let count = 0;
    for (;;) {
      const pl = await r.u32();
      if (!pl) break;
      if (pl > MAX_NAME) throw new BackupError('format');
      const path = dec.decode(await r.read(pl));
      // only the game's own mount, and nothing that climbs out of it
      if (!path.startsWith(DB_NAME + '/') || path.split('/').includes('..')) throw new BackupError('format');
      const mode = await r.u32(), timestamp = await r.f64(), kind = (await r.read(1))[0];
      if (kind > 1 || (kind ? !isFile(mode) : !isDir(mode))) throw new BackupError('format');
      const e = { path, mode, timestamp: new Date(timestamp) };
      if (kind) {
        const size = await r.f64();
        if (!Number.isSafeInteger(size) || size < 0) throw new BackupError('format');
        if (withContents) e.contents = await r.read(size); else await r.skip(size);
        e.size = size;
      }
      await entry(e, ++count, header);
    }
    if (await r.u32() !== count) throw new BackupError('format');
    await r.end();
    return { header, count };
  } catch (e) {
    try { await r.r.cancel(); } catch { }
    if (e instanceof BackupError) throw e;
    throw new BackupError(e instanceof SyntaxError ? 'format' : 'truncated', e?.message); // gzip CRC / cut-off stream
  }
}

/**
 * Replaces this browser's game data with the contents of a backup. The file is read once in full to check it before
 * anything is erased. `onProgress(phase, done, total)` with phase 'check' | 'write'.
 * Resolves to { entries, created, build }; the caller reloads the page.
 */
export async function importBackup(file, { onProgress } = {}) {
  const { header, count } = await walk(file, false, (e, n, h) => { if (n % 64 === 0) onProgress?.('check', n, h.entries || 0); });

  await drop();
  const { db } = await open(true);
  try {
    let batch = [], size = 0;
    const commit = async () => {
      if (!batch.length) return;
      const tx = db.transaction(DB_STORE, 'readwrite'), store = tx.objectStore(DB_STORE), finished = done(tx);
      for (const e of batch) store.put(e.contents ? { timestamp: e.timestamp, mode: e.mode, contents: e.contents } : { timestamp: e.timestamp, mode: e.mode }, e.path);
      batch = []; size = 0;
      await finished;
    };
    await walk(file, true, async (e, n) => {
      batch.push(e); size += e.size || 0;
      if (batch.length >= 256 || size >= 32 << 20) { await commit(); onProgress?.('write', n, count); }
    });
    await commit();
    onProgress?.('write', count, count);
  } catch (e) {
    // a half-written file system would boot into a broken game: leave an empty one instead
    try { db.close(); } catch { }
    await drop().catch(() => { });
    throw e;
  } finally { try { db.close(); } catch { } }

  const settings = header.localStorage && typeof header.localStorage === 'object' ? header.localStorage : {};
  for (const k of SETTINGS_KEYS) {
    try { if (typeof settings[k] === 'string') localStorage.setItem(k, settings[k]); } catch { }
  }
  try { localStorage.removeItem('xc.lastBoot'); } catch { }
  return { entries: count, created: header.created, build: header.build };
}
