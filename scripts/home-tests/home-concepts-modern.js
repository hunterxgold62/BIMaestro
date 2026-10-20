/* Real Autodesk UDA Home concepts. Load home-presets.js first. In-memory only. */
(function () {
 'use strict';
 if (!/\/ModelBrowser\/resource\/udabrowser\.html(?:[?#]|$)/i.test(location.href.replace(/\\/g,'/'))) return;
 if (window.BIMaestroHomeConceptModern) return window.BIMaestroHomeConceptModern.inspect();
 var active=null, observer=null, queued=false, records=[], inserted=[], filter='all', total={};
 var root=document.documentElement, styleId='bm-modern-concept-style';
 function attr(n,k,v){if(!records.some(function(r){return r.n===n&&r.k===k;}))records.push({n:n,k:k,v:n.getAttribute(k)}); if(n.getAttribute(k)!==v)n.setAttribute(k,v);}
 function add(n,parent){inserted.push(n);parent.appendChild(n);return n;}
 function el(tag,cls,text){var n=document.createElement(tag);n.className=cls;if(text)n.textContent=text;return n;}
 function cards(){return Array.prototype.slice.call(document.querySelectorAll('[data-testid="tile-card"]'));}
 function sync(){
  if(!active)return;
  var recent=document.querySelector('[data-testid="recent-container"]'); if(!recent)return;
  if(!document.getElementById('bm-modern-toolbar')){
   var bar=el('section','bm-modern-toolbar');bar.id='bm-modern-toolbar';bar.setAttribute('aria-label','Organisation des fichiers récents');
   var heading=el('div','bm-modern-heading');heading.appendChild(el('span','bm-modern-kicker',active==='2026'?'BIMaestro · ESPACE DE TRAVAIL':'REVIT 2025 · BIBLIOTHÈQUE'));
   heading.appendChild(el('h1','',active==='2026'?'Projets & familles':'Reprendre. Explorer. Construire.'));
   heading.appendChild(el('p','',active==='2026'?'Un accueil serein pour retrouver vos maquettes et vos familles.':'Des aperçus lisibles, les bons fichiers et leur emplacement.'));
   bar.appendChild(heading);var controls=el('div','bm-modern-controls');
   ['all','rvt','rfa'].forEach(function(key){var b=el('button','bm-modern-filter',key==='all'?'Tous':key==='rvt'?'Projets RVT':'Familles RFA');b.type='button';b.setAttribute('data-bm-filter',key);b.addEventListener('click',function(){filter=key;sync();});controls.appendChild(b);});
   bar.appendChild(controls);bar.appendChild(el('span','bm-modern-note','Les filtres portent sur les fichiers affichés. Recherche et tri Autodesk restent disponibles.'));
   inserted.push(bar);recent.insertBefore(bar,recent.firstChild);
  }
  total={all:0,rvt:0,rfa:0,other:0};
  cards().forEach(function(c){var image=c.querySelector('[data-testid="thumbnailImage"]'),p=c.id||(image&&image.getAttribute('alt'))||'',m=p.match(/\.(rvt|rfa)(?:$|[?#])/i),kind=m?m[1].toLowerCase():'other';total.all++;total[kind]++;
   attr(c,'data-bm-kind',kind);attr(c,'data-bm-filtered',filter==='all'||kind===filter?'no':'yes');if(p)attr(c,'title',p);
   var info=c.querySelector('[data-testid="info-wrapper"]');if(info&&!info.querySelector('.bm-modern-meta')){var meta=el('div','bm-modern-meta');meta.appendChild(el('span','bm-modern-type',kind==='rvt'?'PROJET · RVT':kind==='rfa'?'FAMILLE · RFA':'FICHIER'));var parts=p.replace(/\\/g,'/').split('/');parts.pop();var folder=parts.pop()||'Emplacement indisponible';if(/[/\\\\]BIMaestro[/\\\\]Codex[/\\\\]Families[/\\\\]/i.test(p))folder='BIMaestro / Familles';else if(/[/\\\\]Samples[/\\\\]/i.test(p))folder='Exemples Revit';var path=el('span','bm-modern-path',folder);path.title=p;meta.appendChild(path);add(meta,info);}
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-bm-filter]'),function(b){var key=b.getAttribute('data-bm-filter');var text=(key==='all'?'Tous':key==='rvt'?'Projets RVT':'Familles RFA')+'  '+total[key];if(b.textContent!==text)b.textContent=text;var selected=key===filter?'true':'false';if(b.getAttribute('aria-pressed')!==selected)b.setAttribute('aria-pressed',selected);});
 }
 function inspect(){var measurements=cards().filter(function(c){return c.getAttribute('data-bm-filtered')!=='yes';}).map(function(c){var r=c.getBoundingClientRect?c.getBoundingClientRect():null,title=c.querySelector('.tile-title'),meta=c.querySelector('.bm-modern-meta'),t=title&&title.getBoundingClientRect?title.getBoundingClientRect():null,m=meta&&meta.getBoundingClientRect?meta.getBoundingClientRect():null;return {name:title?title.textContent:null,x:r?r.left:null,y:r?r.top:null,width:r?r.width:null,height:r?r.height:null,titleClipped:!!(title&&(title.scrollHeight>title.clientHeight+1||title.scrollWidth>title.clientWidth+1)),metadataOutsideCard:!!(r&&m&&(m.bottom>r.bottom+1||m.right>r.right+1)),titleMetadataOverlap:!!(t&&m&&t.bottom>m.top+1)};});return {concept:active,filter:filter,counts:total,cards:cards().length,visibleCards:measurements.length,cardMetrics:measurements,horizontalOverflow:!!(document.documentElement.scrollWidth>document.documentElement.clientWidth+1),toolbar:!!document.getElementById('bm-modern-toolbar'),nativeOpen:!!document.querySelector('[data-testid="open-revit-file-button"]'),styles:!!document.getElementById(styleId),url:location.href};}
 function restore(){if(observer)observer.disconnect();observer=null;active=null;inserted.forEach(function(n){if(n.parentNode)n.parentNode.removeChild(n);});inserted=[];records.forEach(function(r){if(r.v===null)r.n.removeAttribute(r.k);else r.n.setAttribute(r.k,r.v);});records=[];if(window.BIMaestroHomeProbe)window.BIMaestroHomeProbe.restore();return inspect();}
 function apply(version){
  if(version!=='2025'&&version!=='2026')throw new Error('Choose 2025 or 2026');
  if(!document.querySelector('[data-testid="recent-container"]')||!document.querySelector('[data-testid="open-revit-file-button"]'))return {applied:false,reason:'Native UDA Home signatures absent'};
  if(!window.BIMaestroHomeProbe)return {applied:false,reason:'Load home-presets.js first'};
  restore();active=version;filter='all';window.BIMaestroHomeProbe.apply('compact');attr(root,'data-bm-modern',version);
  var s=el('style','');s.id=styleId;var brand=version==='2026', bg=brand?'#F6F8FB':'#F7F5F0', ink=brand?'#0F172A':'#252D38', accent=brand?'#0F5132':'#315B86', muted=brand?'#64748B':'#667382', border=brand?'#E6EAF0':'#E4E1D9', pre='html[data-bm-modern] ';
  var rules=[
   ['body, #root, #root > div, [data-bimaestro-home-region="layout"], [data-bimaestro-home-region="main"], [data-bimaestro-home-region="main-layer"], [data-testid="recent-container"], [data-testid="recent-wrapper"], [data-testid="top-container"]','background:'+bg+'!important;color:'+ink+'!important;'],
   ['[data-bimaestro-home-region="sidebar"]','background:#FFFFFF!important;border-right:1px solid '+border+'!important;width:232px!important;min-width:232px!important;flex-basis:232px!important;'],
   ['[data-bimaestro-home-region="sidebar"] span, [data-bimaestro-home-region="sidebar"] p, [data-bimaestro-home-region="sidebar"] a','color:'+ink+'!important;'],
   ['[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"] path','fill:'+ink+'!important;'],
   ['button[data-testid$="file-button"]','border:1px solid '+border+'!important;color:'+ink+'!important;border-radius:9px!important;background:white!important;min-height:36px!important;'],
   ['button[data-testid="open-revit-file-button"]','background:'+accent+'!important;color:white!important;border-color:'+accent+'!important;'],
   ['[data-testid="recent-container"]','padding:26px 30px!important;'],
   ['[data-testid="recent-container"] h3','display:none!important;'],
   ['[data-testid="tile-view-wrapper"]','grid-template-columns:repeat(auto-fill,minmax(224px,1fr))!important;gap:20px!important;grid-template-rows:none!important;grid-auto-rows:auto!important;align-items:stretch!important;padding:2px 2px 24px!important;'],
   ['[data-testid="tile-card"]','border:1px solid '+border+'!important;border-radius:14px!important;min-height:310px!important;height:auto!important;max-height:none!important;align-self:stretch!important;background:white!important;box-shadow:0 2px 14px rgba(15,23,42,.06)!important;overflow:hidden!important;transition:border-color .15s,box-shadow .15s;'],
   ['[data-testid="tile-card"]:hover','border-color:'+(brand?'#7CC8A3':accent)+'!important;box-shadow:0 5px 18px rgba(15,23,42,.12)!important;'],
   ['[data-testid="tile-wrapper"]','min-height:310px!important;height:auto!important;max-height:none!important;display:flex!important;flex-direction:column!important;grid-template-rows:none!important;background:white!important;'],
   ['[data-testid="thumbnail-wrapper"]','height:162px!important;min-height:162px!important;max-height:162px!important;flex:0 0 162px!important;background:'+(brand?'#F9FAFB':'#EEF0F1')+'!important;border-bottom:1px solid '+border+'!important;'],
   ['[data-testid="thumbnailImage"]','height:142px!important;width:190px!important;object-fit:contain!important;'],
   ['[data-testid="info-wrapper"]','padding:14px 16px 15px!important;min-height:148px!important;height:auto!important;max-height:none!important;flex:1 0 auto!important;overflow:visible!important;background:white!important;color:'+ink+'!important;gap:9px!important;'],
   ['[data-testid="info-wrapper"] span','color:'+ink+'!important;'],
   ['.tile-title','font-size:14px!important;font-weight:600!important;line-height:1.4!important;'],
   ['[data-testid="info-wrapper"] > .portalContainer span','font-size:11px!important;color:'+muted+'!important;'],
   ['[data-testid="top-container"] p, [data-testid="top-container"] div','color:'+ink+'!important;'],
   ['[data-testid$="id_searchbar_input"]','background:white!important;border-color:'+border+'!important;color:'+ink+'!important;'],
   ['[data-bm-filtered="yes"]','display:none!important;'],
   ['.bm-modern-toolbar','margin:0 0 24px;padding:24px 26px;background:white;border:1px solid '+border+';border-radius:14px;box-shadow:0 2px 14px rgba(15,23,42,.04);border-top:4px solid '+accent+';'],
   ['.bm-modern-kicker','font:600 11px Segoe UI,sans-serif;letter-spacing:1.8px;color:'+accent+';'],
   ['.bm-modern-heading h1','font:600 28px/1.2 Segoe UI,sans-serif;letter-spacing:-.7px;color:'+ink+';margin:9px 0 7px;'],
   ['.bm-modern-heading p','font:13px/1.5 Segoe UI,sans-serif;color:'+muted+';margin:0;'],
   ['.bm-modern-controls','display:flex;gap:8px;flex-wrap:wrap;margin:20px 0 10px;'],
   ['.bm-modern-filter','cursor:pointer;background:'+bg+';border:1px solid '+border+';border-radius:8px;padding:9px 15px;color:'+ink+';font:600 12px Segoe UI,sans-serif;'],
   ['.bm-modern-filter[aria-pressed="true"]','background:'+accent+';border-color:'+accent+';color:white;'],
   ['.bm-modern-filter:focus-visible','outline:3px solid '+(brand?'#7CC8A3':'#93B6DC')+';outline-offset:2px;'],
   ['.bm-modern-note','display:none!important;'],
   ['.bm-modern-meta','display:flex!important;flex-direction:column!important;gap:5px!important;border-top:1px solid '+border+';padding-top:9px!important;margin-top:auto!important;pointer-events:none;'],
   ['[data-testid="info-wrapper"] .bm-modern-type','font:600 10px Segoe UI,sans-serif!important;letter-spacing:1px;color:'+accent+'!important;'],
   ['[data-testid="info-wrapper"] .bm-modern-path','font:11px Segoe UI,sans-serif!important;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:'+muted+'!important;']
  ];
  rules.push(['button[data-testid="open-revit-file-button"] span, button[data-testid="open-revit-file-button"] div','color:#FFFFFF!important;']);
  if(brand){
   rules.push(['.bm-modern-toolbar','display:flex;align-items:center;justify-content:space-between;gap:24px;padding:18px 20px;margin-bottom:18px;']);
   rules.push(['.bm-modern-heading h1','font-size:24px;margin:7px 0 0;']);
   rules.push(['.bm-modern-heading p','display:none;']);
   rules.push(['.bm-modern-controls','margin:0;flex-shrink:0;']);
  }else{
   rules.push(['[data-bimaestro-home-region="sidebar"]','background:#18283B!important;border-right-color:#18283B!important;']);
   rules.push(['[data-bimaestro-home-region="sidebar"] div, [data-bimaestro-home-region="sidebar"] span, [data-bimaestro-home-region="sidebar"] p, [data-bimaestro-home-region="sidebar"] a, [data-bimaestro-home-region="sidebar"] nav','color:#E9EFF7!important;']);
   rules.push(['[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"] path','fill:#E9EFF7!important;']);
   rules.push(['button[data-testid$="file-button"]','background:#243C56!important;border-color:#486079!important;color:#FFFFFF!important;']);
   rules.push(['button[data-testid="open-revit-file-button"]','background:#D8E8F7!important;border-color:#D8E8F7!important;color:#18283B!important;']);
   rules.push(['button[data-testid="open-revit-file-button"] span, button[data-testid="open-revit-file-button"] div','color:#18283B!important;']);
   rules.push(['.bm-modern-toolbar','display:grid;grid-template-columns:minmax(340px,1fr) auto;column-gap:32px;align-items:center;border-top:0;border-left:5px solid #315B86;padding:27px 30px;']);
   rules.push(['.bm-modern-heading','grid-row:1 / 3;']);
   rules.push(['.bm-modern-heading h1','font-size:30px;max-width:580px;']);
   rules.push(['.bm-modern-controls','margin:0;justify-content:flex-end;align-self:end;']);
   rules.push(['.bm-modern-note','max-width:340px;text-align:right;margin-top:10px;']);
   rules.push(['[data-testid="tile-view-wrapper"]','grid-template-columns:repeat(auto-fill,minmax(260px,1fr))!important;gap:24px!important;grid-template-rows:none!important;grid-auto-rows:auto!important;']);
   rules.push(['[data-testid="tile-card"], [data-testid="tile-wrapper"]','min-height:354px!important;']);
   rules.push(['[data-testid="thumbnail-wrapper"]','height:200px!important;min-height:200px!important;max-height:200px!important;flex:0 0 200px!important;background:#EDF0F2!important;']);
   rules.push(['[data-testid="thumbnailImage"]','height:178px!important;width:230px!important;']);
   rules.push(['[data-testid="tile-card"][data-bm-kind="rvt"]','border-left:3px solid #315B86!important;']);
   rules.push(['[data-testid="tile-card"][data-bm-kind="rfa"]','border-left:3px solid #96704F!important;']);
   rules.push(['[data-testid="tile-card"][data-bm-kind="rfa"] .bm-modern-type','color:#96704F!important;']);
  }
  rules.push(['[data-testid="tile-card"]','height:fit-content!important;min-height:0!important;contain:none!important;content-visibility:visible!important;box-sizing:border-box!important;align-self:start!important;']);
  rules.push(['[data-testid="tile-wrapper"]','position:static!important;inset:auto!important;contain:none!important;content-visibility:visible!important;box-sizing:border-box!important;height:auto!important;max-height:none!important;']);
  rules.push(['[data-testid="tile-view-wrapper"]','align-items:start!important;contain:none!important;grid-template-rows:none!important;grid-auto-rows:max-content!important;']);
  s.textContent=rules.map(function(r){return r[0].split(',').map(function(x){return pre+x.trim();}).join(',')+'{'+r[1]+'}';}).join('\n');
  s.textContent+='\n@media(max-width:1100px){'+pre+'.bm-modern-toolbar{display:block;}'+pre+'.bm-modern-controls{justify-content:flex-start;margin-top:12px;}}';
  add(s,document.head);sync();
  observer=new MutationObserver(function(){if(queued)return;queued=true;setTimeout(function(){queued=false;if(observer)observer.disconnect();sync();if(observer)observer.observe(document.body,{childList:true,subtree:true});},80);});observer.observe(document.body,{childList:true,subtree:true});return {applied:true,evidence:inspect()};
 }
 window.BIMaestroHomeConceptModern={apply:apply,inspect:inspect,restore:restore};
})();


