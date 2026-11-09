// usage: node shot.mjs <url> <out.png> [waitMs] [clickScript]
import { chromium } from 'playwright';
const [url, out, wait = '5000', script = '[]'] = process.argv.slice(2);
const b = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required', '--remote-debugging-port=9333'] });
// raw CDP: auto-attach to every worker and print uncaught exceptions with full (wasm) stacks
{
  const ver = await (await fetch('http://127.0.0.1:9333/json/version')).json();
  const ws = new WebSocket(ver.webSocketDebuggerUrl);
  let id = 0;
  const send = (method, params = {}, sessionId) => ws.send(JSON.stringify({ id: ++id, method, params, sessionId }));
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.method === 'Target.attachedToTarget') {
      const sid = m.params.sessionId;
      send('Runtime.enable', {}, sid);
      send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true }, sid);
      send('Runtime.runIfWaitingForDebugger', {}, sid);
    } else if (m.method === 'Runtime.exceptionThrown') {
      const d = m.params.exceptionDetails;
      console.log('[worker-exception]', (d.exception?.description || d.text || '').slice(0, 6000));
    }
  };
  await new Promise(r => ws.onopen = r);
  send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
  send('Target.setDiscoverTargets', { discover: true });
}
const p = await b.newPage({ viewport: { width: 1280, height: 720 } });
p.on('console', m => console.log('[console.' + m.type() + ']', m.text().slice(0, 400)));
p.on('response', r => { if (r.status() >= 400) console.log('[http ' + r.status() + ']', r.url()); });
p.on('pageerror', e => console.log('[pageerror]', e.message));
await p.goto(url);
await p.waitForTimeout(+wait);
// script: [["click",x,y],["key","Escape"],["type","text"],["wait",ms],["shot","file.png"],["move",x,y]]
for (const [op, a, c] of JSON.parse(script)) {
  if (op === 'click') await p.mouse.click(a, c);
  else if (op === 'move') await p.mouse.move(a, c, { steps: 5 });
  else if (op === 'key') await p.keyboard.press(a);
  else if (op === 'type') await p.keyboard.type(a, { delay: 50 });
  else if (op === 'wait') await p.waitForTimeout(a);
  else if (op === 'shot') await p.screenshot({ path: a, timeout: 120000 }).catch(e => console.log('[shot] failed ' + e.message.split('\n')[0]));
  console.log('[script] ' + op + ' ' + (a ?? ''));
}
await p.screenshot({ path: out, timeout: 120000 }).catch(e => console.log('[shot] screenshot failed: ' + e.message.split('\n')[0]));
await b.close();
