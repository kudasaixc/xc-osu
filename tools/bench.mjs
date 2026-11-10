// usage: node bench.mjs <url> <outdir> <script-json>   (same script ops as shot.mjs + ["wheel",dy,count])
import { chromium } from 'playwright';
const [url, out, script = '[]'] = process.argv.slice(2);
const b = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required'] });
const ctx = await b.newContext({ viewport: { width: 1280, height: 720 } });
await ctx.addInitScript((o) => { try { localStorage.setItem('xc.launcher.v1', JSON.stringify(o)); } catch { } }, JSON.parse(process.env.XC_OPTS || '{}'));
const p = await ctx.newPage();
p.on('console', m => { const t = m.text(); if (process.env.XC_ALL || /\[perf\]|\[launch\]|error|fatal|Exception|crash/i.test(t)) console.log(t.slice(0, +(process.env.XC_MAX || 600))); });
p.on('pageerror', e => console.log('[pageerror]', e.message));
await p.goto(url);
for (const [op, a, c] of JSON.parse(script)) {
  if (op === 'click') await p.mouse.click(a, c);
  else if (op === 'move') await p.mouse.move(a, c, { steps: 5 });
  else if (op === 'key') await p.keyboard.press(a);
  else if (op === 'type') await p.keyboard.type(a, { delay: 40 });
  else if (op === 'wait') await p.waitForTimeout(a);
  else if (op === 'wheel') for (let i = 0; i < c; i++) { await p.mouse.wheel(0, a); await p.waitForTimeout(120); }
  else if (op === 'shot') await p.screenshot({ path: out + '/' + a, timeout: 120000 }).catch(e => console.log('[shot] failed ' + e.message.split('\n')[0]));
  console.log('[script] ' + op + ' ' + (a ?? ''));
}
await b.close();
