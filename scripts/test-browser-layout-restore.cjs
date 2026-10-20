const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const source = fs.readFileSync(path.join(__dirname, '../BIMaestro/les couleurs/Changement auto des couleurs/ColoringStateManager.cs'), 'utf8');
const restore = source.slice(source.indexOf('  const restoreAppearance=target=>{'), source.indexOf('  const theme={', source.indexOf('  const restoreAppearance=target=>{')));
const remember = source.slice(source.indexOf('    const originals=new Map();'), source.indexOf('    let badgeStyle=', source.indexOf('    const originals=new Map();')));
(async () => {
 const browser = await chromium.launch({channel:'msedge',headless:true});
 try {
  const page = await browser.newPage({viewport:{width:700,height:1100}});
  await page.setContent('<div id="tree" style="height:220px;overflow:auto;background-color:rgb(240,240,240)">' + Array.from({length:80},(_,i)=>`<div style="height:24px">Vue ${i}</div>`).join('') + '</div>');
  const results = await page.evaluate(({restore,remember}) => {
   const run = new Function(restore + remember + `
    const tree=document.getElementById('tree');
    remember(tree,'background-color'); remember(tree,'color');
    tree.style.setProperty('background-color','#ffeeaa','important');
    tree.style.setProperty('color','#440000','important');
    tree.style.height='900px'; tree.style.width='500px'; tree.style.transform='translateX(3px)';
    restoreAppearance({originals});
    const first={height:tree.clientHeight,width:tree.clientWidth,transform:tree.style.transform,color:tree.style.color,background:tree.style.backgroundColor};
    // Migration from older scripts must not restore their stale 220px height.
    restoreAppearance({originals:new Map([[tree,'height:220px;background-color:rgb(230,230,230);color:rgb(12,34,56) !important']])});
    const legacy={height:tree.clientHeight,color:tree.style.color,priority:tree.style.getPropertyPriority('color')};
    restoreAppearance({originals:new Map([[tree,null]])});
    const absent={height:tree.clientHeight,width:tree.clientWidth,color:tree.style.color};
    for(let i=0;i<30;i++){
      originals.clear(); remember(tree,'background-color');remember(tree,'color');
      tree.style.setProperty('color','#8899aa','important');
      tree.style.height=(i%2?970:760)+'px';
      restoreAppearance({originals});
      if(tree.clientHeight!==(i%2?970:760))throw new Error('Height rolled back on switch '+i);
    }
    tree.scrollTop=tree.scrollHeight;
    const bottom=tree.lastElementChild.getBoundingClientRect();
    const viewport=tree.getBoundingClientRect();
    return {first,legacy,absent,lastVisible:bottom.bottom<=viewport.bottom+1,scrollHeight:tree.scrollHeight,height:tree.clientHeight};
   `);
   return run();
  }, {restore,remember});
  assert.equal(results.first.height,900);
  assert.equal(results.first.width,500);
  assert.equal(results.first.transform,'translateX(3px)');
  assert.equal(results.first.color,'');
  assert.equal(results.first.background,'rgb(240, 240, 240)');
  assert.equal(results.legacy.height,900);
  assert.equal(results.legacy.color,'rgb(12, 34, 56)');
  assert.equal(results.legacy.priority,'important');
  assert.equal(results.absent.height,900);
  assert.equal(results.absent.width,500);
  assert.equal(results.absent.color,'');
  assert.equal(results.lastVisible,true);
  assert.ok(results.scrollHeight>results.height);
  console.log('PASS: native layout survives appearance reset, legacy migration and 30 project switches; last view remains reachable.');
 } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
