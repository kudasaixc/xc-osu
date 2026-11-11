// Crash reporting of the page telemetry: grouped stacks, crash event, unclean exit detection, installation id.
import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const source = await readFile(new URL('../osu/osu.Web/wwwroot/js/xc-telemetry.js', import.meta.url), 'utf8');
const browser = await chromium.launch({executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args:['--no-sandbox']});
try {
  const page = await browser.newPage();
  const bodies = [];
  await page.route('https://telemetry.test/**', async route => {
    if (route.request().url().endsWith('/api/xc/tm')) {
      const body = JSON.parse(route.request().postData()); bodies.push(body);
      return route.fulfill({status: 200, contentType:'application/json', body: JSON.stringify({seq: Math.max(0,...body.items.map(i=>i.q))})});
    }
    if (route.request().url().endsWith('/tm.js')) return route.fulfill({contentType:'text/javascript',body:source});
    return route.fulfill({contentType:'text/html',body:'<!doctype html><title>telemetry test</title>'});
  });
  await page.goto('https://telemetry.test/');
  const start = enabled => page.evaluate(async enabled => {
    const NativeObserver = window.PerformanceObserver;
    window.tmObservers = {};
    window.PerformanceObserver = class extends NativeObserver {
      constructor(callback) { super(callback); this.callback = callback; }
      observe(options) { window.tmObservers[options.type] = this.callback; return super.observe(options); }
    };
    const {startTelemetry} = await import('/tm.js');
    window.tmLines = [];
    window.tmLog = {lines: [{level:'log',text:'loading storyboard for someone@example.org'}, {level:'error',text:'boom'}], on(fn){ window.tmLines.push(fn); }};
    startTelemetry({telemetry:enabled}, 'test', window.tmLog);
  }, enabled);
  const items = () => { const seen = new Map(); for (const b of bodies) for (const i of b.items) seen.set(b.sid + ':' + i.q, i); return [...seen.values()]; };
  const settle = () => page.waitForTimeout(2600);

  // a session that died without saying goodbye, and one that ended properly
  await page.evaluate(() => {
    const now = Date.now();
    localStorage.setItem('xc.tm.live.v1.dead', JSON.stringify({sid:'dead', started: now - 120000, at: now - 60000, build:'old', scr:'PlayerLoader', ov:'', hidden:0, fatal:'thread handle : 0x1'}));
    localStorage.setItem('xc.tm.live.v1.clean', JSON.stringify({sid:'clean', started: now - 120000, at: now - 60000, scr:'MainMenu', end:'pagehide'}));
    localStorage.setItem('xc.tm.live.v1.othertab', JSON.stringify({sid:'othertab', started: now - 120000, at: now - 1000, scr:'MainMenu'}));
  });
  await start(true);
  await settle();
  const unclean = items().filter(i => i.n === 'unclean-exit');
  assert.equal(unclean.length, 1, 'exactly the dead session is reported');
  assert.equal(unclean[0].name, 'PlayerLoader'); assert.equal(unclean[0].prev, 'dead'); assert.equal(unclean[0].up, 60); assert.equal(unclean[0].fatal, 'thread handle : 0x1');
  assert(await page.evaluate(() => localStorage.getItem('xc.tm.live.v1.othertab') !== null && localStorage.getItem('xc.tm.live.v1.dead') === null && localStorage.getItem('xc.tm.live.v1.clean') === null), 'markers cleaned, live tab untouched');

  const iid = bodies[0].iid;
  assert.match(iid, /^[0-9a-f-]{36}$/, 'installation id sent');

  // Restricted resource timing must not put the end before the start or report negative durations.
  await page.evaluate(() => {
    window.tmObservers.resource({getEntries: () => [{name:'https://telemetry.test/cached', initiatorType:'regression-negative', startTime:performance.now() - 10, responseEnd:0, duration:-5, transferSize:0}]});
    window.tmLines[0]({level:'log', text:'[tm] ' + JSON.stringify({k:'e',n:'flight',at:Date.now(),part:0,parts:1,frames:[]})});
  });
  await settle();
  const network = items().filter(i => i.n === 'flight-context').flatMap(i => i.events || []).filter(i => i.kind === 'regression-negative');
  assert.equal(network.length, 2);
  assert(network[1].at >= network[0].at && network[1].ms >= 0, 'non-negative, correctly ordered resource context');

  // a thread dump: many lines in one burst = one event, names readable, secrets removed
  await page.evaluate(() => {
    const emit = (text, level = 'error') => window.tmLines[0]({level, text});
    emit('[tm] {"k":"e","n":"screen","from":"SoloSongSelect","to":"PlayerLoader"}', 'log');
    emit('"ThreadedTaskScheduler (LoadComponentsAsync (standard))" tid=0x11c6b410 this=0xc017710 , thread handle : 0x11c62910, state : not waiting');
    emit('  at (wrapper runtime-invoke) object.runtime_invoke_sig_void_intptr (object,intptr,intptr,intptr) [0x00006] in <0123456789abcdef0123456789abcdef01234567>:0');
    for (let i = 0; i < 28; i++) emit('  at osu.Framework.Graphics.Drawable.Load' + i + ' () mail someone@example.org https://osu.test/api/v2/users/1234?key=secret');
  });
  await settle();
  const dumps = items().filter(i => i.n === 'log');
  assert.equal(dumps.length, 1, 'one event for the whole dump');
  const dump = dumps[0];
  assert.equal(dump.lines, 30); assert.equal(dump.category, 'crash'); assert.equal(dump.scr, 'PlayerLoader');
  assert(dump.text.includes('runtime_invoke_sig_void_intptr'), 'method names kept');
  assert(dump.text.includes('<<id>>') && !dump.text.includes('0123456789abcdef0123456789abcdef'), 'long identifiers removed');
  assert(!dump.text.includes('example.org') && !dump.text.includes('secret') && !dump.text.includes('1234'), 'e-mail, query string and numeric ids removed');
  assert.equal(dump.text.split('\n').length, 30);

  // the same harmless message over and over: three copies at most
  for (let i = 0; i < 6; i++) { await page.evaluate(() => window.tmLines[0]({level:'error', text:'unhandled rejection: NotAllowedError: A user gesture is required to request Pointer Lock.'})); await page.waitForTimeout(350); }
  await settle();
  assert.equal(items().filter(i => i.n === 'log' && i.text.startsWith('unhandled rejection')).length, 3, 'repeats capped');
  // A fatal line immediately after a suppressed repeat must break out of suppression.
  await page.evaluate(() => {
    const emit = text => window.tmLines[0]({level:'error', text});
    emit('unhandled rejection: NotAllowedError: A user gesture is required to request Pointer Lock.');
    emit('SIGABRT: regression-fatal-after-suppressed-warning');
    emit('at actualRootCauseBeforeFinalizer ()');
  });
  await settle();
  assert(items().some(i => i.n === 'log' && i.category === 'crash' && i.text.includes('regression-fatal-after-suppressed-warning') && i.text.includes('actualRootCauseBeforeFinalizer')), 'fatal burst survives log repeat suppression');

  // ... a later burst after a send starts a new event
  await page.evaluate(() => window.tmLines[0]({level:'warn', text:'Texture upload queue is full'}));
  await settle();
  assert.equal(items().filter(i => i.n === 'log' && i.category === 'graphics' && i.name === 'warn').length, 1);

  // explicit crash report
  await page.evaluate(() => globalThis.xcTelemetryCrash('exit', {code: 134, text: 'Assertion at mono/mini/aot-runtime.c:1234\ntoken abcdef\nline three'}));
  await page.waitForTimeout(500);
  const crash = items().find(i => i.n === 'crash');
  assert(crash, 'crash event sent at once');
  assert.equal(crash.name, 'exit'); assert.equal(crash.code, 134); assert.equal(crash.scr, 'PlayerLoader');
  assert(crash.text.includes('aot-runtime.c') && crash.text.includes('token <redacted>') && crash.text.split('\n').length === 3);
  assert(crash.tail.includes('error: boom') && !crash.tail.includes('example.org'), 'console tail attached and scrubbed');
  const sid = bodies.at(-1).sid;
  assert.equal(await page.evaluate(sid => JSON.parse(localStorage.getItem('xc.tm.live.v1.' + sid)).end, sid), 'crash');

  // a normal reload is a clean exit and keeps the installation id
  bodies.length = 0;
  await page.reload(); await start(true);
  await settle();
  assert.equal(items().filter(i => i.n === 'unclean-exit').length, 0, 'clean exits are not reported');
  assert.equal(bodies[0].iid, iid, 'installation id is stable');
  await page.reload(); await start(false);
  assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('xc.tm.') && !k.startsWith('xc.tm.local.')).length), 0, 'opt out clears markers and installation id');
  console.log('PASS: unclean exit, installation id, grouped dump, scrubbing, repeat cap, crash event, opt-out');
} finally { await browser.close(); }
