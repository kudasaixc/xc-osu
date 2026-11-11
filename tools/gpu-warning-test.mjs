// Launcher warns when WebGL reports a CPU rasteriser, and stays silent on a real GPU.
import { chromium } from 'playwright';
import {readFile} from 'node:fs/promises';
import assert from 'node:assert/strict';
const root=new URL('../osu/osu.Web/wwwroot/',import.meta.url);
const browser=await chromium.launch({executablePath:process.env.HOME+'/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',args:['--no-sandbox']});
const WARP='ANGLE (Microsoft, Microsoft Basic Render Driver (0x0000008C) Direct3D11 vs_5_0 ps_5_0, D3D11)';
const REAL='ANGLE (AMD, AMD Radeon RX 6650 XT (0x000073EF) Direct3D11 vs_5_0 ps_5_0, D3D11)';
async function run(renderer,lang,ua){
 const html=(await readFile(new URL('index.html',root),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');
 const page=await browser.newPage({viewport:{width:1280,height:900},userAgent:ua});
 await page.route('https://gpu.test/**',async r=>{
  const path=new URL(r.request().url()).pathname;
  if(path.endsWith('.json'))return r.fulfill({status:404,body:''});
  if(path.endsWith('.js'))return r.fulfill({contentType:'text/javascript',body:await readFile(new URL(path.slice(1),root),'utf8')});
  return r.fulfill({contentType:'text/html',body:html});
 });
 await page.goto('https://gpu.test/');
 // Fake the renderer string: the headless test machine has no GPU of the kind under test.
 await page.evaluate((renderer)=>{
  const fake={getExtension:n=>n==='WEBGL_debug_renderer_info'?{UNMASKED_RENDERER_WEBGL:1}:null,getParameter:()=>renderer};
  const real=HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext=function(type,...a){return type==='webgl2'?fake:real.call(this,type,...a);};
 },renderer);
 await page.evaluate(async(lang)=>{
  const {Launcher,loadOptions}=await import('/js/xc-launcher.js');const {makeT}=await import('/js/xc-i18n.js');
  window.l=new Launcher(document.querySelector('#xc-launcher'),makeT(lang),{options:loadOptions(),build:'test',blocked:false});
 },lang);
 await page.waitForFunction(()=>document.querySelector('#ln-sys')?.children.length>0);
 const box=page.locator('#ln-gpuwarn');
 const out={visible:await box.isVisible(),text:await box.textContent(),w:await page.evaluate(()=>({c:document.documentElement.clientWidth,s:document.documentElement.scrollWidth}))};
 if(renderer===WARP&&lang==='fr')await page.screenshot({path:process.env.XC_SHOT||'/tmp/xc-gpu-warning.png'});
 await page.close();return out;
}
try{
 const chrome='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36';
 let r=await run(WARP,'fr',chrome);
 assert(r.visible,'warning shown on WARP');assert(r.text.includes('Microsoft Basic Render Driver'));assert(r.text.includes('chrome://settings/system'));assert(r.text.includes('chrome://gpu'));assert(!r.text.includes('[['));assert(!r.text.includes('{0}'));
 r=await run(WARP,'en',chrome+' Edg/157.0.0.0');assert(r.visible);assert(r.text.includes('edge://settings/system'));
 r=await run('ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)','en','Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:159.0) Gecko/20100101 Firefox/159.0');assert(r.visible);assert(r.text.includes('about:support'));
 r=await run(REAL,'fr',chrome);assert(!r.visible,'no warning on a real GPU');assert.equal(r.text,'');
 r=await run('ANGLE (AMD, Radeon R9 200 Series Direct3D11 vs_5_0 ps_5_0), or similar','fr',chrome);assert(!r.visible);
 r=await run('Apple GPU','fr',chrome);assert(!r.visible);
 console.log('PASS: software-renderer warning shown for WARP/SwiftShader with browser-specific steps, hidden on real GPUs');
}finally{await browser.close();}
