// Streaming AES-GCM records. Each record binds the file header and its sequence.
// An authenticated empty final record prevents truncation. Memory stays bounded.
const CHUNK = 65536;
async function keyFor(password, salt) {
  if (password.length < 8 || password.length > 256) throw new Error('Le mot de passe doit contenir entre 8 et 256 caractères.');
  const material = await crypto.subtle.importKey('raw', new TextEncoder().encode(password), 'PBKDF2', false, ['deriveKey']);
  return crypto.subtle.deriveKey({ name: 'PBKDF2', salt, iterations: 600000, hash: 'SHA-256' }, material,
    { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
function algorithm(header, index) {
  if (index >= 0xffffffff) throw new Error('Backup too large');
  const iv = new Uint8Array(12); iv.set(header.subarray(26, 34)); new DataView(iv.buffer).setUint32(8, index);
  return { name: 'AES-GCM', iv, additionalData: header, tagLength: 128 };
}
export async function encryptBackupStream(stream, password, baseHeader) {
  const header = new Uint8Array(34); header.set(baseHeader);
  header[8] = 2; header[9] |= 2;
  header.set(crypto.getRandomValues(new Uint8Array(16)), 10);
  header.set(crypto.getRandomValues(new Uint8Array(8)), 26);
  const key = await keyFor(password, header.subarray(10, 26));
  let index = 0;
  async function record(data, controller) {
    const ciphertext = new Uint8Array(await crypto.subtle.encrypt(algorithm(header, index++), key, data));
    const length = new Uint8Array(4); new DataView(length.buffer).setUint32(0, ciphertext.length, true);
    controller.enqueue(length); controller.enqueue(ciphertext);
  }
  return { header, stream: stream.pipeThrough(new TransformStream({
    async transform(chunk, controller) {
      for (let i = 0; i < chunk.length; i += CHUNK) await record(chunk.subarray(i, i + CHUNK), controller);
    },
    async flush(controller) { await record(new Uint8Array(), controller); }
  })) };
}
export async function decryptBackupStream(file, password) {
  const header = new Uint8Array(await file.slice(0, 34).arrayBuffer());
  if (header.length !== 34 || header[8] !== 2 || header[9] !== 3) throw new Error('Invalid encrypted backup');
  const key = await keyFor(password, header.subarray(10, 26));
  const reader = file.slice(34).stream().getReader();
  let pending = new Uint8Array(), offset = 0, index = 0;
  async function read(n) {
    const bytes = new Uint8Array(n);
    let count = 0;
    while (count < n) {
      if (offset === pending.length) {
        const next = await reader.read();
        if (next.done) throw new Error('Truncated encrypted backup');
        pending = next.value; offset = 0;
      }
      const size = Math.min(n - count, pending.length - offset);
      bytes.set(pending.subarray(offset, offset + size), count); count += size; offset += size;
    }
    return bytes;
  }
  return new ReadableStream({
    async pull(controller) {
      try {
        const lengthBytes = await read(4), length = new DataView(lengthBytes.buffer).getUint32(0, true);
        if (length < 16 || length > CHUNK + 16) throw new Error('Invalid encrypted record');
        const data = new Uint8Array(await crypto.subtle.decrypt(algorithm(header, index++), key, await read(length)));
        if (!data.length) {
          if (offset < pending.length || !(await reader.read()).done) throw new Error('Trailing encrypted data');
          controller.close();
        } else controller.enqueue(data);
      } catch (error) { await reader.cancel().catch(() => {}); controller.error(error); }
    },
    cancel(reason) { return reader.cancel(reason); }
  });
}
