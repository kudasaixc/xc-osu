// Smoke-test a served build with a real browser and actual engine telemetry; no production writes.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
const base = process.argv[2] || 'http://127.0.0.1:8813';
const browser = await chromium.launch({executablePath:process.env.HOME+'/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',args:['--no-sandbox','--use-angle=swiftshader','--enable-unsafe-swiftshader','--ignore-gpu-blocklist','--autoplay-policy=no-user-gesture-required']});
const batches = [], errors = [];
try {
  const page = await browser.newPage();
  page.on('pageerror',e=>{errors.push(e.message); console.log('PAGE ERROR',e.message)});
  page.on('console',m=>{if (/fatal|telemetry|entered Intro|entered MainMenu|window skipped/.test(m.text())) console.log(m.text().slice(0,500));});
  await page.route('**/api/xc/tm',async route=>{
    const b=JSON.parse(route.request().postData()); batches.push(b);
    await route.fulfill({contentType:'application/json',body:JSON.stringify({seq:Math.max(0,...b.items.map(i=>i.q))})});
  });
  await page.addInitScript(()=>localStorage.setItem('xc.launcher.v1',JSON.stringify({autoLaunch:true,startGate:false,fullscreen:false,telemetry:true,rev:2})));
  await page.goto(base+'/?launch');
  for(let i=0;i<24;i++) {
    await page.waitForTimeout(5000);
    const items=batches.flatMap(b=>b.items);
    if(i%6===0) console.log('progress',i*5,'seconds',items.length,'items');
    if(items.some(e=>e.k==='w') && items.some(e=>e.n==='flight') && items.some(e=>e.n==='boot')) break;
  }
  const items=batches.flatMap(b=>b.items);
  assert(items.some(e=>e.k==='w'),'actual engine frame windows');
  assert(items.some(e=>e.k==='s'),'actual engine stutters');
  assert(items.some(e=>e.n==='flight'),'actual circular capture');
  const captures=new Map();
  for(const e of items.filter(e=>e.n==='flight')) {if(!captures.has(e.at))captures.set(e.at,[]);captures.get(e.at).push(e);}
  const complete=[...captures.values()].find(parts=>new Set(parts.map(p=>p.part)).size===parts[0].parts && parts.flatMap(p=>p.frames).some(f=>f[0]<0) && parts.flatMap(p=>p.frames).some(f=>f[0]>1000));
  assert(complete,'complete capture with frames before and after the trigger');
  assert.equal(errors.length,0,'browser errors');
  console.log('PASS: real engine windows, stutters and complete before/after capture;',items.length,'events');
} finally {await browser.close();}
