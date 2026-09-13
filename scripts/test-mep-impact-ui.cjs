const fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const {chromium}=require('C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'), fixture=path.join(root,'tmp/mep-ui-fixture');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
  const page=await browser.newPage({viewport:{width:1440,height:1000}}), errors=[];
  page.on('pageerror',e=>{errors.push(e.message); console.error('PAGE:',e.message);});
  let role='editor', scenario={revision:0,state:{valves:{},sources:{}},updated_by:'Test',updated_at:new Date().toISOString()};
  await page.route('**/mep-share',async route=>{
   const body=route.request().postDataJSON(); let data;
   if(body.action==='resolve') data={publication:{id:'ui-test',name:'Validation jalons MEP',slug:'ui-test',revision:1,expiresAt:'2099-01-01'},role,packageUrl:'http://localhost:3000/test-fixture/index.zip',scenario,events:[],realtimeToken:''};
   else if(body.action==='analysis'){scenario={...scenario,revision:scenario.revision+1,state:{...scenario.state,analysis:{...scenario.state.analysis,...body.settings}}};data={scenario};}
   else if(body.action==='scenario'){scenario={...scenario,revision:scenario.revision+1,state:{...scenario.state,valves:{[body.targetId]:body.value}}};data={scenario};}
   else if(body.action==='tile') { const names=body.names||[body.name]; data={tiles:names.map(name=>({name,url:'http://localhost:3000/test-fixture/'+name,bytes:fs.statSync(path.join(fixture,name)).size,sha256:require('node:crypto').createHash('sha256').update(fs.readFileSync(path.join(fixture,name))).digest('hex')}))}; if(body.name)data=data.tiles[0]; }
   else data={scenario};
   await route.fulfill({json:data});
  });
  await page.route('**/test-fixture/*',route=>route.fulfill({body:fs.readFileSync(path.join(fixture,path.basename(new URL(route.request().url()).pathname)))}));
  await page.goto('http://localhost:3000/#/share/'+'a'.repeat(64));
  await page.locator('.model-opening').waitFor({state:'hidden',timeout:60000}).catch(async e=>{console.log(await page.locator('body').innerText());throw e;});
  await page.getByRole('button',{name:'Analyser les coupures',exact:true}).click();
  await page.getByRole('button',{name:'Exporter le compte rendu',exact:true}).waitFor({timeout:90000});
  await page.getByText('Hypothèses et limites du réseau',{exact:true}).click();
  await page.getByRole('checkbox',{name:/Autoriser les extrémités/}).click();
  await page.waitForFunction(()=>{const input=document.querySelector('.mep-impact-panel details input');return input && !input.checked && !input.disabled;});
  assert.equal(scenario.state.analysis.allowImplicitTerminals,false);
  scenario={...scenario,revision:scenario.revision+1,state:{...scenario.state,valves:{valve:true}}};
  await page.reload();
  await page.locator('.model-opening').waitFor({state:'hidden',timeout:60000});
  await page.getByRole('button',{name:'Analyser les coupures',exact:true}).click();
  await page.getByText("Connexion à l'arrivée perdue",{exact:true}).waitFor({timeout:90000});
  const download=page.waitForEvent('download');
  await page.getByRole('button',{name:'Exporter le compte rendu',exact:true}).click();
  assert.match(fs.readFileSync(await (await download).path(),'utf8'),/Vannes fermées sur le chemin de référence/);
  await page.screenshot({path:path.join(root,'tmp/mep-impact-ui.png'),fullPage:true});
  await page.getByRole('button',{name:'Prendre l’état actuel comme référence',exact:true}).click();
  await page.getByText('Aucun changement correspondant aux filtres.',{exact:true}).waitFor();
  role='viewer'; await page.reload(); await page.locator('.model-opening').waitFor({state:'hidden',timeout:60000});
  await page.getByRole('button',{name:'Analyser les coupures',exact:true}).click();
  await page.getByText('Hypothèses et limites du réseau',{exact:true}).click();
  assert.equal(await page.getByRole('checkbox',{name:/Autoriser les extrémités/}).isDisabled(),true);
  console.log('UI: real Revit export, browser engine, shared assumptions, closure impact, report download, local reference and read-only controls passed.');
  assert.equal(errors.length,0,errors.join('\n'));
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
