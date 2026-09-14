const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'../tmp/atmospheres');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  try {
    for(const mode of ['frosted','blueprint','ink']) {
      const page=await browser.newPage({viewport:{width:380,height:600}});
      const errors=[];page.on('pageerror',e=>errors.push(e.message));
      await page.setContent('<style>body{margin:0;font:15px Segoe UI}.row{padding:8px 24px}</style><h3 style="padding:20px">Arborescence du projet</h3>'+['▾ Vues','　▾ Architecture','　　Plan du rez-de-chaussée','　　Plan du premier étage','　▾ Structure','　　Coffrage','　▾ Fluides','　　Ventilation','　　Plomberie','　▾ Coordination','　　Vue 3D de synthèse'].map(n=>'<div class="row">'+n+'</div>').join(''));
      const script=fs.readFileSync(path.join(root,mode+'.js'),'utf8');
      await page.evaluate(script);
      let css=await page.evaluate(()=>{const s=getComputedStyle(document.body);return {image:s.backgroundImage,play:s.animationPlayState,mode:document.body.getAttribute('data-bimaestro-bubble-surface')};});
      assert.equal(css.mode,mode);assert.notEqual(css.image,'none');assert.equal(css.play,'paused');
      await page.screenshot({path:path.join(root,mode+'.png')});
      // Toggle animation using the exact generated stylesheet; no opacity applied to the text.
      await page.evaluate(()=>{const style=document.getElementById('bimaestro-project-browser-badges');style.textContent=style.textContent.replaceAll('animation-play-state:paused','animation-play-state:running');});
      assert.equal(await page.evaluate(()=>getComputedStyle(document.body).animationPlayState),'running');
      await page.emulateMedia({reducedMotion:'reduce'});
      assert.equal(await page.evaluate(()=>getComputedStyle(document.body).animationName),'none');
      assert.deepEqual(errors,[]);await page.close();
    }
    console.log('PASS: 3 browser backgrounds render; animation paused/running and reduced-motion handling.');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
