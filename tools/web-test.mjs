// xc-osu! web (/web/) end-to-end test. xc softworks (kudasaixc)
// usage: node tools/web-test.mjs            writes on an ISOLATED server (XC_WEB_API, default http://127.0.0.1:5081)
//        XC_WEB_READONLY=http://127.0.0.1:5080 node tools/web-test.mjs   also renders real data, GET requests only
//        XC_WEB_MAIL=<dir>   where the isolated server's mail transport drops messages (*.eml); needed when it requires
//                            confirmed addresses (the default), so the test can follow the confirmation link like a player would
import { chromium } from 'playwright';
import { readFile, readdir } from 'node:fs/promises';
import assert from 'node:assert/strict';

const root = new URL('../web/', import.meta.url);
const API = process.env.XC_WEB_API || 'http://127.0.0.1:5081';
const READONLY = process.env.XC_WEB_READONLY || '';
const SHOTS = process.env.XC_SHOT_DIR || '/tmp';
// https, like production: the session lives in Secure __Host- cookies and the server checks the request's origin.
const ORIGIN = 'https://web.test';
const TYPES = { js: 'text/javascript', css: 'text/css', json: 'application/json', html: 'text/html' };
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

const browser = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args: ['--no-sandbox'] });

async function open(api, { readonly = false, viewport = { width: 1280, height: 900 }, locale = 'fr-FR' } = {}) {
  const context = await browser.newContext({ viewport, locale });
  const page = await context.newPage();
  const problems = [];
  page.on('pageerror', (e) => problems.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) problems.push('console: ' + m.text()); });
  await context.route(ORIGIN + '/**', async (route) => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname;
    if (/^\/(api|oauth)\//.test(path) || (path === '/users' && req.method() === 'POST')) {
      if (readonly && req.method() !== 'GET') { problems.push(`write attempted in read-only mode: ${req.method()} ${path}`); return route.fulfill({ status: 403, body: '{}' }); }
      // what the production reverse proxy sends, so the server sees the page's own origin
      const headers = { ...await req.allHeaders(), 'x-forwarded-host': 'web.test', 'x-forwarded-proto': 'https' };
      try { return await route.fulfill({ response: await route.fetch({ url: api + path + url.search, headers }) }); }
      catch { return route.abort(); } // the page navigated away while the request was in flight
    }
    if (path.startsWith('/web/a/')) {
      const file = path.slice('/web/a/'.length);
      try { return await route.fulfill({ contentType: TYPES[file.split('.').pop()] || 'application/octet-stream', body: await readFile(new URL(file, root)) }); }
      catch { return route.fulfill({ status: 404, body: 'missing ' + file }); }
    }
    if (path === '/web' || path.startsWith('/web/')) return route.fulfill({ contentType: 'text/html', body: (await readFile(new URL('index.html', root), 'utf8')).replaceAll('__A__', 'a') });
    return route.fulfill({ status: 404, body: '' });
  });
  const visit = async (path) => { await page.goto(ORIGIN + '/web' + path); await page.waitForFunction(() => document.querySelector('#view')?.childElementCount && !document.querySelector('#view > .loading')); };
  return { page, context, problems, visit };
}
const MAIL = process.env.XC_WEB_MAIL;
// Follows the link of the confirmation mail when the server gates new accounts on it; returns whether it had to.
async function confirmEmail(page, visit, address) {
  if (!await page.evaluate(async () => (await (await fetch('/api/xc/account')).json()).email_confirmation_required)) return false;
  assert.match(await text(page, '#email-confirm'), /Confirme ton adresse/, 'the settings say what is waiting for confirmation');
  assert.ok(MAIL, 'this server requires confirmed addresses: set XC_WEB_MAIL to the directory its mail transport writes to');
  let link = null;
  for (let i = 0; i < 100 && !link; i++) {
    for (const f of (await readdir(MAIL)).filter((f) => f.endsWith('.eml'))) {
      const mail = await readFile(MAIL + '/' + f, 'utf8');
      if (!mail.includes(address)) continue;
      const [head, body] = mail.split('\r\n\r\n', 2);
      link = (/base64/i.test(head) ? Buffer.from(body, 'base64').toString() : body).match(/\/api\/xc\/account\/email\/verify\?token=[A-F0-9]{64}/)?.[0];
    }
    if (!link) await page.waitForTimeout(100);
  }
  assert.ok(link, 'a confirmation mail was sent to ' + address);
  await page.goto(ORIGIN + link);
  await Promise.all([page.waitForResponse((r) => r.request().method() === 'POST' && r.status() === 200), page.click('button')]);
  await visit('/home/account/edit');
  assert.equal(await page.locator('#email-confirm').count(), 0, 'the reminder goes away once confirmed');
  return true;
}
const text = (page, sel = '#view') => page.locator(sel).first().innerText();
const done = [];
const step = (name) => { done.push(name); };

try {
  // ------------------------------------------------------------------ isolated server: everything that writes
  const stamp = Date.now().toString(36).slice(-6);
  const alice = 'wa' + stamp, bob = 'wb' + stamp, password = 'correct horse 42';
  {
    const { page, context, problems, visit } = await open(API);

    await visit('/');
    assert.match(await text(page), /Jouer maintenant/); step('home renders in French for a French browser');

    await visit('/home/account/edit');
    assert.match(page.url(), /\/web\/login\?next=%2Fhome%2Faccount%2Fedit/); step('settings while signed out leads to sign-in with a way back');

    await visit('/login');
    await page.fill('#login [name=username]', 'nobody-' + stamp); await page.fill('#login [name=password]', 'wrong password');
    await page.click('#login button');
    await page.waitForFunction(() => document.querySelector('#login .msg.bad')?.textContent);
    step('wrong credentials are reported next to the form');

    await visit('/register');
    await page.fill('[name=username]', alice); await page.fill('[name=email]', 'not-an-email@x'); await page.fill('[name=password]', 'short');
    await page.evaluate(() => document.querySelector('#register').noValidate = true); // exercise the server-side messages too
    await page.click('#register button');
    await page.waitForFunction(() => document.querySelector('#register .msg.bad')?.textContent);
    await page.fill('[name=email]', `${alice}@example.test`); await page.fill('[name=password]', password);
    await page.setExtraHTTPHeaders({ 'CF-IPCountry': 'FR' }); // the country comes from the connection, as behind Cloudflare
    await page.click('#register button');
    await page.waitForURL(/\/web\/home\/account\/edit/);
    await page.waitForSelector('#profile');
    assert.match(await text(page, '.me-menu summary'), new RegExp(alice)); step('registration signs in and opens the settings');

    if (await confirmEmail(page, visit, `${alice}@example.test`)) step('a new account confirms its address from the mail it received');
    await page.fill('#profile [name=location]', 'Lyon <b>x</b>');
    await page.setExtraHTTPHeaders({});
    assert.equal(await page.locator('#profile select[name=country], #profile input[name=country]').count(), 0, 'no country field to submit');
    assert.match(await page.locator('#profile input[disabled]').first().inputValue(), /France/);
    assert.equal(await page.evaluate(async () => {
      const h = {}; // the session is carried by HttpOnly cookies, which the page cannot read
      await fetch('/api/xc/account', { method: 'PUT', headers: { ...h, 'Content-Type': 'application/json' }, body: JSON.stringify({ country: 'US' }) });
      return (await (await fetch('/api/xc/account', { headers: h })).json()).country;
    }), 'FR', 'the API ignores a country change');
    step('country is set at registration and cannot be changed');
    await page.check('#profile [name=playstyle][value=tablet]');
    await page.fill('#profile [name=page]', '# Salut\n**gras** et <img src=x onerror="window.pwned=1">\n- un\n- deux\n[lien](javascript:alert(1)) [ok](https://example.org/a)');
    await page.click('#profile [type=submit]');
    await page.waitForFunction(() => document.querySelector('#profile .msg.good'));
    step('profile saves');

    await page.setInputFiles('#avatar-file', { name: 'a.png', mimeType: 'image/png', buffer: PNG });
    await page.waitForFunction(() => document.querySelector('#img-msg.good'));
    await page.setInputFiles('#avatar-file', { name: 'a.txt', mimeType: 'text/plain', buffer: Buffer.from('not an image') });
    await page.waitForFunction(() => document.querySelector('#img-msg.bad')?.textContent);
    step('avatar upload works and a non-image is refused with a message');

    await page.fill('#password [name=current]', 'not my password'); await page.fill('#password [name=next]', 'another pass 43');
    await page.click('#password button');
    await page.waitForFunction(() => document.querySelector('#password .msg.bad')?.textContent);
    step('wrong current password is refused');

    await page.waitForSelector('#team-create');
    await page.fill('#team-create [name=name]', 'Team ' + stamp); await page.fill('#team-create [name=short_name]', 'T' + stamp.slice(-3));
    await page.click('#team-create button');
    await page.waitForSelector('#team-edit');
    await visit('/teams');
    assert.match(await text(page), new RegExp('Team ' + stamp)); step('team creation shows up in the team list');

    await visit('/users/' + alice);
    const profile = await text(page);
    assert.match(profile, /Lyon <b>x<\/b>/, 'location is shown as text');
    assert.match(profile, /France/); assert.match(profile, /tablette/);
    assert.equal(await page.locator('.user-text h2').innerText(), 'Salut');
    assert.equal(await page.locator('.user-text b').first().innerText(), 'gras');
    assert.equal(await page.locator('.user-text li').count(), 2);
    assert.equal(await page.locator('.user-text img').count(), 0, 'raw HTML in the page is not interpreted');
    assert.equal(await page.locator('.user-text a[href^="javascript"]').count(), 0);
    assert.equal(await page.locator('.user-text a[href="https://example.org/a"]').count(), 1);
    assert.equal(await page.evaluate(() => window.pwned), undefined);
    assert.match(page.url(), new RegExp('/web/users/' + alice + '$'));
    assert.match(await text(page, '.profile-actions'), /Modifier mon profil/); step('own profile shows saved fields, formatted page, no injected HTML');

    await page.evaluate(async () => {
      const h = { 'Content-Type': 'application/json' };
      await fetch('/api/xc/account', { method: 'PUT', headers: h, body: JSON.stringify({ page: '[notice]\n[centre][b][size=150]GOALS[/size][/b][/centre]\n[box=Goal 1]\n[s]5 digits[/s] [url=javascript:alert(1)]bad[/url] [url=https://osu.ppy.sh/users/2]peppy[/url]\n[/box]\n[/notice]' }) });
    });
    await visit('/users/' + alice);
    assert.equal(await page.locator('.user-text .bb-notice .bb-centre strong span[style="font-size:150%"]').innerText(), 'GOALS');
    assert.equal(await page.locator('.user-text details.bb-box summary').innerText(), 'Goal 1');
    await page.click('.user-text details.bb-box summary');
    assert.equal(await page.locator('.user-text .bb-box del').innerText(), '5 digits');
    assert.equal(await page.locator('.user-text a[href^="javascript"]').count(), 0);
    assert.equal(await page.locator('.user-text a[href="https://osu.ppy.sh/users/2"]').innerText(), 'peppy');
    step('osu! BBCode on the me! page (notice, centre, size, box, links)');

    // sign out, second player
    await page.click('.me-menu summary'); await page.click('#sign-out');
    await page.waitForSelector('#sign-in-link');
    await visit('/register');
    await page.fill('[name=username]', bob); await page.fill('[name=email]', `${bob}@example.test`); await page.fill('[name=password]', password);
    await page.click('#register button');
    await page.waitForURL(/\/web\/home\/account\/edit/);
    await page.waitForSelector('#profile');
    await confirmEmail(page, visit, `${bob}@example.test`);

    await visit('/users/' + alice);
    await page.click('#friend');
    await page.waitForFunction(() => document.querySelector('#friend')?.getAttribute('aria-pressed') === 'true');
    await visit('/home/friends');
    assert.match(await text(page), new RegExp(alice)); step('adding a friend from a profile fills the friends page');
    await page.click('[data-remove]');
    await page.waitForFunction(() => !document.querySelector('[data-remove]'));

    await visit('/teams');
    await page.click(`a.score-map:has-text("Team ${stamp}")`);
    await page.waitForSelector('#join');
    await page.click('#join');
    await page.waitForSelector('#leave');
    assert.match(await text(page), new RegExp(bob)); step('joining a team from its page');

    // beatmap page + comments (the isolated server reaches the beatmap mirrors)
    await visit('/b/3853713');
    await page.waitForURL(/\/web\/beatmapsets\/1872124#osu\/3853713/);
    await page.waitForSelector('.diff[aria-selected=true]');
    assert.match(await text(page, '#diff-detail'), /Extra/); step('/b/{id} lands on the right difficulty of the set');
    await page.waitForSelector('#comments form');
    await page.fill('#comments textarea', 'Premier <i>commentaire</i>');
    await page.click('#comments form button');
    const own = page.locator('.comment', { hasText: bob }).first();
    await own.waitFor();
    assert.match(await own.locator('.comment-text').innerText(), /Premier <i>commentaire<\/i>/);
    assert.equal(await own.locator('[data-vote]').count(), 0, 'no vote button on your own comment');
    await page.click('#fav');
    await page.waitForFunction(() => document.querySelector('#fav')?.getAttribute('aria-pressed') === 'true');
    step('comment and favourite on a beatmap');
    page.once('dialog', (d) => d.accept());
    await own.locator('[data-delete]').click();
    await page.waitForFunction(() => !document.querySelector('.comment [data-delete]'));

    // session survives a reload; an expired token falls back to the refresh token
    await page.reload(); await page.waitForSelector('.me-menu');
    assert.deepEqual(await page.evaluate(() => JSON.parse(localStorage.getItem('xc.web.auth'))), { session: true }, 'no token is kept where scripts can read it');
    const cookie = (name, value) => ({ name, value, url: ORIGIN + '/', httpOnly: true, secure: true, sameSite: 'Strict' });
    await context.addCookies([cookie('__Host-xc-access', 'expired')]);
    await visit('/home/friends');
    assert.match(page.url(), /\/home\/friends$/); step('an expired access token is renewed without asking to sign in again');
    await context.addCookies([cookie('__Host-xc-access', 'expired'), cookie('__Host-xc-refresh', 'expired')]);
    await visit('/home/friends');
    assert.match(page.url(), /\/web\/login\?next=%2Fhome%2Ffriends/); step('a dead session leads to sign-in');
    await page.fill('#login [name=username]', bob); await page.fill('#login [name=password]', password); await page.click('#login button');
    await page.waitForURL(/\/web\/home\/friends$/); step('signing in returns to the page that was asked for');

    await visit('/community/tournaments/1'); assert.match(await text(page), /ne font pas partie/);
    await visit('/wiki'); assert.match(await text(page), /Premiers pas/);
    await page.click('.wiki-index a:has-text("Son")');
    await page.waitForSelector('.prose h1');
    assert.match(page.url(), /\/web\/wiki\/Sound$/); assert.equal(await page.locator('.prose h1').innerText(), 'Son');
    await page.click('.prose a:has-text("Performances")');
    await page.waitForFunction(() => document.querySelector('.prose h1')?.textContent === 'Performances');
    assert.equal(await page.locator('.prose table').count(), 1); step('wiki: index, page, links between pages stay in the site');
    await visit('/wiki/en/Does_not_exist'); assert.match(await text(page), /n’existe pas/);
    await visit('/community/forums/topics/create?forum_id=5');
    await page.waitForURL(/\/web\/wiki\/Reporting_a_problem$/); await page.waitForSelector('.prose h1'); step('the game\'s "report an issue" link lands on the reporting page');
    await visit('/home/changelog'); assert.match(await text(page), /2026\.1109\.0/); assert.match(await text(page), /Première version publique/);
    assert.equal(await page.locator('.stream-badge').count(), 2); assert.match(await text(page), /Première version du serveur/);
    await visit('/home/changelog/lazer/2026.1110.0'); assert.doesNotMatch(await text(page), /2026\.1109\.0/); assert.doesNotMatch(await text(page), /Matchmaking/);
    await visit('/home/changelog/nanbeige'); assert.match(await text(page), /Première version du serveur/); assert.doesNotMatch(await text(page), /Première version publique/);
    await visit('/home/changelog/nanbeige/2026.1110.0'); assert.match(await text(page), /Matchmaking/); assert.doesNotMatch(await text(page), /2026\.1109\.0/); step('changelog: all versions, one stream and one version');
    await visit('/home/news'); await page.click('.post-card:has-text("Bienvenue")');
    await page.waitForSelector('.prose'); assert.match(page.url(), /\/web\/news\/2026-11-09-welcome-to-xc-osu$/);
    await page.click('.post-near'); await page.waitForFunction(() => /onglet/.test(document.querySelector('.article h1')?.textContent || '')); step('news: list, post, next post');
    await visit('/users/nobody-' + stamp); assert.match(await text(page), /Rien ici/);
    await visit('/this/does/not/exist'); assert.match(await text(page), /Rien ici/); step('missing player, unknown page and unsupported sections explain themselves');

    await page.click('#lang');
    await page.waitForFunction(() => /Nothing here/.test(document.querySelector('#view').innerText)); step('language switch');
    assert.deepEqual(problems, [], 'no script errors');
    await context.close();
  }

  // ------------------------------------------------------------------ real data, read-only
  if (READONLY) {
    for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
      const { page, context, problems, visit } = await open(READONLY, { readonly: true, viewport });
      const ranking = await (await fetch(READONLY + '/api/v2/rankings/osu/performance')).json();
      const top = ranking.ranking[0]?.User?.id ?? ranking.ranking[0]?.user?.id;
      const best = top ? await (await fetch(`${READONLY}/api/v2/users/${top}/scores/best?limit=1`)).json() : [];
      const paths = ['/', '/rankings', '/rankings/osu/country', '/beatmapsets', '/teams', '/news', '/home/search?query=a'];
      if (top) paths.push('/users/' + top);
      if (best[0]) paths.push('/scores/' + best[0].id, `/beatmapsets/${best[0].beatmap.beatmapset_id}#osu/${best[0].beatmap_id}`);
      for (const path of paths) {
        await visit(path);
        await page.waitForLoadState('networkidle').catch(() => { });
        assert.equal(await page.locator('#view .notice').count(), 0, `${path} renders (${name})`);
        const size = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
        assert(size.scroll <= size.client + 1, `${path} fits the ${name} viewport (${size.scroll} > ${size.client})`);
        await page.screenshot({ path: `${SHOTS}/xc-web-${name}-${path.replace(/[^a-z0-9]+/gi, '_').replace(/^_|_$/g, '') || 'home'}.png`, fullPage: true });
      }
      if (top) { await visit('/users/' + top); await page.waitForSelector('#sec-best .score'); }
      if (best[0]) { await visit(`/beatmapsets/${best[0].beatmap.beatmapset_id}#osu/${best[0].beatmap_id}`); await page.waitForSelector('#board .score'); }
      assert.deepEqual(problems, [], `no script errors (${name})`);
      await context.close();
      step(`real data renders on ${name}: ${paths.length} pages, no overflow`);
    }
  }
  console.log('PASS\n - ' + done.join('\n - '));
} catch (e) {
  console.log('passed so far:\n - ' + done.join('\n - '));
  throw e;
} finally { await browser.close(); }
