// xc-osu!: launcher export / import round trip (.xcbak). xc softworks (kudasaixc)
// usage: node tools/backup-test.mjs <base url>
// Seeds the IDBFS database, exports from the launcher, wipes, imports the file back and compares every record; then
// checks that a damaged backup is refused without touching the data. Exit code 0 when everything matches.
import { chromium } from 'playwright';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const base = process.argv[2] || 'http://127.0.0.1:8813';
const browser = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, acceptDownloads: true });
page.on('pageerror', (e) => console.log('[pageerror]', e.message));
page.on('dialog', (d) => d.accept());
let failed = 0;
const check = (ok, what) => { console.log(ok ? 'ok  ' : 'FAIL', what); if (!ok) failed++; };

// Runs in the page: FNV-1a over path, mode, mtime and contents of every record, plus the launcher options.
const snapshot = () => page.evaluate(async () => {
  const dbs = await indexedDB.databases();
  if (!dbs.some((d) => d.name === '/xc')) return { n: 0, sum: 'none', version: 0, opts: localStorage.getItem('xc.launcher.v1') };
  const db = await new Promise((res, rej) => { const r = indexedDB.open('/xc'); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
  const store = db.transaction('FILE_DATA').objectStore('FILE_DATA');
  const index = [...store.indexNames].join(',');
  const keys = await new Promise((res) => { const r = store.getAllKeys(); r.onsuccess = () => res(r.result); });
  let h = 0x811c9dc5, bytes = 0;
  const mix = (b) => { for (let i = 0; i < b.length; i++) { h ^= b[i]; h = Math.imul(h, 0x01000193); } };
  const enc = new TextEncoder();
  for (const k of keys.sort()) {
    const e = await new Promise((res) => { const r = db.transaction('FILE_DATA').objectStore('FILE_DATA').get(k); r.onsuccess = () => res(r.result); });
    mix(enc.encode(`${k}|${e.mode}|${+e.timestamp}|${e.timestamp instanceof Date}|${e.contents ? e.contents.length : 'dir'}|`));
    if (e.contents) { mix(e.contents); bytes += e.contents.length; }
  }
  const version = db.version; db.close();
  return { n: keys.length, sum: (h >>> 0).toString(16), bytes, version, index, opts: localStorage.getItem('xc.launcher.v1') };
});

const openOptions = async () => {
  await page.goto(base + '/');
  await page.waitForSelector('#tab-options', { timeout: 60000 });
  await page.click('#tab-options');
  await page.waitForSelector('#ln-export');
};

try {
  await openOptions();
  check(await page.getAttribute('#ln-site', 'href') === 'https://osu.xcsoftworks.com/web/', 'website link in the rail');

  // nothing stored yet
  await page.evaluate(() => { window.showSaveFilePicker = undefined; });
  await page.click('#ln-export');
  await page.waitForSelector('#ln-backup-status.err');
  check(!(await page.evaluate(async () => (await indexedDB.databases()).some((d) => d.name === '/xc'))), 'export with no data reports it and leaves no database behind');

  // seed: what emscripten's IDBFS writes (version 21, FILE_DATA, timestamp index)
  await page.evaluate(async () => {
    const db = await new Promise((res, rej) => {
      const r = indexedDB.open('/xc', 21);
      r.onupgradeneeded = () => r.result.createObjectStore('FILE_DATA').createIndex('timestamp', 'timestamp', { unique: false });
      r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error);
    });
    const tx = db.transaction('FILE_DATA', 'readwrite'), s = tx.objectStore('FILE_DATA');
    const rnd = (n, seed) => { const b = new Uint8Array(n); let x = seed; for (let i = 0; i < n; i++) { x = (Math.imul(x, 1664525) + 1013904223) >>> 0; b[i] = x >>> 24; } return b; };
    const dir = (p, t) => s.put({ timestamp: new Date(t), mode: 16895 }, p);
    const file = (p, t, c) => s.put({ timestamp: new Date(t), mode: 33206, contents: c }, p);
    dir('/xc/files', 1700000000000); dir('/xc/files/a', 1700000001000); dir('/xc/empty dir', 1700000002000);
    file('/xc/client.realm', 1700000003123, rnd(6 << 20, 1));
    file('/xc/game.ini', 1700000004000, new TextEncoder().encode('Username = someone\nToken = secret\n'.repeat(200)));
    file('/xc/files/a/empty', 1700000005000, new Uint8Array(0));
    file('/xc/files/a/ユーザー é.osu', 1700000006000, rnd(70000, 2));
    for (let i = 0; i < 700; i++) file(`/xc/files/a/${i.toString(16).padStart(4, '0')}`, 1700000010000 + i, rnd(1 + (i * 97) % 9000, 100 + i));
    await new Promise((res, rej) => { tx.oncomplete = res; tx.onerror = () => rej(tx.error); });
    db.close();
    localStorage.setItem('xc.launcher.v1', JSON.stringify({ ...JSON.parse(localStorage.getItem('xc.launcher.v1') || '{}'), lang: 'fr', marker: 'backup-test' }));
  });
  const before = await snapshot();
  check(before.n === 707, `seeded ${before.n} records, ${before.bytes} bytes`);

  // export (download path)
  await openOptions();
  await page.evaluate(() => { window.showSaveFilePicker = undefined; });
  const [download] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), page.click('#ln-export')]);
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'xcbak-')), download.suggestedFilename());
  await download.saveAs(file);
  const raw = fs.readFileSync(file);
  check(/^xc-osu-\d{8}-\d{4}\.xcbak$/.test(path.basename(file)), 'file name ' + path.basename(file));
  check(raw.subarray(0, 8).toString() === 'XCOSUBAK' && raw[8] === 1 && raw[9] === 1 && raw[10] === 0x1f && raw[11] === 0x8b, `container header, ${raw.length} bytes`);
  await page.waitForFunction(() => /\.xcbak/.test(document.querySelector('#ln-backup-status')?.textContent || '') && !document.querySelector('#ln-export').disabled);
  check((await snapshot()).sum === before.sum, 'export leaves the data untouched');

  // a damaged backup is refused before anything is erased
  const bad = file + '.cut.xcbak';
  fs.writeFileSync(bad, raw.subarray(0, raw.length - 4096));
  await page.setInputFiles('#ln-import-file', bad);
  await page.waitForSelector('#ln-backup-status.err', { timeout: 120000 });
  check((await snapshot()).sum === before.sum, 'truncated backup refused, data untouched: ' + await page.textContent('#ln-backup-status'));
  const junk = file + '.junk.xcbak';
  fs.writeFileSync(junk, Buffer.from('not a backup at all, just some text'));
  await page.evaluate(() => { document.querySelector('#ln-backup-status').className = 'backup-status'; });
  await page.setInputFiles('#ln-import-file', junk);
  await page.waitForSelector('#ln-backup-status.err');
  check((await snapshot()).sum === before.sum, 'foreign file refused, data untouched');

  // wipe everything, then restore
  await page.evaluate(async () => {
    await new Promise((res) => { const r = indexedDB.deleteDatabase('/xc'); r.onsuccess = r.onerror = r.onblocked = res; });
    localStorage.removeItem('xc.launcher.v1');
  });
  await openOptions();
  check((await snapshot()).n === 0, 'storage wiped');
  await Promise.all([page.waitForEvent('load', { timeout: 180000 }), page.setInputFiles('#ln-import-file', file)]);
  const after = await snapshot();
  check(after.n === before.n && after.sum === before.sum && after.bytes === before.bytes, `restored ${after.n} records, checksum ${after.sum} (expected ${before.sum})`);
  check(after.version === 21 && after.index === 'timestamp', `database layout IDBFS expects (version ${after.version}, index ${after.index})`);
  check(after.opts === before.opts && /backup-test/.test(after.opts || ''), 'launcher options restored');

  // import over existing, different data replaces it entirely
  await page.evaluate(async () => {
    const db = await new Promise((res) => { const r = indexedDB.open('/xc'); r.onsuccess = () => res(r.result); });
    const tx = db.transaction('FILE_DATA', 'readwrite');
    tx.objectStore('FILE_DATA').put({ timestamp: new Date(), mode: 33206, contents: new Uint8Array(10) }, '/xc/stray');
    await new Promise((res) => { tx.oncomplete = res; }); db.close();
  });
  await openOptions();
  await Promise.all([page.waitForEvent('load', { timeout: 180000 }), page.setInputFiles('#ln-import-file', file)]);
  check((await snapshot()).sum === before.sum, 'import replaces existing data');
  await page.screenshot({ path: process.argv[3] || path.join(path.dirname(file), 'options.png') }).catch(() => { });
} catch (e) { console.log('FAILED', e.message); failed++; } finally { await browser.close(); }
console.log(failed ? `${failed} check(s) failed` : 'all checks passed');
process.exit(failed ? 1 : 0);
