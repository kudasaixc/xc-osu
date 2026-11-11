// Isolated API + real browser recovery test; uses a capture transport, never real email.
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';
const root = resolve(import.meta.dirname, '..');
const directory = await mkdtemp(join(tmpdir(), 'xc-recovery-browser-'));
const api = 'http://127.0.0.1:5083', origin = 'http://recovery.test';
const capture = join(directory, 'mail.eml'), transport = join(directory, 'sendmail');
await writeFile(transport, `#!/bin/sh\ncat > '${capture}'\n`, { mode: 0o700 });
let output = '';
const server = spawn(process.env.DOTNET || join(process.env.HOME, '.dotnet/dotnet'), [join(root, 'server/XcOsu.Server/bin/Release/net10.0/xc-osu-server.dll')], {
  env: { ...process.env, DataDirectory: directory, Urls: api, PublicUrl: 'https://osu.test', Mail__SendmailPath: transport },
});
server.stdout.on('data', x => { output += x; }); server.stderr.on('data', x => { output += x; });
let browser;
const post = async (path, data) => fetch(api + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
try {
  for (let i = 0; ; i++) {
    if (await fetch(api + '/api/xc/health').then(r => r.ok).catch(() => false)) break;
    if (i > 100 || server.exitCode != null) throw new Error('Isolated server failed: ' + output.slice(-3000));
    await new Promise(r => setTimeout(r, 200));
  }
  const register = await post('/users', { 'user[username]': 'RecoveryTest', 'user[user_email]': 'recovery@example.test', 'user[password]': 'before-reset' });
  assert.equal(register.status, 200);
  const oldLogin = await post('/oauth/token', { grant_type: 'password', username: 'RecoveryTest', password: 'before-reset' });
  const oldToken = (await oldLogin.json()).access_token;
  assert(oldToken);
  browser = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args: ['--no-sandbox'] });
  const page = await browser.newPage({ locale: 'fr-FR' });
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.route(origin + '/**', async route => {
    const req = route.request(), url = new URL(req.url());
    if (/^\/(api|oauth)\//.test(url.pathname)) return route.fulfill({ response: await route.fetch({ url: api + url.pathname + url.search }) });
    if (url.pathname.startsWith('/web/a/')) {
      const file = url.pathname.slice('/web/a/'.length);
      const type = { js: 'text/javascript', css: 'text/css', json: 'application/json' }[file.split('.').pop()] || 'application/octet-stream';
      try { return route.fulfill({ contentType: type, body: await readFile(join(root, 'web', file)) }); }
      catch { return route.fulfill({ status: 404, body: '' }); }
    }
    return route.fulfill({ contentType: 'text/html', body: (await readFile(join(root, 'web/index.html'), 'utf8')).replaceAll('__A__', 'a') });
  });
  await page.goto(origin + '/web/login');
  await page.locator('a[href="/web/home/password-reset"]').click();
  await page.locator('#password-recovery [name=email]').fill('recovery@example.test');
  await page.locator('#password-recovery button').click();
  await page.waitForFunction(() => document.querySelector('#password-recovery .msg')?.textContent.includes('indésirables'));
  const mime = await readFile(capture, 'utf8');
  const mailBody = Buffer.from(mime.split(/\r?\n\r?\n/).slice(1).join('\n'), 'base64').toString();
  const token = /#token=([A-F0-9]{64})/.exec(mailBody)?.[1]; assert(token);
  const known = await post('/api/xc/password-reset/request', { email: 'recovery@example.test' });
  const unknown = await post('/api/xc/password-reset/request', { email: 'unknown@example.test' });
  assert.deepEqual(await known.json(), await unknown.json(), 'No account enumeration');
  await page.goto(origin + '/web/home/password-reset#token=' + token);
  await page.locator('[name=confirmation]').waitFor();
  assert(!page.url().includes(token), 'Token removed from address bar');
  await page.locator('[name=password]').fill('after-reset-password');
  await page.locator('[name=confirmation]').fill('mismatched-password');
  await page.locator('#password-recovery button').click();
  await page.waitForFunction(() => document.querySelector('.msg')?.textContent.includes('correspondent pas'));
  await page.locator('[name=confirmation]').fill('after-reset-password');
  await page.locator('#password-recovery button').click();
  await page.waitForFunction(() => document.querySelector('.msg')?.textContent.includes('modifié'));
  assert.equal((await fetch(api + '/api/v2/me', { headers: { Authorization: 'Bearer ' + oldToken } })).status, 401);
  assert.equal((await post('/oauth/token', { grant_type: 'password', username: 'RecoveryTest', password: 'before-reset' })).status, 400);
  assert((await (await post('/oauth/token', { grant_type: 'password', username: 'RecoveryTest', password: 'after-reset-password' })).json()).access_token);
  assert.equal((await post('/api/xc/password-reset/complete', { token, password: 'reuse-password' })).status, 422);
  assert.equal((await post('/api/xc/password-reset/request', { email: 'unknown@example.test' })).status, 429, 'Rate limit enforced');
  assert.deepEqual(errors, []);
  console.log('PASS: real browser forgotten-password link, request form, captured email, mismatch validation, reset, token cleanup, revoked sessions, no enumeration, single-use token and rate limit');
} finally {
  await browser?.close();
  server.kill('SIGTERM');
  await new Promise(r => { if (server.exitCode != null) r(); else server.once('exit', r); });
  await rm(directory, { recursive: true, force: true });
}
