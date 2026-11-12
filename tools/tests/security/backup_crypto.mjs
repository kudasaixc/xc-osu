import { readFile } from 'node:fs/promises';
import { webcrypto } from 'node:crypto';
import assert from 'node:assert/strict';
if (!globalThis.crypto) globalThis.crypto = webcrypto;
const source = await readFile(new URL('../../../osu/osu.Web/wwwroot/js/xc-backup-crypto.js', import.meta.url), 'utf8');
const moduleUrl = 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');
const { encryptBackupStream, decryptBackupStream } = await import(moduleUrl);
const header = new Uint8Array(10); header.set(new TextEncoder().encode('XCOSUBAK')); header[8] = 1; header[9] = 1;
const input = new Uint8Array(150000); for (let i = 0; i < input.length; i++) input[i] = i % 251;
const encrypted = await encryptBackupStream(new Blob([input]).stream(), 'test-password-123', header);
const file = new Blob([encrypted.header, await new Response(encrypted.stream).arrayBuffer()]);
const result = new Uint8Array(await new Response(await decryptBackupStream(file, 'test-password-123')).arrayBuffer());
assert.deepEqual(result, input);
for (const [badFile, password] of [
  [file, 'wrong-password-123'],
  [file.slice(0, file.size - 20), 'test-password-123'],
  [new Blob([file, new Uint8Array([0])]), 'test-password-123'],
]) {
  await assert.rejects(async () => new Response(await decryptBackupStream(badFile, password)).arrayBuffer());
}
const damaged = new Uint8Array(await file.arrayBuffer()); damaged[80] ^= 1;
await assert.rejects(async () => new Response(await decryptBackupStream(new Blob([damaged]), 'test-password-123')).arrayBuffer());
console.log('PASS backup streaming encryption roundtrip, wrong password, truncation, appended data, tampering');
let backupSource = await readFile(new URL('../../../osu/osu.Web/wwwroot/js/xc-backup.js', import.meta.url), 'utf8');
backupSource = backupSource.replace("'./xc-backup-crypto.js'", JSON.stringify(moduleUrl)).replace('async function walk(', 'export async function walk(');
const { walk } = await import('data:text/javascript;base64,' + Buffer.from(backupSource).toString('base64'));
function u32(n) { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, n, true); return b; }
function f64(n) { const b = new Uint8Array(8); new DataView(b.buffer).setFloat64(0, n, true); return b; }
const enc = new TextEncoder(), meta = enc.encode(JSON.stringify({app:'xc-osu!', entries:1}));
const plainHeader = header.slice(); plainHeader[9] = 0;
const name = enc.encode('/xc/test.bin');
const body = new Blob([u32(meta.length), meta, u32(name.length), name, u32(0o100644), f64(Date.now()), new Uint8Array([1]), f64(3), new Uint8Array([1,2,3]), u32(0), u32(1)]);
const legacy = new Blob([plainHeader, body]);
assert.equal((await walk(legacy, false, () => {})).count, 1);
const oversize = new Blob([plainHeader, u32(meta.length), meta, u32(name.length), name, u32(0o100644), f64(Date.now()), new Uint8Array([1]), f64(3 * 1024 ** 3)]);
await assert.rejects(() => walk(oversize, false, () => {}));
const gzip = body.stream().pipeThrough(new CompressionStream('gzip'));
const protectedBackup = await encryptBackupStream(gzip, 'test-password-123', header);
const protectedFile = new Blob([protectedBackup.header, await new Response(protectedBackup.stream).arrayBuffer()]);
assert.equal((await walk(protectedFile, false, () => {}, 'test-password-123')).count, 1);
console.log('PASS legacy backup compatible, oversized entry rejected, encrypted gzip backup validation');
