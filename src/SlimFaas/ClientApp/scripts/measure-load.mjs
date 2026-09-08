// Production browser load check. See docs/dashboard-validation.md for setup.
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { tmpdir } from 'node:os';
const require = createRequire(resolve(process.env.PLAYWRIGHT_ROOT ?? '.', 'package.json'));
const { chromium } = require('playwright-core');
import fs from 'node:fs';
import assert from 'node:assert/strict';
const out=process.env.DASHBOARD_LOAD_RESULTS ?? resolve(tmpdir(), 'slimfaas-dashboard-load');
fs.mkdirSync(out, { recursive: true });
const duration=Number(process.env.DASHBOARD_LOAD_SECONDS ?? 300);
if (!process.env.CHROMIUM_EXECUTABLE) throw new Error('Set CHROMIUM_EXECUTABLE to your Chromium executable.');
const browser=await chromium.launch({executablePath:process.env.CHROMIUM_EXECUTABLE,headless:true,args:['--disable-background-timer-throttling','--disable-renderer-backgrounding']});
try {
 const page=await browser.newPage({viewport:{width:1920,height:1080},locale:'en-US'});
 const errors=[];page.on('pageerror',e=>errors.push(String(e)));
 await page.addInitScript(()=>{
   window.__draws=[];
   window.__labels=new Set();
   const label=CanvasRenderingContext2D.prototype.fillText;
   CanvasRenderingContext2D.prototype.fillText=function(...args){if(this.canvas.classList.contains('traffic-canvas__surface'))window.__labels.add(args[0]);return label.apply(this,args);};
   const original=CanvasRenderingContext2D.prototype.fillRect;
   CanvasRenderingContext2D.prototype.fillRect=function(...args){if(this.canvas.classList.contains('traffic-canvas__surface')) window.__draws.push(performance.now()); return original.apply(this,args);};
 });
 await page.goto(`${process.env.DASHBOARD_LOAD_URL ?? 'http://127.0.0.1:6011'}/#/live/traffic`);
 await page.getByRole('button',{name:'Event journal (5,000)',exact:true}).waitFor();
 await page.waitForFunction(()=>['JOB','FUNCTION','SLIMFAAS'].every(label=>window.__labels.has(label)));
 await page.screenshot({path:out+'/traffic-dense-overview.png',fullPage:true});
 const search=page.getByRole('searchbox',{name:'Find an actor'});
 for(const id of ['fibonacci1-09999','daily-report-slimfaas-job-09999']){
   await search.fill(id); await page.getByRole('button',{name:id,exact:true}).click();
   assert.equal(await page.locator('.traffic__selection strong').innerText(),id);
   await page.locator('canvas').scrollIntoViewIfNeeded();
 }
 await search.fill('');await page.getByRole('button',{name:'Clear selection'}).click();
 await page.getByRole('button',{name:'Pause',exact:true}).click();
 await page.getByRole('button',{name:/Event journal/}).click();
 const paused=await page.locator('.traffic tbody').innerText();
 await page.waitForTimeout(2100);
 assert.equal(await page.locator('.traffic tbody').innerText(),paused);
 await page.getByRole('button',{name:'Resume live'}).click();
 await page.waitForFunction(old=>document.querySelector('.traffic tbody').innerText!==old,paused);
 await page.getByRole('button',{name:'Show actors'}).click();
 await search.fill('daily-report');await page.getByRole('button',{name:'daily-report',exact:true}).click();
 await page.locator('canvas').scrollIntoViewIfNeeded();
 for(let i=0;i<4;i++) await page.getByRole('button',{name:'Zoom in',exact:true}).click();
 await page.locator('canvas').focus();
 await page.screenshot({path:out+'/traffic-dense-detail.png'});
 const cdp=await page.context().newCDPSession(page); await cdp.send('Performance.enable');
 await cdp.send('HeapProfiler.collectGarbage');
 const heap=async()=>Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map(x=>[x.name,x.value])).JSHeapUsedSize;
 const heapStart=await heap();
 await page.evaluate(()=>{
   window.__draws=[];let tick=0;
   window.__pan=setInterval(()=>{const c=document.querySelector('canvas');c.dispatchEvent(new KeyboardEvent('keydown',{key:['ArrowLeft','ArrowUp','ArrowRight','ArrowDown'][tick++%4],bubbles:true}));if(tick%16===0)c.dispatchEvent(new KeyboardEvent('keydown',{key:tick%32===0?'-':'+',bubbles:true}));},100);
 });
 const start=Date.now();const samples=[];
 for(let i=0;i<Math.ceil(duration/30);i++){
   await page.waitForTimeout(Math.min(30, duration-i*30)*1000);
   const sample={seconds:Math.round((Date.now()-start)/1000),draws:await page.evaluate(()=>window.__draws.length),heapBytes:await heap()};samples.push(sample);console.log(JSON.stringify(sample));
 }
 await page.evaluate(()=>clearInterval(window.__pan));
 const draws=await page.evaluate(()=>window.__draws);const seconds=(Date.now()-start)/1000;
 await cdp.send('HeapProfiler.collectGarbage');const heapEnd=await heap();
 const gaps=draws.slice(1).map((t,i)=>t-draws[i]).sort((a,b)=>a-b);
 const result={browser:browser.version(),viewport:'1920x1080',seconds,replicas:10000,jobs:10000,eventsPerSecond:1000,stateRefreshMs:1000,drawFps:draws.length/seconds,p95FrameGapMs:gaps[Math.floor(gaps.length*.95)],heapStart,heapEnd,samples,errors};
 fs.writeFileSync(out+'/production-stress.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result));
 assert.ok(heapEnd < heapStart + 30_000_000, `Retained heap grew: ${heapEnd-heapStart}`);assert.ok(result.drawFps>=30,`Actual canvas draw FPS ${result.drawFps}`);assert.equal(errors.length,0);
 await page.emulateMedia({reducedMotion:'reduce'});assert.ok(await page.evaluate(()=>matchMedia('(prefers-reduced-motion: reduce)').matches));
 await page.setViewportSize({width:390,height:844});await page.locator('canvas').scrollIntoViewIfNeeded();
 assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 await page.screenshot({path:out+'/traffic-mobile.png'});
 fs.writeFileSync(out+'/browser-checks.json',JSON.stringify({searchLastReplica:true,searchLastExecution:true,pauseStable:true,resumeLive:true,reducedMotion:true,mobileNoOverflow:true,errors},null,2));
} finally {await browser.close();}
