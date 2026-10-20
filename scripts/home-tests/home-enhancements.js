/* Optional in-memory arrangement experiment. Native Home cards and handlers retained. */
(function(){
  'use strict';
  if(!/\/ModelBrowser\/resource\/(?:index|udabrowser)\.html(?:[?#]|$)/i.test(location.href.replace(/\\/g,'/')))return {applied:false,reason:'Not Home'};
  if(window.__bimaestroHomeEnhance)return window.__bimaestroHomeEnhance.inspect();
  var changes=[],mode=null;
  function snapshot(node){return {node:node,title:node.getAttribute('title'),style:node.getAttribute('style')};}
  function restore(){changes.forEach(function(c){['title','style'].forEach(function(a){if(c[a]===null)c.node.removeAttribute(a);else c.node.setAttribute(a,c[a]);});});changes=[];mode=null;return inspect();}
  function cards(){return Array.prototype.slice.call(document.querySelectorAll('[data-testid="tile-card"],.RecentFilesView__thumbnails-thumbnail'));}
  function describe(card){
    var image=card.querySelector('[data-testid="thumbnailImage"],img[alt]');
    var label=card.querySelector('.tile-title,.Thumbnail__caption');
    var path=card.id && /\.(rvt|rfa)$/i.test(card.id) ? card.id : image ? image.getAttribute('alt')||'' : '';
    var kind=/\.rfa$/i.test(path)?'RFA':/\.rvt$/i.test(path)?'RVT':'OTHER';
    return {node:card,path:path,kind:kind,name:label?label.textContent.trim():path};
  }
  function inspect(){return {mode:mode,changedNodes:changes.length,cards:cards().map(function(c){var d=describe(c);return {name:d.name,path:d.path,kind:d.kind,title:c.getAttribute('title'),order:c.style.order,borderColor:c.style.borderLeftColor};})};}
  function apply(options){
    options=options||{};
    var uda=document.querySelector('[data-testid="recent-container"]') && document.querySelector('[data-testid="open-revit-file-button"]');
    var legacy=document.querySelector('.MbSidePanel') && document.querySelector('.RecentFilesView');
    if(!uda&&!legacy)return {applied:false,reason:'Home signatures absent'};
    restore();
    var items=cards().map(describe);
    items.forEach(function(d){
      changes.push(snapshot(d.node));
      if(d.path)d.node.setAttribute('title',d.path);
      var color=d.kind==='RFA'?'#b887ff':d.kind==='RVT'?'#37c7df':'#f2ba62';
      d.node.style.setProperty('border-left','4px solid '+color,'important');
      var badge=d.node.querySelector('.file-type-wrapper');
      if(badge){changes.push(snapshot(badge));badge.setAttribute('title',d.kind);badge.style.setProperty('background-color',color,'important');badge.style.setProperty('border-radius','4px','important');}
    });
    if(options.group || options.alphabetical){
      items.sort(function(a,b){var group=options.group?a.kind.localeCompare(b.kind):0;return group || (options.alphabetical?a.name.localeCompare(b.name,undefined,{numeric:true,sensitivity:'base'}):0);});
      items.forEach(function(d,i){d.node.style.setProperty('order',String(i),'important');});
    }
    mode={group:!!options.group,alphabetical:!!options.alphabetical};
    return {applied:true,evidence:inspect()};
  }
  window.__bimaestroHomeEnhance={apply:apply,restore:restore,inspect:inspect};
  return inspect();
})();
