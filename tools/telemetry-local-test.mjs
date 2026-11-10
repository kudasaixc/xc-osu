import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const browser = await chromium.launch({executablePath:process.env.HOME+'/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',args:['--no-sandbox']});
try {
 const page = await browser.newPage(); let sent = 0;
 await page.route('https://local.test/**', async r => {
  const url=new URL(r.request().url());
  if(url.pathname==='/api/xc/tm') { sent++; const b=JSON.parse(r.request().postData()); return r.fulfill({contentType:'application/json',body:JSON.stringify({seq:Math.max(0,...b.items.map(i=>i.q))})}); }
  if(url.pathname.endsWith('.js')) return r.fulfill({contentType:'text/javascript',body:await readFile(new URL('../osu/osu.Web/wwwroot/js/'+url.pathname.split('/').pop(),import.meta.url),'utf8')});
  return r.fulfill({contentType:'text/html',body:'<!doctype html>'});
 });
 await page.goto('https://local.test/');
 await page.evaluate(async()=>{
  const m=await import('/xc-telemetry.js');
  m.startTelemetry({telemetry:true},'local-test',{on(fn){window.line=fn;}});
  const at=Date.now();
  globalThis.xcTelemetryEvent('worker-ui',{op:15});
  window.line({text:'[tm] '+JSON.stringify({k:'e',n:'flight',at,part:0,parts:1,scr:'Menu',cols:'offset,interval,drawWork,gcPause,gc0,gc1,tex,texQueue,latestUpdateWork',frames:[[-1000,16,1,0,0,0,0,0,1],[0,87,30,4,1,0,2,0,2],[1500,16,1,0,0,0,0,0,1]]})});
 });
 await page.waitForTimeout(2500);
 assert(await page.evaluate(()=>JSON.parse(localStorage.getItem(Object.keys(localStorage).find(k=>k.startsWith('xc.tm.v1.')))).items.length===0),'server acknowledged records');
 await page.reload(); const before=sent;
 await page.evaluate(async()=>{
  const m=await import('/xc-telemetry.js'); m.startTelemetry({telemetry:false},'off',{on(){throw new Error('opt-out listener');}});
  const v=await import('/xc-telemetry-viewer.js'); v.showLocalTelemetry(k=>k);
 });
 assert.equal(await page.locator('#xc-local-telemetry').count(),1);
 assert((await page.locator('#xc-local-telemetry').textContent()).includes('Menu'),'acknowledged flight survives reload and opt out');
 assert((await page.locator('#xc-local-telemetry').textContent()).includes('worker-ui'));
 assert.equal(await page.locator('canvas').count(),1);
 assert.equal(sent,before,'viewer sends no telemetry');
 const download=page.waitForEvent('download'); await page.getByRole('button',{name:'tm.export',exact:true}).click(); assert.equal((await download).suggestedFilename(),'xc-osu-telemetry-local.json');
 page.on('dialog',d=>d.accept()); await page.getByRole('button',{name:'tm.clear',exact:true}).click();
 assert((await page.locator('#xc-local-telemetry').textContent()).includes('tm.empty'));
 assert.equal(await page.evaluate(()=>Object.keys(localStorage).filter(k=>k.startsWith('xc.tm.local.v1.')).length),0);
 await page.evaluate(async()=>{
  document.querySelector('dialog').close();
  const {Launcher,loadOptions}=await import('/xc-launcher.js');
  const {makeT}=await import('/xc-i18n.js');
  const root=document.createElement('div');document.body.append(root);
  const launcher=new Launcher(root,makeT('fr'),{options:loadOptions(),build:'test',blocked:false});
  launcher.show('options');
 });
 await page.locator('#ln-local-telemetry').click();
 assert((await page.locator('#xc-local-telemetry').textContent()).includes('Aucun historique local'));
 console.log('PASS: acknowledged history survives reload, opt-out, curve/context viewer, export, local deletion, no viewer network');
} finally { await browser.close(); }
