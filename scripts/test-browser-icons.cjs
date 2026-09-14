const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'BIMaestro/Resources/BrowserIcons/browser-icons.js'), 'utf8');
const icon = name => 'data:image/png;base64,' + fs.readFileSync(path.join(root, 'BIMaestro/Resources/BrowserIcons', name + '.png')).toString('base64');
const config = { enabled: true, rules: [
  { name: 'Lighting', source: icon('lighting') },
  { name: 'Power', source: icon('power') },
  { name: 'HVAC', source: icon('ventilation') },
  { name: 'Plumbing', source: icon('plumbing') },
  { name: 'LIGHTING', source: icon('power') }
] };
const script = value => source.replace('__ICON_CONFIGURATION__', JSON.stringify(value));
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 700, height: 440 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.setContent(`<style>body{background:#172434;color:#edf2fa;font:16px Segoe UI;padding:24px}.vTree_treeItemWrap{display:flex;align-items:center;height:32px}.native{margin-right:8px}h3{font-size:16px}</style><h3>Icônes BIMaestro · aperçu à 18 px</h3><div id="tree">${['Lighting','Power','HVAC','Plumbing','Level 1'].map((name,i) => `<div class="vTree_treeItemWrap" id="row${i}"><button class="native" aria-label="Déplier">›</button><span class="label">${name}</span></div>`).join('')}</div>`);
    const names = await page.locator('.label').allTextContents();
    await page.evaluate(script(config));
    await page.waitForFunction(() => document.querySelectorAll('[data-bimaestro-custom-icon]').length === 4);
    assert.deepEqual(await page.locator('.label').allTextContents(), names);
    assert.equal(await page.locator('#row0 img').getAttribute('src'), config.rules[0].source);
    assert.equal(await page.locator('#row4 img').count(), 0);
    await page.evaluate(() => { window.clicked = 0; document.querySelector('#row0 button').onclick = () => window.clicked++; });
    await page.locator('#row0 button').click();
    assert.equal(await page.evaluate(() => window.clicked), 1);
    await page.evaluate(script(config));
    assert.equal(await page.locator('[data-bimaestro-custom-icon]').count(), 4);
    await page.evaluate(() => { document.querySelector('#row0 .label').textContent = '  pOwEr  '; });
    await page.waitForFunction(src => document.querySelector('#row0 img').src === src, config.rules[1].source);
    await page.evaluate(() => { document.querySelector('#row0 .label').innerHTML = '<input value="Power">'; });
    await page.waitForFunction(() => !document.querySelector('#row0 img'));
    await page.evaluate(() => { document.querySelector('#row0 .label').textContent = 'Lighting'; });
    await page.waitForFunction(() => !!document.querySelector('#row0 img'));
    // Recycled virtual row loses its old icon when its new name has no rule.
    await page.evaluate(() => { document.querySelector('#row0 .label').textContent = 'Other'; });
    await page.waitForFunction(() => !document.querySelector('#row0 img'));
    // New rows receive the icon after expanding/rebuilding a branch.
    await page.evaluate(() => { document.querySelector('#row0').remove(); document.querySelector('#tree').insertAdjacentHTML('afterbegin', '<div id="row0" class="vTree_treeItemWrap"><button class="native">›</button><span class="label">Lighting</span></div>'); });
    await page.waitForFunction(() => document.querySelectorAll('[data-bimaestro-custom-icon]').length === 4);
    await page.waitForFunction(() => Array.from(document.images).every(i => i.complete && i.naturalWidth));
    // Revit can put the label in a block/column container: the image must stay on the same line.
    for (const display of ['block', 'flex']) {
      await page.evaluate(display => {
        const row = document.querySelector('#row3');
        const label = row.querySelector('.label');
        const host = document.createElement('div');
        host.style.cssText = `display:${display};flex-direction:column`;
        row.appendChild(host);
        host.appendChild(label);
      }, display);
      await page.waitForFunction(() => document.querySelector('#row3 img').nextSibling === document.querySelector('#row3 .label'));
      const boxes = await page.evaluate(() => {
        const image = document.querySelector('#row3 img').getBoundingClientRect();
        const label = document.querySelector('#row3 .label').getBoundingClientRect();
        return { gap: label.left - image.right, centerDelta: Math.abs(image.top + image.height / 2 - label.top - label.height / 2) };
      });
      assert.ok(boxes.gap >= 4 && boxes.gap <= 6, JSON.stringify(boxes));
      assert.ok(boxes.centerDelta < 1, JSON.stringify(boxes));
    }
    fs.mkdirSync(path.join(root, 'tmp/browser-icons'), { recursive: true });
    await page.screenshot({ path: path.join(root, 'tmp/browser-icons/dark-preview.png') });
    await page.evaluate(() => { document.body.style.background = '#f5f6f8'; document.body.style.color = '#182333'; });
    await page.screenshot({ path: path.join(root, 'tmp/browser-icons/light-preview.png') });
    await page.evaluate(script({ enabled: false, rules: config.rules }));
    assert.equal(await page.locator('[data-bimaestro-custom-icon]').count(), 0);
    assert.equal(await page.locator('[data-bimaestro-icon-line]').count(), 0);
    assert.equal(await page.locator('.native').count(), 5);
    await page.evaluate(script(config));
    await page.waitForFunction(() => document.querySelectorAll('[data-bimaestro-custom-icon]').length === 4);
    await page.evaluate(() => { window.__bimaestroBrowserIcons.dispose(); delete window.__bimaestroBrowserIcons; });
    assert.equal(await page.locator('[data-bimaestro-custom-icon]').count(), 0);
    assert.deepEqual(errors, []);
    const catalog = [['lighting','Éclairage'],['power','Électricité'],['ventilation','Ventilation'],['plumbing','Plomberie'],
      ['architecture','Architecture'],['structure','Structure'],['coordination','Coordination'],
      ['heating','Chauffage'],['fire','Sécurité incendie'],['data','Réseaux informatiques']];
    await page.setContent(`<style>body{margin:0;font:16px Segoe UI;display:flex}.panel{padding:24px;width:300px;background:#172434;color:#edf2fa}.light{background:#f5f6f8;color:#182333}.row{display:flex;align-items:center;gap:6px;height:32px}img{width:18px;height:18px;object-fit:contain}</style>${['panel','panel light'].map(cls => `<div class="${cls}">${catalog.map(([id,name]) => `<div class="row"><img src="${icon(id)}">${name}</div>`).join('')}</div>`).join('')}`);
    await page.waitForFunction(() => Array.from(document.images).every(i => i.complete && i.naturalWidth));
    await page.screenshot({ path: path.join(root, 'tmp/browser-icons/catalog-preview.png') });
    console.log('PASS: matching, duplicate priority, native clicks, idempotence, rename, virtual rows, expansion, disable, disposal and light/dark previews.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
