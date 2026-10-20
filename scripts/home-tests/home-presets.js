/* In-memory probe for Autodesk ModelBrowser Home only. No Autodesk files changed.
 * Execute once in the identified Home browser, then:
 * BIMaestroHomeProbe.apply('compact'|'dark'|'blueprint'|'large');
 * BIMaestroHomeProbe.restore(); BIMaestroHomeProbe.inspect();
 * Supports legacy and UDA Home DOM signatures; no native controls replaced.
 */
(function () {
  'use strict';
  if (!/\/ModelBrowser\/resource\/(?:index|udabrowser)\.html(?:[?#]|$)/i.test(location.href.replace(/\\/g,'/')))
    return { applied:false, reason:'Not Autodesk ModelBrowser Home resource URL.' };
  if (window.BIMaestroHomeProbe) return window.BIMaestroHomeProbe.inspect();
  var id = 'bimaestro-home-probe-style';
  var root = document.documentElement;
  var previous = root.getAttribute('data-bimaestro-home-probe');
  var marked=[];
  function clearMarkers(){
    marked.forEach(function(m){if(m.previous===null)m.node.removeAttribute('data-bimaestro-home-region');else m.node.setAttribute('data-bimaestro-home-region',m.previous);});
    marked=[];
  }
  function mark(node,region){if(!node || !node.setAttribute)return;marked.push({node:node,previous:node.getAttribute('data-bimaestro-home-region')});node.setAttribute('data-bimaestro-home-region',region);}
  function markUDA(){
    clearMarkers();
    var button=document.querySelector('[data-testid="open-revit-file-button"]');
    var recent=document.querySelector('[data-testid="recent-container"]');
    var side=button;
    while(side && side.parentElement && !side.parentElement.contains(recent))side=side.parentElement;
    if(!side || !side.parentElement)return;
    var layout=side.parentElement, main=recent;
    while(main && main.parentElement!==layout)main=main.parentElement;
    if(!main || side===main)return;
    mark(side,'sidebar');mark(main,'main');mark(layout,'layout');
    var layer=recent.parentElement;
    while(layer && layer!==main){mark(layer,'main-layer');layer=layer.parentElement;}
  }
  var prefix = 'html[data-bimaestro-home-probe] ';
  var rules = function (selector, text) { return prefix + selector + '{' + text + '}'; };
  var themes = {
    compact: { bg:'#f5f7fa', panel:'#e8eef4', card:'#fff', fg:'#243449', accent:'#007ea8', width:230, tile:150, image:115, gap:12, radius:8 },
    dark: { bg:'#141c28', panel:'#1c2939', card:'#233246', fg:'#edf4ff', accent:'#65cfff', width:250, tile:180, image:145, gap:18, radius:10 },
    blueprint: { bg:'#0b2946', panel:'#102f4e', card:'#153c60', fg:'#e4f4ff', accent:'#78dfed', width:230, tile:175, image:140, gap:16, radius:2 },
    large: { bg:'#faf9f6', panel:'#eeeae2', card:'#fff', fg:'#302e2a', accent:'#a34a18', width:260, tile:230, image:190, gap:24, radius:12 }
  };
  function inspect() {
    var cards = document.querySelectorAll('[data-testid="tile-card"],.RecentFilesView__thumbnails-thumbnail');
    var metrics = [];
    var overlaps=[];
    for (var i=0;i<cards.length;i++) {
      var c=cards[i], rect=c.getBoundingClientRect ? c.getBoundingClientRect() : null;
      var label=c.querySelector ? c.querySelector('.tile-title,.Thumbnail__caption') : null;
      var info=c.querySelector ? c.querySelector('[data-testid="info-wrapper"]') : null;
      var date=info && info.children.length>1 ? info.children[1] : null;
      var lr=label && label.getBoundingClientRect ? label.getBoundingClientRect() : null;
      var dr=date && date.getBoundingClientRect ? date.getBoundingClientRect() : null;
      if(lr && dr && lr.bottom>dr.top+1) overlaps.push({index:i,text:label.textContent,titleBottom:lr.bottom,dateTop:dr.top});
      metrics.push({width:rect ? rect.width : null,height:rect ? rect.height : null,
        text:label ? label.textContent : null, labelHeight:label ? label.clientHeight : null,
        labelScrollHeight:label ? label.scrollHeight : null,
        labelClipped:label ? (label.scrollHeight>label.clientHeight+1 || label.scrollWidth>label.clientWidth+1) : null});
    }
    var ancestors=[], node=document.querySelector('[data-testid="recent-container"]');
    while (node && node.getBoundingClientRect && ancestors.length<9) {
      var r=node.getBoundingClientRect();
      ancestors.push({tag:node.tagName,className:node.className,width:r.width,height:r.height});
      node=node.parentElement;
    }
    return { url:location.href, title:document.title, preset:root.getAttribute('data-bimaestro-home-probe'),
      sidebars:document.querySelectorAll('.MbSidePanel').length,
      recentViews:document.querySelectorAll('.RecentFilesView').length,
      cards:cards.length, cardMetrics:metrics, titleDateOverlaps:overlaps, recentAncestors:ancestors,
      homeKind:document.querySelector('[data-testid="recent-container"]') ? 'uda' : 'legacy',
      styles:!!document.getElementById(id), available:Object.keys(themes) };
  }
  function apply(name) {
    var t = themes[name];
    if (!t) throw new Error('Unknown Home preset: ' + name);
    var uda=!!document.querySelector('[data-testid="recent-container"]') && !!document.querySelector('[data-testid="open-revit-file-button"]');
    if (!uda && (!document.querySelector('.MbSidePanel') || !document.querySelector('.RecentFilesView')))
      return { applied:false, reason:'Home DOM signatures absent; no changes made.', evidence:inspect() };
    var s = document.getElementById(id) || document.createElement('style');
    if(uda)markUDA();
    s.id = id;
    s.textContent = rules('.MbSidePanel','background:'+t.panel+'!important;width:'+t.width+'px!important;color:'+t.fg+'!important;') +
      rules('.RecentFilesView','background:'+t.bg+'!important;color:'+t.fg+'!important;width:calc(100vw - '+t.width+'px)!important;padding:32px!important;') +
      rules('.RecentFilesView__banner','color:'+t.fg+'!important;margin-bottom:24px!important;') +
      rules('.RecentFilesView__group-title','background:'+t.panel+'!important;color:'+t.fg+'!important;') +
      rules('.RecentFilesView__thumbnails','grid-template-columns:repeat(auto-fill,minmax('+t.tile+'px,1fr))!important;gap:'+t.gap+'px!important;padding:20px!important;') +
      rules('.RecentFilesView__thumbnails-thumbnail','width:'+t.tile+'px!important;') +
      rules('.Thumbnail','color:'+t.fg+'!important;background:'+t.card+'!important;border-radius:'+t.radius+'px!important;padding:10px!important;box-sizing:border-box!important;') +
      rules('.RecentFilesView__thumbnails-thumbnail .Thumbnail__container','height:'+t.image+'px!important;border-radius:'+t.radius+'px!important;overflow:hidden!important;border-color:'+t.accent+'66!important;') +
      rules('.Thumbnail__caption','white-space:normal!important;overflow-wrap:anywhere!important;line-height:1.4!important;') +
      rules('.Thumbnail:hover','color:'+t.accent+'!important;') +
      rules('.App__sidebar-footer-button','color:'+t.fg+'!important;') +
      rules('.App__sidebar-button-group','max-width:calc(100% - 32px)!important;') +
      rules('.MbSidePanel span, .MbSidePanel p, .MbSidePanel a, .MbSidePanel label','color:'+t.fg+'!important;') +
      rules('.MbSidePanel .IconPanel,.MbSidePanel .NavItem','color:'+t.fg+'!important;background-color:'+t.panel+'!important;') +
      rules('.MbSidePanel button, .MbSidePanel .Button','color:'+t.fg+'!important;background:'+t.card+'!important;border-color:'+t.accent+'!important;') +
      rules('.MbSidePanel button:hover, .MbSidePanel .Button:hover, .MbSidePanel button:focus','color:'+t.bg+'!important;background:'+t.accent+'!important;') +
      rules('.MbSidePanel button:hover span, .MbSidePanel .Button:hover span, .MbSidePanel button:focus span','color:'+t.bg+'!important;');
    if (uda) {
      s.textContent += rules('body','background:'+t.bg+'!important;color:'+t.fg+'!important;') +
        rules('#root, #root > div, #root > div > div, #root > div > div > div','width:100%!important;max-width:none!important;box-sizing:border-box!important;background:'+t.bg+'!important;') +
        rules('#root > div','background:'+t.bg+'!important;color:'+t.fg+'!important;') +
        rules('[data-bimaestro-home-region="sidebar"]','width:'+t.width+'px!important;min-width:'+t.width+'px!important;flex:0 0 '+t.width+'px!important;background:'+t.panel+'!important;') +
        rules('[data-bimaestro-home-region="sidebar"] div','background-color:transparent!important;color:'+t.fg+'!important;') +
        rules('[data-bimaestro-home-region="main"]','background:'+t.bg+'!important;flex:1!important;min-width:0!important;width:calc(100% - '+t.width+'px)!important;max-width:none!important;') +
        rules('[data-bimaestro-home-region="main"] > div, [data-bimaestro-home-region="main"] > div > div','background:'+t.bg+'!important;') +
        rules('[data-testid="recent-container"]','background:'+t.bg+'!important;color:'+t.fg+'!important;padding-top:20px!important;width:100%!important;max-width:none!important;box-sizing:border-box!important;') +
        rules('[data-testid="recent-container"] h3','color:'+t.fg+'!important;') +
        rules('[data-testid="recent-wrapper"], [data-testid="top-container"]','background:'+t.bg+'!important;color:'+t.fg+'!important;width:100%!important;max-width:none!important;') +
        rules('[data-testid="tile-view-wrapper"]','display:grid!important;width:100%!important;max-width:none!important;grid-template-columns:repeat(auto-fill,minmax('+t.tile+'px,1fr))!important;grid-auto-rows:minmax('+(t.image+140)+'px,auto)!important;gap:'+t.gap+'px!important;align-content:start!important;') +
        rules('[data-testid="tile-card"]','display:block!important;position:relative!important;box-sizing:border-box!important;width:auto!important;min-width:0!important;max-width:none!important;height:auto!important;min-height:'+(t.image+140)+'px!important;max-height:none!important;margin:0!important;background:'+t.card+'!important;border-radius:'+t.radius+'px!important;overflow:visible!important;border:1px solid '+t.accent+'55!important;') +
        rules('[data-testid="tile-wrapper"]','display:block!important;position:relative!important;width:100%!important;height:auto!important;min-height:'+(t.image+140)+'px!important;max-height:none!important;overflow:visible!important;background:'+t.card+'!important;') +
        rules('[data-testid="thumbnail-wrapper"]','position:relative!important;box-sizing:border-box!important;width:100%!important;height:'+t.image+'px!important;min-height:'+t.image+'px!important;max-height:'+t.image+'px!important;background:'+t.panel+'!important;') +
        rules('[data-testid="thumbnailImage"]','width:'+Math.max(65,t.image-20)+'px!important;height:'+Math.max(65,t.image-20)+'px!important;max-height:none!important;max-width:100%!important;object-fit:contain!important;') +
        rules('[data-testid="info-wrapper"]','display:block!important;position:relative!important;top:auto!important;bottom:auto!important;width:100%!important;box-sizing:border-box!important;height:auto!important;min-height:140px!important;max-height:none!important;overflow:visible!important;background:'+t.card+'!important;color:'+t.fg+'!important;padding:12px!important;') +
        rules('[data-testid="info-wrapper"] > div','height:auto!important;max-height:none!important;min-height:0!important;overflow:visible!important;') +
        rules('[data-testid="info-wrapper"] span','color:'+t.fg+'!important;') +
        rules('.tile-title','white-space:normal!important;overflow:visible!important;overflow-wrap:anywhere!important;display:block!important;-webkit-line-clamp:unset!important;line-height:1.35!important;max-height:none!important;') +
        rules('button[data-testid$="file-button"]','color:'+t.fg+'!important;background:'+t.card+'!important;border-color:'+t.accent+'!important;border-radius:'+t.radius+'px!important;') +
        rules('[data-testid="top-container"] p, [data-testid="top-container"] div','color:'+t.fg+'!important;') +
        rules('[data-testid$="id_searchbar_input"]','background:'+t.card+'!important;color:'+t.fg+'!important;') +
        rules('[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"]','fill:'+t.fg+'!important;');
      s.textContent += rules('[data-bimaestro-home-region="sidebar"] span, [data-bimaestro-home-region="sidebar"] p, [data-bimaestro-home-region="sidebar"] a, [data-bimaestro-home-region="sidebar"] nav','color:'+t.fg+'!important;') +
        rules('[data-testid="info-wrapper"]','display:flex!important;flex-direction:column!important;justify-content:flex-start!important;gap:8px!important;') +
        rules('[data-testid="info-wrapper"] > div','position:static!important;flex:0 0 auto!important;transform:none!important;margin:0!important;height:auto!important;max-height:none!important;min-height:0!important;') +
        rules('[data-testid="info-wrapper"] > div:first-child','display:flex!important;align-items:flex-start!important;') +
        rules('[data-testid="info-wrapper"] > div:first-child > .portalContainer','height:auto!important;max-height:none!important;min-height:0!important;overflow:visible!important;flex:1!important;min-width:0!important;') +
        rules('.tile-title','height:auto!important;min-height:0!important;max-height:none!important;position:static!important;transform:none!important;') +
        rules('[data-testid$="id_searchbar_input"]::placeholder','color:'+t.fg+'!important;opacity:.7!important;');
      s.textContent += rules('[data-bimaestro-home-region="layout"]','display:flex!important;width:100%!important;background:'+t.bg+'!important;') +
        rules('[data-bimaestro-home-region="main-layer"]','background:'+t.bg+'!important;width:100%!important;max-width:none!important;') +
        rules('[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"] path','fill:'+t.fg+'!important;');
    }
    if (!s.parentNode) document.head.appendChild(s);
    root.setAttribute('data-bimaestro-home-probe',name);
    return { applied:true, evidence:inspect() };
  }
  function restore() {
    clearMarkers();
    var s = document.getElementById(id); if (s) s.remove();
    if (previous === null) root.removeAttribute('data-bimaestro-home-probe');
    else root.setAttribute('data-bimaestro-home-probe',previous);
    return inspect();
  }
  window.BIMaestroHomeProbe = { apply:apply, restore:restore, inspect:inspect };
  window.__bimaestroHomeApply = apply;
  window.__bimaestroHomeRestore = restore;
  window.__bimaestroHomeInspect = inspect;
  return inspect();
})();
