(() => {
  const config = __ICON_CONFIGURATION__;
  const key = '__bimaestroBrowserIcons';
  const signature = '2:' + JSON.stringify(config);
  if (window[key] && window[key].signature === signature) return;
  if (window[key]) window[key].dispose();
  delete window[key];
  if (!config.enabled) return;
  const normalize = value => (value || '').replace(/[\u200B-\u200D\uFEFF]/g, '')
    .replace(/\s+/g, ' ').trim().toLocaleLowerCase();
  const rules = new Map();
  config.rules.forEach(rule => { if (!rules.has(normalize(rule.name))) rules.set(normalize(rule.name), rule.source); });
  const selector = '[class*="vTree_treeItemWrap"]';
  const marker = 'data-bimaestro-custom-icon';
  const layout = 'data-bimaestro-icon-line';
  const parents = new Set();
  const style = document.createElement('style');
  style.textContent = '[' + layout + ']{display:flex!important;flex-direction:row!important;align-items:center!important;flex-wrap:nowrap!important;}' ;
  document.head.appendChild(style);
  const cleanParents = () => parents.forEach(parent => {
    if (!parent.isConnected || !Array.from(parent.children).some(child => child.hasAttribute(marker))) {
      parent.removeAttribute(layout);
      parents.delete(parent);
    }
  });
  let frame = 0, disposed = false;
  const paint = () => {
    frame = 0;
    if (disposed) return;
    document.querySelectorAll(selector).forEach(wrap => {
      const input = wrap.querySelector('input:not([type="checkbox"]):not([type="radio"]),textarea');
      const label = Array.from(wrap.querySelectorAll('*')).find(el =>
        !el.children.length && !el.hasAttribute(marker) &&
        !el.closest('button,svg,[aria-hidden="true"]') && rules.has(normalize(el.textContent)));
      const source = label && rules.get(normalize(label.textContent));
      const existing = wrap.querySelector('[' + marker + ']');
      // Never disturb an active inline rename editor.
      if (!source || input) { if (existing) existing.remove(); return; }
      if (!label) { if (existing) existing.remove(); return; }
      label.parentNode.setAttribute(layout, '1');
      parents.add(label.parentNode);
      if (existing && existing.nextSibling === label && existing.getAttribute('src') === source) return;
      if (existing) existing.remove();
      const icon = document.createElement('img');
      icon.setAttribute(marker, '1');
      icon.setAttribute('aria-hidden', 'true');
      icon.alt = '';
      icon.draggable = false;
      icon.src = source;
      icon.style.cssText = 'width:18px!important;height:18px!important;min-width:18px!important;object-fit:contain!important;display:inline-block!important;vertical-align:middle!important;margin:0 5px 0 0!important;pointer-events:none!important;flex:0 0 18px!important;';
      label.parentNode.insertBefore(icon, label);
    });
    document.querySelectorAll('[' + marker + ']').forEach(icon => {
      if (!icon.closest(selector)) icon.remove();
    });
    cleanParents();
  };
  const schedule = () => { if (!disposed && !frame) frame = requestAnimationFrame(paint); };
  const observer = new MutationObserver(schedule);
  observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true,
    attributes: true, attributeFilter: ['class', 'value'] });
  document.addEventListener('scroll', schedule, true);
  window[key] = { signature, dispose: () => {
    disposed = true;
    observer.disconnect();
    if (frame) cancelAnimationFrame(frame);
    document.removeEventListener('scroll', schedule, true);
    document.querySelectorAll('[' + marker + ']').forEach(icon => icon.remove());
    parents.forEach(parent => parent.removeAttribute(layout));
    parents.clear();
    style.remove();
  }};
  paint();
})();
