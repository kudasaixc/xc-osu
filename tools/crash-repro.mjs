// Imports a beatmap archive into a served build, starts it from song select and reports what the engine logs.
// usage: node tools/crash-repro.mjs <base url> <file.osz> [seconds in gameplay] [screenshot.png]
import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
const [base = 'http://127.0.0.1:8813', osz, playSeconds = '25', shot] = process.argv.slice(2);
const browser = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',
  args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required'] });
const seen = [], errors = [];
let status = 1;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const t0 = Date.now(), stamp = () => ((Date.now() - t0) / 1000).toFixed(1).padStart(6);
  page.on('pageerror', (e) => { errors.push(e.message); console.log(stamp(), 'PAGE ERROR', e.message); });
  page.on('console', (m) => {
    const text = m.text(); seen.push(text);
    if (m.type() === 'error' || m.type() === 'warning' || /entered |\[launch\]|import|video|decoder|storyboard|unhandled|fatal|exception/i.test(text)) console.log(stamp(), m.type(), text.slice(0, 3000));
  });
  await page.route('**/api/**', (route) => route.fulfill({ status: 404, body: '' }));
  if (osz) await page.route('**/__repro.osz', async (route) => route.fulfill({ contentType: 'application/octet-stream', body: await readFile(osz) }));
  await page.addInitScript(() => localStorage.setItem('xc.launcher.v1', JSON.stringify({ autoLaunch: true, startGate: false, fullscreen: false, telemetry: false, log: 'verbose', rev: 2 })));
  await page.goto(base + '/?launch&mute');
  // wasm linear memory (the engine's whole address space, capped by EmccMaximumHeapSize) whenever it grows
  let lastBytes = 0;
  const memory = setInterval(async () => {
    const bytes = await page.evaluate(() => globalThis.getDotnetRuntime?.(0)?.Module?.HEAP8?.buffer.byteLength || 0).catch(() => 0);
    if (bytes > lastBytes + 16 * 1048576) { lastBytes = bytes; console.log(stamp(), 'wasm memory', (bytes / 1048576).toFixed(0), 'MB'); }
  }, 3000);
  const waitFor = async (re, seconds, from = 0) => {
    for (let i = 0; i < seconds * 4; i++) { if (seen.slice(from).some((l) => re.test(l))) return true; await page.waitForTimeout(250); }
    return false;
  };
  if (!await waitFor(/entered MainMenu/, 240)) throw new Error('main menu not reached');
  await page.waitForTimeout(4000);
  // a fresh profile opens the first-run setup over the menu
  await page.mouse.move(640, 360);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(2500);
  // ... and the login panel takes the keyboard once it closes
  await page.keyboard.press('Escape');
  await page.waitForTimeout(1500);
  if (osz) {
    const mark = seen.length;
    await page.evaluate(async (name) => {
      const data = await (await fetch('/__repro.osz')).blob();
      const dt = new DataTransfer(); dt.items.add(new File([data], name));
      dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
    }, path.basename(osz));
    console.log(stamp(), 'archive dropped');
    await waitFor(/Import successfully completed|Imported /, 120, mark);
    await page.waitForTimeout(6000);
  }
  // main menu by mouse: the keyboard may belong to an open panel (login) on a fresh profile
  const step = async (name) => { if (shot) await page.screenshot({ path: shot.replace(/\.png$/, '-' + name + '.png'), timeout: 120000 }).catch(() => { }); };
  await step('menu');
  await page.keyboard.press('p'); await page.waitForTimeout(2500);   // opens the menu; a guest gets the login panel, which takes the keyboard
  await page.mouse.click(1073, 18); await page.waitForTimeout(1500); // toolbar user button: closes it
  await step('play');
  for (let i = 0; i < 4 && !seen.some((l) => /entered \S*SongSelect#/.test(l)); i++) { await page.keyboard.press('p'); await page.waitForTimeout(1500); }
  await waitFor(/entered .*SongSelect/, 40);
  await page.waitForTimeout(5000);
  await step('select');
  const mark = seen.length;
  await page.keyboard.press('Enter');
  const loader = await waitFor(/entered \S*PlayerLoader#/, 30, mark);
  const player = loader && await waitFor(/entered \S*Player#/, 90, mark);
  console.log(stamp(), 'player loader:', loader, 'player:', player);
  await page.waitForTimeout(+playSeconds * 1000);
  if (shot) await page.screenshot({ path: shot, timeout: 120000 }).catch((e) => console.log('screenshot failed', e.message.split('\n')[0]));
  const back = seen.slice(mark).some((l) => /entered \S*SongSelect#/.test(l));
  const dump = seen.slice(mark).some((l) => /thread handle|memory access out of bounds|RuntimeError/.test(l));
  console.log(stamp(), `result: loader=${loader} player=${player} returnedToSongSelect=${back} runtimeDump=${dump} pageErrors=${errors.length}`);
  status = player && !back && !dump && !errors.length ? 0 : 2;
  clearInterval(memory);
} catch (e) { console.log('FAILED', e.message); } finally { await browser.close(); }
process.exit(status);
