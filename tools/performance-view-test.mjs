import { chromium } from 'playwright';
import {readFile} from 'node:fs/promises';
import assert from 'node:assert/strict';
const root=new URL('../osu/osu.Web/wwwroot/',import.meta.url);
const browser=await chromium.launch({executablePath:process.env.HOME+'/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',args:['--no-sandbox']});
try {
 const html=(await readFile(new URL('index.html',root),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');
 const page=await browser.newPage({viewport:{width:1280,height:900}});
 await page.route('https://history.test/**',async r=>{
  const path=new URL(r.request().url()).pathname;
  if(path==='/launcher/performance.json')return r.fulfill({contentType:'application/json',body:await readFile(new URL('../docs/performance/baseline-20261110.json',import.meta.url),'utf8')});
  if(path.endsWith('.js')||path.endsWith('.css'))return r.fulfill({contentType:path.endsWith('.js')?'text/javascript':'text/css',body:await readFile(new URL(path.slice(1),root),'utf8')});
  return r.fulfill({contentType:'text/html',body:html});
 });
 await page.goto('https://history.test/');
 await page.evaluate(async()=>{
  const {Launcher,loadOptions}=await import('/js/xc-launcher.js');const {makeT}=await import('/js/xc-i18n.js');
  const l=new Launcher(document.querySelector('#xc-launcher'),makeT('fr'),{options:loadOptions(),build:'pacing1',blocked:false});l.show('status');
 });
 await page.waitForFunction(()=>document.querySelector('#ln-performance table'));
 const content=await page.locator('#ln-performance').textContent();assert(content.includes('16.997'));assert(content.includes('(33, 50]'));
 await page.screenshot({path:'/tmp/xc-performance-history.png'});
 await page.setViewportSize({width:393,height:852});
 const size=await page.locator('#ln-performance').evaluate(e=>({client:e.clientWidth,scroll:e.scrollWidth}));
 assert(size.scroll>size.client,'wide table scrolls on mobile');
 console.log('PASS: real baseline renders in launcher status, P99 bounds and horizontal scroll on mobile');
}finally{await browser.close();}
