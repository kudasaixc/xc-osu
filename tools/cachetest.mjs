import { chromium } from 'playwright';
const b = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required'] });
const ctx = await b.newContext();
const page = await ctx.newPage();
await page.addInitScript(() => localStorage.setItem('xc.launcher.v1', JSON.stringify({ autoLaunch: true, startGate: false, rev: 2 })));
let net = [];
page.on('response', r => { if (/osu\.Game\.Resources\./.test(r.url())) net.push(r.status() + ' sw=' + r.fromServiceWorker() + ' ' + r.url().split('/').pop()); });
page.on('console', m => { const t = m.text(); if (/boot complete|keep .* offline|Aborted|terminated|Error/i.test(t)) console.log('  ' + t.slice(0, 200)); });
for (const pass of [1, 2]) {
  net = []; const t0 = Date.now();
  await page.goto(process.argv[2], { waitUntil: 'load' });
  await page.waitForTimeout(+process.argv[3] || 60000);
  const keys = await page.evaluate(async () => { const c = await caches.open('xc-big-assets-v1'); const out = []; for (const k of await c.keys()) { const r = await c.match(k); out.push(k.url.split('/').pop() + ' ' + (await r.blob()).size); } return out; });
  console.log(`pass ${pass}: network requests for big file: ${JSON.stringify(net)}; cache: ${JSON.stringify(keys)}`);
}
await b.close();
