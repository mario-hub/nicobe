const fs=require('node:fs');
const path=require('node:path');
const {spawn}=require('node:child_process');
const {pathToFileURL}=require('node:url');
const vm=require('node:vm');
const root=path.resolve(__dirname,'..');
const out=path.join(root,'temp','test-results','web-page');fs.mkdirSync(out,{recursive:true});
const profile=path.join(root,'temp','web-check-'+Date.now());fs.mkdirSync(profile,{recursive:true});
const executable=['C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe','C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'].find(p=>fs.existsSync(p));
if(!executable)throw Error('Chrome or Edge is required.');
const browser=spawn(executable,[
 ...(process.env.NICOBE_HEADLESS_NO_SANDBOX==='1'?['--no-sandbox']:[]),
 '--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--disable-extensions','--disable-background-networking','--disable-component-update','--disable-sync','--disable-breakpad','--disable-crash-reporter',
 '--remote-debugging-pipe','--user-data-dir='+profile,'about:blank'
],{windowsHide:true,stdio:['ignore','ignore','ignore','pipe','pipe'],env:{...process.env,TEMP:path.join(root,'temp'),TMP:path.join(root,'temp')}});
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let nextId=0,sessionId,buffer='';const pending=new Map(),errors=[],requests=[];
browser.stdio[4].setEncoding('utf8');
browser.stdio[4].on('data',chunk=>{
 buffer+=chunk;let end;
 while((end=buffer.indexOf('\0'))>=0){
  const frame=buffer.slice(0,end);buffer=buffer.slice(end+1);if(!frame)continue;
  const message=JSON.parse(frame);
  if(message.id){const task=pending.get(message.id);if(!task)continue;pending.delete(message.id);clearTimeout(task.timeout);if(message.error)task.reject(message.error);else task.resolve(message.result);}
  else if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails.text);
  else if(message.method==='Network.requestWillBeSent')requests.push(message.params.request.url);
 }
});
function call(method,params={}){return new Promise((resolve,reject)=>{
 const id=++nextId,timeout=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout: '+method));},15000);
 pending.set(id,{resolve,reject,timeout});
 browser.stdio[3].write(JSON.stringify({id,method,params,...(sessionId&&!method.startsWith('Browser.')&&!method.startsWith('Target.')?{sessionId}:{})})+'\0');
});}
browser.on('error',error=>{for(const task of pending.values()){clearTimeout(task.timeout);task.reject(error);}pending.clear();});
(async()=>{
 try{
  const targets=(await call('Target.getTargets')).targetInfos;
  const page=targets.find(t=>t.type==='page');
  if(!page)throw Error('Headless page not found');
  sessionId=(await call('Target.attachToTarget',{targetId:page.targetId,flatten:true})).sessionId;
  async function evaluate(expression){const value=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(value.exceptionDetails)throw Error(JSON.stringify(value.exceptionDetails));return value.result.value;}
  await call('Page.enable');await call('Runtime.enable');await call('Network.enable');
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  await call('Page.navigate',{url:pathToFileURL((process.env.NICOBE_PAGE_PATH||path.join(root,'src','Website','index.html'))).href});
  for(let n=0;n<50;n++){await pause(100);if(await evaluate('document.readyState==="complete"'))break;}
  await evaluate('Promise.all([...document.images].filter(i=>i.getAttribute("src")).map(i=>{i.loading="eager";return i.decode();}))');
  const result={};
  result.desktop=await evaluate('({width:innerWidth,scrollWidth:document.documentElement.scrollWidth,images:[...document.images].filter(i=>i.getAttribute("src")).map(i=>({id:i.id,loaded:i.complete&&i.naturalWidth>0}))})');
  fs.writeFileSync(path.join(out,'desktop.png'),Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
  // Check the published link contract; do not create, inspect or download release ZIPs.
  result.downloads=await evaluate('Object.fromEntries([...document.querySelectorAll("[data-download]")].map(a=>[a.dataset.download,{href:a.getAttribute("href"),download:a.getAttribute("download")}]))');
  const expected={nicobe:'NicoBE-win-x64.zip',encoder:'NicoBE-Encoder-win-x64.zip',mkvSource:'NicoBE-MKV-Source.zip'};
  result.downloadLinks=Object.entries(expected).every(([key,name])=>result.downloads[key]?.href==='./'+name&&result.downloads[key]?.download===name);
  result.metadata=await evaluate('(()=>{const graphs=[...document.querySelectorAll("script[type=\\\"application/ld+json\\\"]")].flatMap(s=>JSON.parse(s.textContent)["@graph"]||[]);return document.querySelectorAll("h1").length===1&&document.title.includes("ニコニコ実況")&&document.querySelector("meta[name=description]").content.includes("NicoJK")&&document.querySelector("meta[property=\\\"og:title\\\"]")!==null&&graphs.filter(x=>x["@type"]==="SoftwareApplication").length===2;})()');
  const analytics=await evaluate('document.getElementById("moreread-article-analytics").textContent');
  result.canonical=await evaluate('document.querySelector("link[rel=canonical]").href==="https://www.moreread.net/apps/nicobe/index.html"&&document.querySelector("meta[property=\\\"og:url\\\"]").content==="https://www.moreread.net/apps/nicobe/index.html"');
  function analyticsCase(url,options={}){
   const location=new URL(url),scripts=[];
   const window={location,...options.window};
   const document={querySelector:selector=>selector.startsWith('meta')?(options.noindex?{}:null):(scripts.length||options.existingScript?{}:null),createElement:()=>({}),head:{appendChild:s=>scripts.push(s)}};
   vm.runInNewContext(analytics.replace(/var id = '[^']*';/, "var id = 'G-TEST12345';"),{window,document});vm.runInNewContext(analytics.replace(/var id = '[^']*';/, "var id = 'G-TEST12345';"),{window,document});
   return {scripts,configs:(window.dataLayer||[]).filter(x=>x[0]==='config'&&x[1]==='G-TEST12345').length};
  }
  const live=analyticsCase('https://www.moreread.net/apps/nicobe/');
  result.analytics=live.scripts.length===1&&live.configs===1&&live.scripts[0].async===true
   &&['file:///C:/nicobe/index.html','http://localhost:8000/','https://example.com/','https://www.moreread.net/preview/nicobe/','https://www.moreread.net:8000/apps/nicobe/'].every(url=>analyticsCase(url).scripts.length===0)
   &&analyticsCase('https://www.moreread.net/apps/nicobe/',{noindex:true}).scripts.length===0
   &&analyticsCase('https://www.moreread.net/apps/nicobe/',{existingScript:true}).scripts.length===0
   &&analyticsCase('https://www.moreread.net/apps/nicobe/',{window:{dataLayer:[['config','G-TEST12345']]}}).scripts.length===0;
  const baseline='(()=>{const h=document.querySelector(".title h1"),img=document.querySelector(".title img"),mark=document.createElement("span");mark.style.cssText="display:inline-block;width:0;height:0";h.appendChild(mark);const delta=mark.getBoundingClientRect().top-(img.getBoundingClientRect().top+img.getBoundingClientRect().height*205/256);mark.remove();return Math.abs(delta);})()';
  result.baseline=[await evaluate(baseline)];
  result.details=await evaluate('(()=>{const details=document.querySelector("#faq details");details.querySelector("summary").click();const ok=details.open;details.querySelector("summary").click();return ok&&!details.open;})()');
  result.releaseHistory=await evaluate("(()=>{const boxes=Array.from(document.querySelectorAll(\".history-text\"));const cards=Array.from(document.querySelectorAll(\"#download .downloads tbody>tr\"));const buttons=Array.from(document.querySelectorAll(\"#download .downloads .download-link\"));if(boxes.length!==2||cards.length!==2)return false;const heights=cards.map(x=>x.getBoundingClientRect().height);const tops=buttons.map(x=>x.getBoundingClientRect().top);const regions=boxes.map(x=>x.getBoundingClientRect());return Math.abs(heights[0]-heights[1])<1&&Math.abs(tops[0]-tops[1])<1&&Math.abs(regions[0].height-regions[1].height)<1&&Math.abs(regions[0].top-regions[1].top)<1&&boxes.every(box=>{const style=getComputedStyle(box);if(box.tabIndex!==0||style.overflowY!==\"auto\"||Math.abs(box.getBoundingClientRect().height-(parseFloat(style.lineHeight)*8+26))>1)return false;if(box.scrollHeight>box.clientHeight){box.scrollTop=30;const moved=box.scrollTop>0;box.scrollTop=0;return moved;}return true;});})()");
  result.anchors=await evaluate('[...document.querySelectorAll("a[href^=\\\"#\\\"]")].every(a=>document.getElementById(a.getAttribute("href").slice(1)))');
  result.mobile=[];
  for(const width of [390,320]){
   await call('Emulation.setDeviceMetricsOverride',{width,height:844,deviceScaleFactor:1,mobile:true});
   await evaluate('window.scrollTo(0,0)');await pause(150);
   result.mobile.push(await evaluate('({width:innerWidth,scrollWidth:document.documentElement.scrollWidth})'));
   result.baseline.push(await evaluate(baseline));
   if(width===390)fs.writeFileSync(path.join(out,'mobile.png'),Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
  }
  result.externalRequests=requests.filter(url=>/^https?:/.test(url));result.errors=errors;
  result.passed=result.desktop.scrollWidth<=result.desktop.width&&result.mobile.every(x=>x.scrollWidth<=x.width)&&result.desktop.images.every(i=>i.loaded)&&result.downloadLinks&&result.metadata&&result.canonical&&result.analytics&&result.baseline.every(x=>x<1)&&result.details&&result.releaseHistory&&result.anchors&&errors.length===0&&result.externalRequests.length===0;
  fs.writeFileSync(path.join(out,'checks.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));
  await call('Browser.close');if(!result.passed)process.exitCode=1;
 }catch(error){console.error(error);process.exitCode=1;}
 finally{for(const task of pending.values())clearTimeout(task.timeout);pending.clear();if(browser.exitCode===null)browser.kill();}
})();
