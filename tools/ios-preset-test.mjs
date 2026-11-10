import { chromium } from 'playwright';
import {readFile} from 'node:fs/promises';
import assert from 'node:assert/strict';
const source=await readFile(new URL('../osu/osu.Web/wwwroot/js/xc-launcher.js',import.meta.url),'utf8');
const browser=await chromium.launch({executablePath:process.env.HOME+'/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',args:['--no-sandbox']});
try {
 const page=await browser.newPage({userAgent:'Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 Version/26.0 Mobile Safari/604.1'});
 await page.route('https://ios.test/**',r=>r.fulfill({contentType:r.request().url().endsWith('.js')?'text/javascript':'text/html',body:r.request().url().endsWith('.js')?source:'<!doctype html>'}));
 await page.goto('https://ios.test/');
 const result=await page.evaluate(async()=>{
  const m=await import('/launcher.js');
  localStorage.setItem('xc.launcher.v1',JSON.stringify({telemetry:false,lang:'fr',fullscreen:true}));
  const first=m.loadOptions();
  const launch=m.buildLaunch(first);
  m.saveOptions({...first,workers:6,iosCompat:false});
  const second=m.loadOptions();
  Object.defineProperty(document.documentElement,'requestFullscreen',{value:undefined,configurable:true});
  const missing=await m.requestGameFullscreen();
  Object.defineProperty(document.documentElement,'requestFullscreen',{value:()=>{throw new Error('unsupported')},configurable:true});
  const throws=await m.requestGameFullscreen();
  Object.defineProperty(document.documentElement,'requestFullscreen',{value:()=>Promise.reject(new Error('denied')),configurable:true});
  const rejected=await m.requestGameFullscreen();
  return {first,launch,second,missing,throws,rejected};
 });
 assert.equal(result.first.workers,4); assert.equal(result.first.telemetry,false); assert.equal(result.first.lang,'fr');
 assert.equal(result.launch.fullscreen,false); assert.equal(result.launch.iosCompat,true);
 assert.equal(result.second.workers,6); assert.equal(result.second.iosCompat,false,'migration runs only once');
 assert.equal(result.missing,false); assert.equal(result.throws,false); assert.equal(result.rejected,false);
 console.log('PASS: iOS preset defaults, preserved privacy/language, one-time migration, absent/throwing/rejected fullscreen APIs');
} finally {await browser.close();}
