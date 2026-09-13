const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const publicDir=path.join(root,'.sites/bimaestro-viewer-mep-audit/public');
const cases=JSON.parse(fs.readFileSync(path.resolve(root,process.env.MEP_PARITY_FILE || 'tmp/mep-parity.json'),'utf8'));
if (process.argv.includes('--permutations')) {
 for (const item of [...cases]) {
  const request=structuredClone(item.request);
  for(const key of ['elements','connections','valves','sources']) request.graph[key].reverse();
  cases.push({name:item.name+'-permuted',request,expected:item.expected});
 }
}
const server=http.createServer((req,res)=>{
  const file=path.resolve(publicDir,'.'+decodeURIComponent(req.url.split('?')[0]));
  if(!file.startsWith(publicDir+path.sep)||!fs.existsSync(file)||fs.statSync(file).isDirectory()){res.writeHead(404);res.end();return;}
  res.setHeader('Content-Type',file.endsWith('.wasm')?'application/wasm':file.endsWith('.js')?'text/javascript':file.endsWith('.json')?'application/json':'text/html');fs.createReadStream(file).pipe(res);
});
const ordinal=(a,b)=>a<b?-1:a>b?1:0;
function summary(result){const g=result.graph;return {states:[...g.elements].sort((a,b)=>ordinal(a.key,b.key)).map(e=>({key:e.key,flowState:e.flowState,connectedToInlet:e.connectedToInlet,connectedToReturn:e.connectedToReturn,paths:e.paths.map(p=>({flowState:p.flowState,hasCirculation:p.hasCirculation,flowForward:p.flowForward,directionState:p.directionState,directionReason:p.directionReason,connectedToInlet:p.connectedToInlet,connectedToReturn:p.connectedToReturn}))})),valves:[...g.valves].sort((a,b)=>ordinal(a.elementKey,b.elementKey)).map(v=>({elementKey:v.elementKey,isClosed:v.isClosed,upstreamState:v.upstreamState,downstreamState:v.downstreamState})),report:g.impactReport,reportText:result.reportText};}
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage();page.on('pageerror',e=>console.error('browser:',e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/mep-engine/index.html`);
  await page.evaluate(async()=>await ready);
  let failures=0;
  for(const [index,item] of cases.entries()){
   const result=JSON.parse(await page.evaluate(async request=>await DotNet.invokeMethodAsync('BIMaestro.MepEngine.Browser','Calculate',JSON.stringify(request)),item.request));
   try{assert.deepEqual(summary(result),item.expected);}catch(error){failures++;fs.writeFileSync(path.join(root,'tmp/mep-parity-failure-'+index+'.json'),JSON.stringify({name:item.name,expected:item.expected,actual:summary(result)},null,2));console.error('FAIL',item.name,error.message.slice(0,350));}
   if((index+1)%25===0)console.log(`${index+1}/${cases.length} C# / browser cases compared`);
  }
  console.log(`${cases.length} parity cases, ${failures} differences`);
  if(failures)process.exitCode=1;
 }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
