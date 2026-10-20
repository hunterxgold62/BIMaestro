const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(require('node:path').join(__dirname, 'home-presets.js'), 'utf8');
function browser(url, signatures = true, previous = null) {
  const attrs = new Map();
  if (previous !== null) attrs.set('data-bimaestro-home-probe', previous);
  const children = new Map();
  const root = {
    getAttribute: key => attrs.has(key) ? attrs.get(key) : null,
    setAttribute: (key, value) => attrs.set(key, value),
    removeAttribute: key => attrs.delete(key)
  };
  const head = { appendChild(node) { children.set(node.id, node); node.parentNode = head; } };
  function element(parent, initial = null) {
    const values=new Map();if(initial!==null)values.set('data-bimaestro-home-region',initial);
    return {parentElement:parent,getAttribute:k=>values.has(k)?values.get(k):null,setAttribute:(k,v)=>values.set(k,v),removeAttribute:k=>values.delete(k),
      contains(other){while(other){if(other===this)return true;other=other.parentElement;}return false;}};
  }
  const layout=element(null), side=element(layout,'original-region'), main=element(layout), layer=element(main), recent=element(layer), button=element(side);
  const document = {
    documentElement: root, title: 'Cloud First Model Browser', head,
    querySelector: selector => !signatures ? null :
      signatures === 'uda' ? (selector.includes('open-revit-file-button') ? button : selector.includes('recent-container') ? recent : null) :
      (selector === '.MbSidePanel' || selector === '.RecentFilesView' ? {} : null),
    querySelectorAll: () => signatures ? [{}] : [],
    getElementById: id => children.get(id),
    createElement: () => ({ remove() { children.delete(this.id); this.parentNode = null; } })
  };
  const context = { window: {}, document, location: { href:url } };
  vm.createContext(context);
  vm.runInContext(source, context);
  return { ...context, attrs, children, side,main,layout,layer };
}
const goodURL = 'file:///C:/Program%20Files/Autodesk/Revit%202025/AddIns/ModelBrowser/resource/index.html';
for (const url of ['https://example.com/', 'file:///C:/other/resource/index.html', goodURL.replace('ModelBrowser', 'ProjectBrowser'), goodURL.replace('index.html', 'saveas.html')]) {
  const b = browser(url);
  assert.equal(b.window.BIMaestroHomeProbe, undefined);
  assert.equal(b.children.size, 0);
  assert.equal(b.attrs.size, 0);
}
const missing = browser(goodURL, false);
assert.equal(missing.window.__bimaestroHomeApply('dark').applied, false);
assert.equal(missing.children.size, 0);
assert.equal(missing.attrs.size, 0);
for (const kind of ['legacy','uda']) for (const name of ['compact', 'dark', 'blueprint', 'large']) {
  const b = browser(kind === 'uda' ? goodURL.replace('index.html','udabrowser.html') : goodURL,kind === 'uda' ? 'uda' : true);
  assert.equal(b.window.__bimaestroHomeApply(name).applied, true);
  assert.equal(b.children.size, 1);
  assert.equal(b.attrs.get('data-bimaestro-home-probe'), name);
  assert.ok(b.children.values().next().value.textContent.includes('grid-template-columns'));
  assert.equal(b.children.values().next().value.textContent.includes('tile-view-wrapper'),kind === 'uda');
  if(kind==='uda')assert.equal(b.side.getAttribute('data-bimaestro-home-region'),'sidebar');
  b.window.__bimaestroHomeApply(name);
  assert.equal(b.children.size, 1, 'repeat injection must not duplicate style');
  b.window.__bimaestroHomeRestore();
  assert.equal(b.children.size, 0);
  assert.equal(b.attrs.size, 0);
  if(kind==='uda'){
    assert.equal(b.side.getAttribute('data-bimaestro-home-region'),'original-region');
    for(const node of [b.main,b.layout,b.layer])assert.equal(node.getAttribute('data-bimaestro-home-region'),null);
  }
}
const prior = browser(goodURL, true, 'previous');
prior.window.__bimaestroHomeApply('compact');
prior.window.__bimaestroHomeRestore();
assert.equal(prior.attrs.get('data-bimaestro-home-probe'), 'previous');
assert.throws(() => prior.window.__bimaestroHomeApply('invalid'));
console.log('PASS: wrong URL and absent DOM guards; four presets; repeated apply; restore; previous attribute; invalid preset. Offline DOM mock only, not live Revit proof.');
