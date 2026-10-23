const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const playwright = process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,
  '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const { chromium } = require(playwright);
const source = fs.readFileSync(process.env.APPEARANCE_SOURCE || path.join(__dirname,
  '../BIMaestro/les couleurs/Changement auto des couleurs/ColoringStateManager.cs'), 'utf8');
function fragment(start, end) {
  const from = source.indexOf(start), to = source.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `Production fragment missing: ${start}`);
  return source.slice(from, to).replaceAll('""', '"');
}
const functions = [
  fragment('  const restoreAppearance=target=>{', '  const theme={'),
  fragment('    const originals=new Map();', '    let badgeStyle='),
  fragment('    const clearViewTypeMarkers=()=>{', '    const resolveViewKind='),
  fragment('    const markViewTypeRow=', '    const getCategoryRules='),
  fragment('    const applyCategoryRules=', '    const paintViewTypes=')
].join('\n');
(async () => {
  const browser = await chromium.launch({channel: 'msedge', headless: true});
  try {
    const page = await browser.newPage();
    await page.setContent('<div id="wrap"><span id="label" style="color:rgb(12,34,56) !important">Vue</span></div>');
    const result = await page.evaluate(functions => new Function('theme', functions + `
      let clickedRowId='', clickedRowName='';
      const label=document.getElementById('label'), wrap=document.getElementById('wrap');
      const row={label,wrap,id:'1',name:'vue'};
      const snapshot=()=>[label.style.color,label.style.getPropertyPriority('color')];
      markViewTypeRow(row,'plan',false);
      const colored=snapshot(); clearViewTypeMarkers(); const restored=snapshot();
      const effects=['Texte','Souligné','Gras','Italique','Barré'];
      const cleared=effects.map(effect=>{
        applyCategoryRules(row,[{effect,color:'#aabbcc'}]); clearViewTypeMarkers();
        return snapshot();
      });
      for(let i=0;i<30;i++){
        markViewTypeRow(row,'plan',false); clearViewTypeMarkers();
      }
      restoreAppearance({originals}); const reset=snapshot();
      label.style.removeProperty('color'); originals.clear();
      markViewTypeRow(row,'plan',false); clearViewTypeMarkers(); const absent=snapshot();
      theme.appearanceEnabled=true;
      markViewTypeRow(row,'plan',false); clearViewTypeMarkers(); const base=snapshot();
      return {colored,restored,cleared,reset,absent,base};
    `)({appearanceEnabled:false, text:'#112233', viewColorTarget:'text', viewColors:{plan:'#ff0000'}}), functions);
    assert.deepEqual(result.colored, ['rgb(255, 0, 0)', 'important']);
    assert.deepEqual(result.restored, ['rgb(12, 34, 56)', 'important']);
    result.cleared.forEach(value => assert.deepEqual(value, result.restored));
    assert.deepEqual(result.reset, result.restored);
    assert.deepEqual(result.absent, ['', '']);
    assert.deepEqual(result.base, ['rgb(17, 34, 51)', 'important']);
    console.log('PASS: view colors, five folder text effects, 30 refreshes and reset preserve native inline colors and priority.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
