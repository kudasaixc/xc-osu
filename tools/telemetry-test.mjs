import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const source = await readFile(new URL('../osu/osu.Web/wwwroot/js/xc-telemetry.js', import.meta.url), 'utf8');
const browser = await chromium.launch({executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args:['--no-sandbox']});
try {
  const page = await browser.newPage();
  let bodies = [], fail = true;
  await page.route('https://telemetry.test/**', async route => {
    if (route.request().url().endsWith('/api/xc/tm')) {
      const body = JSON.parse(route.request().postData()); bodies.push(body);
      return route.fulfill({status: fail ? 503 : 200, contentType:'application/json', body: JSON.stringify({seq: Math.max(0,...body.items.map(i=>i.q))})});
    }
    if (route.request().url().endsWith('/tm.js')) return route.fulfill({contentType:'text/javascript',body:source});
    return route.fulfill({contentType:'text/html',body:'<!doctype html><title>telemetry test</title>'});
  });
  await page.goto('https://telemetry.test/');
  const start = enabled => page.evaluate(async enabled => {
    const {startTelemetry} = await import('/tm.js');
    window.tmLines = [];
    startTelemetry({telemetry:enabled}, 'test', {on(fn){ window.tmLines.push(fn); }});
  }, enabled);
  await start(false);
  assert.equal(bodies.length, 0, 'opt out must send nothing');
  await start(true);
  await page.waitForTimeout(200);
  await page.evaluate(() => window.tmLines[0]({text:'[tm] {"k":"w","th":{"draw":{"late":2}}}',level:'log'}));
  assert(await page.evaluate(() => Object.keys(localStorage).some(k=>k.startsWith('xc.tm.v1.'))));
  await page.evaluate(() => dispatchEvent(new Event('pagehide')));
  await page.waitForTimeout(200);
  assert(bodies.some(b=>b.items.some(i=>i.n==='lifecycle')), 'pagehide beacon');
  await page.evaluate(() => {
    globalThis.xcTelemetryEvent('worker-ui', { op: 15 });
    const at = Date.now();
    for (let part = 0; part < 8; part++) window.tmLines[0]({level:'log',text:'[tm] '+JSON.stringify({k:'e',n:'flight',at,part,parts:8,frames:Array.from({length:100},(_,i)=>[i,16.67,1.23,0,0,0,0,0,2.34]),padding:'x'.repeat(5000)})});
  });
  await page.waitForTimeout(200);
  assert(await page.evaluate(()=>Object.keys(localStorage).some(k=>localStorage.getItem(k).length>48000)), 'multi-packet capture persists');
  const previousSid = bodies[0].sid;
  fail = false;
  await page.reload(); await start(true);
  await page.waitForTimeout(6500);
  assert(bodies.some(b=>b.sid===previousSid && b.items.some(i=>i.k==='w')), 'reload retries pending frames');
  assert(bodies.every(b=>Buffer.byteLength(JSON.stringify(b))<=48000), 'keepalive packet limit');
  assert(bodies.some(b=>b.items.some(i=>i.n==='flight-context' && i.events.some(e=>e.type==='worker-ui'))), 'capture context joined');
  assert.equal(await page.evaluate(sid=>localStorage.getItem('xc.tm.v1.'+sid),previousSid),null,'acknowledged old outbox removed');
  await page.reload(); await start(false);
  assert.equal(await page.evaluate(()=>Object.keys(localStorage).filter(k=>k.startsWith('xc.tm.v1.')).length),0,'opt out clears pending data');
  console.log('PASS: opt-out, durable retry, pagehide beacon, acknowledgements, reload recovery');
} finally { await browser.close(); }
