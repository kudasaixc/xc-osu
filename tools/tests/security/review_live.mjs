// Live checks for the post-merge review, against a throwaway server: email confirmation gate, session-bound sockets,
// rotation continuity, team paging, unavailable second factor. Mail goes to a capture script, never to a real mailbox.
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, chmodSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import http from 'node:http';
import crypto from 'node:crypto';

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const dir = mkdtempSync(join(tmpdir(), 'xc-review-'));
const port = 5190, api = `http://127.0.0.1:${port}`;
const transport = join(dir, 'sendmail');
writeFileSync(transport, `#!/bin/sh\ncat > "${dir}/mail-$$.eml"\n`);
chmodSync(transport, 0o700);
const server = spawn(process.env.DOTNET || join(process.env.HOME, '.dotnet/dotnet'), [join(root, 'server/XcOsu.Server/bin/Release/net10.0/xc-osu-server.dll')], {
  env: { ...process.env, DataDirectory: dir, Urls: api, PublicUrl: 'https://osu.test', Mail__SendmailPath: transport }, stdio: ['ignore', 'pipe', 'pipe'],
});
let log = '';
server.stdout.on('data', d => log += d); server.stderr.on('data', d => log += d);

let checks = 0;
const check = (ok, name) => { if (!ok) throw new Error('FAIL ' + name); checks++; console.log('PASS ' + name); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function req(path, { body, token, method } = {}) {
  const r = await fetch(api + path, { method: method || (body ? 'POST' : 'GET'), body: body ? JSON.stringify(body) : undefined,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) } });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch { }
  return { status: r.status, json, text, headers: r.headers };
}
const mails = () => readdirSync(dir).filter(f => f.endsWith('.eml')).map(f => readFileSync(join(dir, f), 'utf8'));
const mailBody = m => { const [head, body] = m.split('\r\n\r\n', 2); return /base64/i.test(head) ? Buffer.from(body, 'base64').toString() : body; };

// raw upgraded socket with a bearer header (browsers use the cookie; the game client sends the header).
function socket(path, token) {
  return new Promise((resolve, reject) => {
    const r = http.request(api + path, { headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13',
      'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'), Authorization: 'Bearer ' + token } });
    r.on('upgrade', (_res, s) => {
      const state = { open: true, socket: s };
      s.on('close', () => state.open = false); s.on('end', () => state.open = false); s.on('error', () => state.open = false);
      s.on('data', d => { if ((d[0] & 0x0f) === 8) state.open = false; });
      state.send = text => {
        const payload = Buffer.from(text), mask = crypto.randomBytes(4);
        const head = payload.length < 126 ? Buffer.from([0x81, 0x80 | payload.length]) : Buffer.from([0x81, 0x80 | 126, payload.length >> 8, payload.length & 255]);
        s.write(Buffer.concat([head, mask, payload.map((b, i) => b ^ mask[i % 4])]));
      };
      resolve(state);
    });
    r.on('response', res => resolve({ open: false, refused: res.statusCode }));
    r.on('error', reject);
    r.end();
  });
}
async function hub(name, token) {
  const n = await req(`/signalr/${name}/negotiate?negotiateVersion=1`, { method: 'POST', token });
  if (n.status !== 200) return { open: false, refused: n.status };
  const s = await socket(`/signalr/${name}?id=${encodeURIComponent(n.json.connectionToken)}`, token);
  if (s.open) s.send('{"protocol":"json","version":1}\x1e');
  return s;
}
const until = async (f, ms = 15000) => { for (const end = Date.now() + ms; Date.now() < end; await sleep(100)) if (f()) return true; return f(); };

try {
  for (let i = 0; i < 100; i++) { try { if ((await fetch(api + '/api/xc/health')).status === 200) break; } catch { await sleep(200); } }
  const register = (name, mail) => req('/users', { body: { 'user[username]': name, 'user[user_email]': mail, 'user[password]': 'a-good-password-123' } });
  const login = async (name, password = 'a-good-password-123') => (await req('/oauth/token', { body: { grant_type: 'password', username: name, password } })).json;

  // F — a new account plays straight away, but reaches other players only once its address is confirmed.
  check((await register('ReviewPlayer', 'review@example.test')).status === 200, 'registration');
  let t = await login('ReviewPlayer');
  check(!!t?.access_token, 'unconfirmed account signs in (native client compatible)');
  let account = (await req('/api/xc/account', { token: t.access_token })).json;
  check(account.email_verified === false && account.email_confirmation_required === true, 'account reports the pending confirmation');
  check((await req('/api/v2/me', { token: t.access_token })).status === 200, 'unconfirmed account can use the API');
  const refused = await req('/api/xc/teams', { body: { name: 'Review Team', short_name: 'RVW' }, token: t.access_token });
  check(refused.status === 403 && /confirm your email/i.test(refused.text), 'team creation waits for confirmation');
  check((await req('/api/v2/rooms', { body: { name: 'room', playlist: [] }, token: t.access_token })).status === 403, 'hosting a room waits for confirmation');
  check(await until(() => mails().length >= 1), 'confirmation mail handed to the transport');
  const link = mailBody(mails()[0]).match(/https:\/\/osu\.test(\/api\/xc\/account\/email\/verify\?token=([A-F0-9]{64}))/);
  check(!!link, 'mail carries a link on the public origin');
  check((await fetch(api + link[1])).status === 200, 'confirmation page opens');
  check((await req('/api/xc/account', { token: t.access_token })).json.email_verified === false, 'opening the link alone confirms nothing (mail scanners)');
  check((await req('/api/xc/account/email/verify', { body: { token: link[2] } })).status === 200, 'confirmation accepted');
  check((await req('/api/xc/account/email/verify', { body: { token: link[2] } })).status === 400, 'confirmation link is single use');
  account = (await req('/api/xc/account', { token: t.access_token })).json;
  check(account.email_verified === true && account.email_confirmation_required === false, 'account confirmed');
  const team = await req('/api/xc/teams', { body: { name: 'Review Team', short_name: 'RVW' }, token: t.access_token });
  check(team.status === 200, 'confirmed account creates a team');

  // H — every team stays reachable past the first page.
  const rows = Array.from({ length: 119 }, (_, i) => `('Filler ${i}', 'F${i}', 900000, '2026-01-01T00:00:00Z')`).join(',');
  execFileSync('sqlite3', [join(dir, 'xc-osu.db'), `INSERT INTO teams (name, short_name, leader_id, created_at) VALUES ${rows}, ('Needle Squad', 'NDL', 900000, '2026-01-01T00:00:00Z');`]);
  const p1 = await req('/api/xc/teams'), p3 = await req('/api/xc/teams?page=3'), found = await req('/api/xc/teams?q=needle');
  check(p1.json.length === 50 && p1.headers.get('x-total-count') === '121' && p1.headers.get('x-has-more') === '1', 'first page of teams announces more');
  check(p3.json.length === 21 && p3.headers.get('x-has-more') === '0', 'last page of teams reachable');
  check(found.json.length === 1 && found.json[0].short_name === 'NDL' && found.headers.get('x-total-count') === '1', 'team search finds a team beyond the first page');
  check((await req('/api/xc/teams?q=' + encodeURIComponent('%'))).json.length === 0, 'search wildcard is literal');

  // B — no pretend second factor.
  for (const path of ['/api/v2/session/verify', '/api/v2/session/verify/reissue', '/api/v2/session/verify/mail-fallback'])
    check((await req(path, { body: {}, token: t.access_token })).status === 501, path + ' answers 501');

  // C — the same bounds at the door and in the service.
  check((await req('/users', { body: { 'user[username]': 'TooLong', 'user[user_email]': 'long@example.test', 'user[password]': 'p'.repeat(257) } })).status !== 200, '257-character password refused at registration');
  check((await req('/api/xc/account/password', { body: { current_password: 'a-good-password-123', new_password: 'p'.repeat(257) }, token: t.access_token })).status !== 204, '257-character password refused at change');

  // A — sockets live and die with their session.
  const other = await login('ReviewPlayer');
  const notif = await socket('/notifications', t.access_token);
  const meta = await hub('metadata', t.access_token), spectator = await hub('spectator', t.access_token), multiplayer = await hub('multiplayer', t.access_token);
  const otherNotif = await socket('/notifications', other.access_token);
  check(notif.open && meta.open && spectator.open && multiplayer.open && otherNotif.open, 'notification socket and the three hubs connect');
  const rotated = (await req('/oauth/token', { body: { grant_type: 'refresh_token', refresh_token: t.refresh_token } })).json;
  check(!!rotated?.access_token && rotated.access_token !== t.access_token, 'token rotated mid-session');
  await sleep(12000); // longer than the periodic sweep
  check(notif.open && meta.open && spectator.open && multiplayer.open, 'connections opened before the rotation survive it');
  check((await req('/api/v2/me', { token: t.access_token })).status === 401 && (await req('/api/v2/me', { token: rotated.access_token })).status === 200, 'old access token dead, new one live');
  check((await req('/api/v2/oauth/tokens/current', { method: 'DELETE', token: rotated.access_token })).status === 204, 'sign-out');
  check(await until(() => !notif.open && !meta.open && !spectator.open && !multiplayer.open, 5000), 'sign-out closes that session\'s sockets at once');
  check(otherNotif.open, 'the other device stays connected');
  const late = await socket('/notifications', rotated.access_token);
  check(!late.open && late.refused === 401, 'revoked token cannot open a socket');
  const otherHub = await hub('multiplayer', other.access_token);
  check((await req('/api/xc/account/password', { body: { current_password: 'a-good-password-123', new_password: 'another-password-456' }, token: other.access_token })).status === 204, 'password change');
  check(await until(() => !otherNotif.open && !otherHub.open, 5000), 'password change closes every socket of the account');

  // D — refreshing from a shared address is not throttled like password guessing.
  let last = await login('ReviewPlayer', 'another-password-456'), refreshed = 0;
  for (let i = 0; i < 60; i++) {
    const r = await req('/oauth/token', { body: { grant_type: 'refresh_token', refresh_token: last.refresh_token } });
    if (r.status !== 200) break;
    last = r.json; refreshed++;
  }
  check(refreshed === 60, '60 refreshes in a row from one address pass');
  let codes = [];
  for (let i = 0; i < 25; i++) codes.push((await req('/oauth/token', { body: { grant_type: 'password', username: 'ReviewPlayer', password: 'wrong-password-' + i } })).status);
  check(codes.includes(429) && codes[0] !== 429, 'password guessing from that address is cut off');
  check((await req('/oauth/token', { body: { grant_type: 'refresh_token', refresh_token: last.refresh_token } })).status === 200, 'refresh still works while password attempts are blocked');
  console.log(`${checks} checks passed.`);
} catch (e) {
  console.error(e.message || e);
  console.error(log.split('\n').filter(l => /warn|fail|rror|Exception/.test(l)).slice(-15).join('\n'));
  process.exitCode = 1;
} finally {
  server.kill('SIGTERM');
  await sleep(500);
  rmSync(dir, { recursive: true, force: true });
}
