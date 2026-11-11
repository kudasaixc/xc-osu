// xc-osu! web (/web/) end-to-end test. xc softworks (kudasaixc)
// usage: node tools/web-test.mjs            writes on an ISOLATED server (XC_WEB_API, default http://127.0.0.1:5081)
//        XC_WEB_READONLY=http://127.0.0.1:5080 node tools/web-test.mjs   also renders real data, GET requests only
import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const root = new URL('../web/', import.meta.url);
const API = process.env.XC_WEB_API || 'http://127.0.0.1:5081';
const READONLY = process.env.XC_WEB_READONLY || '';
const SHOTS = process.env.XC_SHOT_DIR || '/tmp';
const ORIGIN = 'http://web.test';
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
      try { return await route.fulfill({ response: await route.fetch({ url: api + path + url.search }) }); }
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
    await page.click('#register button');
    await page.waitForURL(/\/web\/home\/account\/edit/);
    await page.waitForSelector('#profile');
    assert.match(await text(page, '.me-menu summary'), new RegExp(alice)); step('registration signs in and opens the settings');

    await page.fill('#profile [name=location]', 'Lyon <b>x</b>');
    await page.selectOption('#profile [name=country]', 'FR');
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

    // sign out, second player
    await page.click('.me-menu summary'); await page.click('#sign-out');
    await page.waitForSelector('#sign-in-link');
    await visit('/register');
    await page.fill('[name=username]', bob); await page.fill('[name=email]', `${bob}@example.test`); await page.fill('[name=password]', password);
    await page.click('#register button');
    await page.waitForURL(/\/web\/home\/account\/edit/);

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
    await page.evaluate(() => { const a = JSON.parse(localStorage.getItem('xc.web.auth')); a.access = 'expired'; localStorage.setItem('xc.web.auth', JSON.stringify(a)); });
    await visit('/home/friends');
    assert.match(page.url(), /\/home\/friends$/); step('an expired access token is renewed without asking to sign in again');
    await page.evaluate(() => localStorage.setItem('xc.web.auth', JSON.stringify({ access: 'expired', refresh: 'expired' })));
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
    await visit('/home/changelog/lazer/2026.1110.0'); assert.doesNotMatch(await text(page), /2026\.1109\.0/); step('changelog: all versions and one version');
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
