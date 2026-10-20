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
   var heading=el('div','bm-modern-heading');heading.appendChild(el('span','bm-modern-kicker',({'2023':'ATELIER ESSENTIEL','2024':'BUREAU DE PROJETS','2025':'BIBLIOTHÈQUE','2026':'INDEX'})[active]));
   heading.appendChild(el('h1','',active==='2026'?'Votre espace de travail':'Retrouver un fichier'));
   heading.appendChild(el('p','',active==='2026'?'Un accueil serein pour retrouver vos maquettes et vos familles.':'Des aperçus lisibles, les bons fichiers et leur emplacement.'));
   bar.appendChild(heading);var controls=el('div','bm-modern-controls');
   ['all','rvt','rfa'].forEach(function(key){var b=el('button','bm-modern-filter',key==='all'?'Tous':key==='rvt'?'Projets RVT':'Familles RFA');b.type='button';b.setAttribute('data-bm-filter',key);b.addEventListener('click',function(){filter=key;sync();});controls.appendChild(b);});
   bar.appendChild(controls);bar.appendChild(el('span','bm-modern-note','Les filtres portent sur les fichiers affichés. Recherche et tri Autodesk restent disponibles.'));
   inserted.push(bar);recent.insertBefore(bar,recent.firstChild);
  }
  if((active==='2026'||active==='2024')&&!document.getElementById('bm-modern-register-head')){
   var grid=document.querySelector('[data-testid="tile-view-wrapper"]');
   if(grid&&grid.parentNode){var columns=el('div','bm-modern-register-head');columns.id='bm-modern-register-head';columns.setAttribute('aria-hidden','true');['Nom du fichier','Modifié le','Type et emplacement'].forEach(function(label){columns.appendChild(el('span','',label));});inserted.push(columns);grid.parentNode.insertBefore(columns,grid);}
  }
  total={all:0,rvt:0,rfa:0,other:0};
  cards().forEach(function(c){var image=c.querySelector('[data-testid="thumbnailImage"]'),p=c.id||(image&&image.getAttribute('alt'))||'',m=p.match(/\.(rvt|rfa)(?:$|[?#])/i),kind=m?m[1].toLowerCase():'other';total.all++;total[kind]++;
   attr(c,'data-bm-kind',kind);attr(c,'data-bm-filtered',filter==='all'||kind===filter?'no':'yes');if(p)attr(c,'title',p);
   var info=c.querySelector('[data-testid="info-wrapper"]');if(info&&!info.querySelector('.bm-modern-meta')){var meta=el('div','bm-modern-meta');meta.appendChild(el('span','bm-modern-type',kind==='rvt'?'PROJET · RVT':kind==='rfa'?'FAMILLE · RFA':'FICHIER'));var parts=p.replace(/\\/g,'/').split('/');parts.pop();var folder=parts.pop()||'Emplacement indisponible';if(/[/\\\\]BIMaestro[/\\\\]Codex[/\\\\]Families[/\\\\]/i.test(p))folder='Familles enregistrées';else if(/[/\\\\]Samples[/\\\\]/i.test(p))folder='Exemples Revit';var path=el('span','bm-modern-path',folder);path.title=p;meta.appendChild(path);add(meta,info);}
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-bm-filter]'),function(b){var key=b.getAttribute('data-bm-filter');var text=(key==='all'?'Tous':key==='rvt'?'Projets RVT':'Familles RFA')+'  '+total[key];if(b.textContent!==text)b.textContent=text;var selected=key===filter?'true':'false';if(b.getAttribute('aria-pressed')!==selected)b.setAttribute('aria-pressed',selected);});
 }
 function inspect(){var measurements=cards().filter(function(c){return c.getAttribute('data-bm-filtered')!=='yes';}).map(function(c){var r=c.getBoundingClientRect?c.getBoundingClientRect():null,title=c.querySelector('.tile-title'),meta=c.querySelector('.bm-modern-meta'),t=title&&title.getBoundingClientRect?title.getBoundingClientRect():null,m=meta&&meta.getBoundingClientRect?meta.getBoundingClientRect():null;return {name:title?title.textContent:null,x:r?r.left:null,y:r?r.top:null,width:r?r.width:null,height:r?r.height:null,previewHeight:(c.querySelector('[data-testid=thumbnailImage]')||c).getBoundingClientRect().height,titleClipped:!!(title&&(title.scrollHeight>title.clientHeight+1||title.scrollWidth>title.clientWidth+1)),metadataOutsideCard:!!(r&&m&&(m.bottom>r.bottom+1||m.right>r.right+1)),titleMetadataOverlap:!!(t&&m&&t.bottom>m.top+1&&m.bottom>t.top+1&&t.right>m.left+1&&m.right>t.left+1)};});return {concept:active,filter:filter,counts:total,cards:cards().length,visibleCards:measurements.length,cardMetrics:measurements,horizontalOverflow:!!(document.documentElement.scrollWidth>document.documentElement.clientWidth+1),toolbar:!!document.getElementById('bm-modern-toolbar'),nativeOpen:!!document.querySelector('[data-testid="open-revit-file-button"]'),styles:!!document.getElementById(styleId),url:location.href};}
 function restore(){if(observer)observer.disconnect();observer=null;active=null;inserted.forEach(function(n){if(n.parentNode)n.parentNode.removeChild(n);});inserted=[];records.forEach(function(r){if(r.v===null)r.n.removeAttribute(r.k);else r.n.setAttribute(r.k,r.v);});records=[];if(window.BIMaestroHomeProbe)window.BIMaestroHomeProbe.restore();return inspect();}
 function apply(version){
  if(['2023','2024','2025','2026'].indexOf(version)<0)throw new Error('Unknown Home style');
  if(!document.querySelector('[data-testid="recent-container"]')||!document.querySelector('[data-testid="open-revit-file-button"]'))return {applied:false,reason:'Native UDA Home signatures absent'};
  if(!window.BIMaestroHomeProbe)return {applied:false,reason:'Load home-presets.js first'};
  restore();active=version;filter='all';window.BIMaestroHomeProbe.apply('compact');attr(root,'data-bm-modern',version);
  var s=el('style','');s.id=styleId;var brand=version==='2026'||version==='2024', bg=brand?'#F6F8FB':'#F7F5F0', ink=brand?'#0F172A':'#252D38', accent=brand?'#0F5132':'#315B86', muted=brand?'#64748B':'#667382', border=brand?'#E6EAF0':'#E4E1D9', pre='html[data-bm-modern] ';
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
   rules.push(['.bm-modern-register-head','display:grid!important;grid-template-columns:minmax(220px,1fr) 128px minmax(180px,.65fr)!important;gap:20px!important;padding:0 14px 8px 80px!important;color:'+muted+'!important;font:600 11px Segoe UI,sans-serif!important;flex:0 0 auto!important;box-sizing:border-box!important;']);
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
  /* V2: previews support identification; file information takes priority. */
  bg=brand?'#F6F8FB':'#F7F7F2';ink=brand?'#0F172A':'#28342B';accent=brand?'#0F5132':'#536944';border=brand?'#E6EAF0':'#DEE3D6';muted=brand?'#64748B':'#6D7667';
  rules.push(['body, #root, #root > div, [data-bimaestro-home-region="layout"], [data-bimaestro-home-region="main"], [data-bimaestro-home-region="main-layer"], [data-testid="recent-container"], [data-testid="recent-wrapper"], [data-testid="top-container"]','background:'+bg+'!important;color:'+ink+'!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"]','background:'+(brand?'#FFFFFF':'#EEF1E8')+'!important;border-right:1px solid '+border+'!important;width:216px!important;min-width:216px!important;flex-basis:216px!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"] div, [data-bimaestro-home-region="sidebar"] span, [data-bimaestro-home-region="sidebar"] p, [data-bimaestro-home-region="sidebar"] a, [data-bimaestro-home-region="sidebar"] nav','color:'+ink+'!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"] path','fill:'+ink+'!important;']);
  rules.push(['button[data-testid$="file-button"]','background:white!important;border-color:'+border+'!important;color:'+ink+'!important;border-radius:6px!important;']);
  rules.push(['button[data-testid="open-revit-file-button"]','background:'+accent+'!important;border-color:'+accent+'!important;color:white!important;']);
  rules.push(['button[data-testid="open-revit-file-button"] span, button[data-testid="open-revit-file-button"] div','color:white!important;']);
  rules.push(['[data-testid="recent-container"]','padding:20px 24px!important;']);
  rules.push(['.bm-modern-toolbar','display:flex!important;align-items:center!important;justify-content:space-between!important;gap:16px!important;padding:14px 18px!important;margin-bottom:14px!important;border:1px solid '+border+'!important;border-left:3px solid '+accent+'!important;border-top:1px solid '+border+'!important;border-radius:8px!important;box-shadow:none!important;']);
  rules.push(['.bm-modern-kicker','font-size:10px!important;letter-spacing:1px!important;color:'+accent+'!important;']);
  rules.push(['.bm-modern-heading h1','font-size:20px!important;letter-spacing:-.3px!important;margin:4px 0 0!important;color:'+ink+'!important;']);
  rules.push(['.bm-modern-heading p, .bm-modern-note','display:none!important;']);
  rules.push(['.bm-modern-controls','margin:0!important;gap:6px!important;']);
  rules.push(['.bm-modern-filter','padding:7px 12px!important;border-radius:6px!important;background:'+bg+'!important;border-color:'+border+'!important;color:'+ink+'!important;']);
  rules.push(['.bm-modern-filter[aria-pressed="true"]','background:'+accent+'!important;color:white!important;border-color:'+accent+'!important;']);
  rules.push(['[data-testid="tile-view-wrapper"]','grid-template-columns:'+(brand?'minmax(0,1fr)':'repeat(auto-fill,minmax(370px,1fr))')+'!important;gap:'+(brand?'6':'12')+'px!important;']);
  rules.push(['[data-testid="tile-card"]','border:1px solid '+border+'!important;border-radius:'+(brand?'6':'10')+'px!important;border-left:3px solid '+accent+'!important;box-shadow:none!important;min-width:0!important;width:100%!important;']);
  rules.push(['[data-testid="tile-card"]:hover','border-color:'+accent+'!important;box-shadow:0 2px 8px rgba(15,51,32,.08)!important;']);
  rules.push(['[data-testid="tile-card"][data-bm-kind="rfa"]','border-left-color:'+(brand?'#5E7F9A':'#A28551')+'!important;']);
  rules.push(['[data-testid="tile-wrapper"]','display:flex!important;flex-direction:row!important;align-items:stretch!important;min-height:0!important;width:100%!important;']);
  rules.push(['[data-testid="thumbnail-wrapper"]','position:relative!important;width:'+(brand?'64':'96')+'px!important;min-width:'+(brand?'64':'96')+'px!important;height:'+(brand?'66':'110')+'px!important;min-height:0!important;max-height:none!important;flex:0 0 '+(brand?'64':'96')+'px!important;align-self:center!important;border-bottom:0!important;border-right:1px solid '+border+'!important;background:'+(brand?'#F9FAFB':'#F1F3ED')+'!important;']);
  rules.push(['[data-testid="thumbnailImage"]','width:'+(brand?'52':'80')+'px!important;height:'+(brand?'52':'80')+'px!important;max-width:100%!important;object-fit:contain!important;']);
  rules.push(['[data-testid="thumbnail-wrapper"]','display:flex!important;align-items:center!important;justify-content:center!important;overflow:hidden!important;']);
  rules.push(['[data-testid="thumbnailImage"]','position:static!important;inset:auto!important;top:auto!important;left:auto!important;transform:none!important;margin:0!important;']);
  rules.push(['[data-testid="info-wrapper"]','display:flex!important;flex-direction:column!important;justify-content:center!important;flex:1 1 0!important;min-width:0!important;min-height:0!important;padding:'+(brand?'8px 14px':'12px 14px')+'!important;gap:5px!important;overflow:hidden!important;']);
  rules.push(['.tile-title','display:block!important;white-space:normal!important;overflow:visible!important;text-overflow:clip!important;-webkit-line-clamp:unset!important;overflow-wrap:anywhere!important;font-size:13px!important;line-height:1.35!important;color:'+ink+'!important;']);
  rules.push(['[data-testid="info-wrapper"] > div:first-child, [data-testid="info-wrapper"] .portalContainer, .tile-title','width:100%!important;max-width:none!important;min-width:0!important;box-sizing:border-box!important;']);
  rules.push(['.bm-modern-meta','display:flex!important;flex-direction:row!important;align-items:center!important;flex-wrap:wrap!important;gap:6px 12px!important;border-top:0!important;padding-top:0!important;margin-top:0!important;min-width:0!important;']);
  rules.push(['[data-testid="info-wrapper"] .bm-modern-type','font-size:9px!important;letter-spacing:.7px!important;color:'+accent+'!important;flex:0 0 auto!important;']);
  rules.push(['[data-testid="info-wrapper"] .bm-modern-path','font-size:11px!important;color:'+muted+'!important;min-width:0!important;max-width:100%!important;']);
  if(brand){
   rules.push(['[data-testid="info-wrapper"]','display:grid!important;grid-template-columns:minmax(220px,1fr) 128px minmax(180px,.65fr)!important;align-items:center!important;column-gap:20px!important;']);
   rules.push(['[data-testid="info-wrapper"] > .portalContainer','grid-column:2!important;grid-row:1!important;']);
   rules.push(['[data-testid="info-wrapper"] .bm-modern-meta','grid-column:3!important;grid-row:1!important;']);
  }
  /* V3: restrained green library and a denser index, preserving every native card. */
  accent='#0F5132';border='#DDE5DF';
  rules.push(['.bm-modern-toolbar','background:transparent!important;border:0!important;border-bottom:1px solid '+border+'!important;border-radius:0!important;padding:2px 0 17px!important;margin-bottom:18px!important;']);
  rules.push(['.bm-modern-kicker','color:'+accent+'!important;letter-spacing:1px!important;']);
  rules.push(['.bm-modern-heading h1','font-size:23px!important;']);
  rules.push(['.bm-modern-filter','border-radius:3px!important;background:white!important;border-color:'+border+'!important;padding:8px 13px!important;']);
  rules.push(['.bm-modern-filter[aria-pressed="true"]','background:'+accent+'!important;color:white!important;border-color:'+accent+'!important;']);
  rules.push(['button[data-testid="open-revit-file-button"]','background:'+accent+'!important;border-color:'+accent+'!important;']);
  rules.push(['[data-testid="tile-view-wrapper"]','grid-template-columns:'+(brand?'minmax(0,1fr)':'repeat(auto-fill,minmax(430px,1fr))')+'!important;gap:'+(brand?'2':'14')+'px!important;']);
  rules.push(['[data-testid="tile-card"]','border:1px solid '+border+'!important;border-radius:'+(brand?'3':'7')+'px!important;background:white!important;']);
  rules.push(['[data-testid="tile-card"][data-bm-kind="rvt"]','border-left:'+(brand?'1':'3')+'px solid '+(brand?border:accent)+'!important;']);
  rules.push(['[data-testid="tile-card"][data-bm-kind="rfa"]','border-left:'+(brand?'1':'3')+'px solid '+(brand?border:'#71977F')+'!important;']);
  rules.push(['[data-testid="thumbnail-wrapper"]','width:'+(brand?'60':'92')+'px!important;min-width:'+(brand?'60':'92')+'px!important;flex-basis:'+(brand?'60':'92')+'px!important;height:'+(brand?'64':'108')+'px!important;background:#F3F6F2!important;border-right:0!important;']);
  rules.push(['[data-testid="thumbnailImage"]','width:'+(brand?'44':'72')+'px!important;height:'+(brand?'44':'72')+'px!important;']);
  rules.push(['[data-testid="info-wrapper"]','padding:'+(brand?'8px 14px':'13px 16px')+'!important;background:transparent!important;']);
  rules.push(['.tile-title','font-size:'+(brand?'13':'14')+'px!important;font-weight:600!important;line-height:1.4!important;']);
  rules.push(['[data-testid="info-wrapper"] .bm-modern-type','color:'+accent+'!important;font-size:10px!important;letter-spacing:.35px!important;']);
  if(brand){
   rules.push(['[data-testid="tile-card"]:nth-child(even)','background:#F1F5F2!important;']);
   rules.push(['[data-testid="tile-wrapper"]','background:transparent!important;']);
   rules.push(['.bm-modern-register-head','padding-left:75px!important;']);
  }
  /* Final palette and layout invariants shared by all four user-selected concepts. */
  var dark=window.__bmHomeDark===true;
  var palette=dark?{bg:'#18211e',side:'#1e2b25',card:'#25352d',fg:'#edf5ef',muted:'#afc4b6',accent:'#7ed5a4',line:'#40584a',preview:'#1e2b25',stripe:'#203027'}:{bg:'#f5f8f5',side:'#edf3ee',card:'#ffffff',fg:'#203c30',muted:'#62746a',accent:'#0f5132',line:'#d8e3dc',preview:'#f0f5f1',stripe:'#f1f5f2'};
  attr(root,'data-bm-home-theme',dark?'dark':'light');
  rules.push(['body, #root, #root > div, [data-bimaestro-home-region="layout"], [data-bimaestro-home-region="main"], [data-bimaestro-home-region="main-layer"], [data-testid="recent-container"], [data-testid="recent-wrapper"], [data-testid="top-container"]','background:var(--bm-home-bg)!important;color:var(--bm-home-fg)!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"]','background:var(--bm-home-side)!important;border-color:var(--bm-home-line)!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"] div, [data-bimaestro-home-region="sidebar"] span, [data-bimaestro-home-region="sidebar"] p, [data-bimaestro-home-region="sidebar"] a, [data-testid="top-container"] div, [data-testid="top-container"] p','color:var(--bm-home-fg)!important;']);
  rules.push(['[data-bimaestro-home-region="sidebar"] svg[data-testid^="revit-"] path','fill:var(--bm-home-fg)!important;']);
  rules.push(['[data-testid="tile-card"], [data-testid="tile-wrapper"], [data-testid="info-wrapper"], button[data-testid$="file-button"], .bm-modern-filter, [data-testid$="id_searchbar_input"]','background:var(--bm-home-card)!important;color:var(--bm-home-fg)!important;border-color:var(--bm-home-line)!important;']);
  rules.push(['.tile-title, [data-testid="info-wrapper"] span','color:var(--bm-home-fg)!important;']);
  rules.push(['[data-testid="info-wrapper"] > .portalContainer span, [data-testid="info-wrapper"] .bm-modern-path, .bm-modern-register-head','color:var(--bm-home-muted)!important;']);
  rules.push(['.bm-modern-kicker, [data-testid="info-wrapper"] .bm-modern-type','color:var(--bm-home-accent)!important;']);
  rules.push(['[data-testid="thumbnail-wrapper"]','background:var(--bm-home-preview)!important;align-self:stretch!important;height:auto!important;min-height:'+(brand?'64':'112')+'px!important;']);
  rules.push(['[data-testid="tile-view-wrapper"]','align-items:stretch!important;grid-auto-rows:'+(brand?'minmax(68px,auto)':'minmax(140px,auto)')+'!important;']);
  rules.push(['[data-testid="tile-card"]','height:100%!important;align-self:stretch!important;min-height:'+(brand?'68':'140')+'px!important;']);
  rules.push(['[data-testid="tile-wrapper"]','height:100%!important;min-height:inherit!important;']);
  rules.push(['button[data-testid="open-revit-file-button"], .bm-modern-filter[aria-pressed="true"]','background:var(--bm-home-accent)!important;border-color:var(--bm-home-accent)!important;color:'+(dark?'#112b1e':'#ffffff')+'!important;']);
  rules.push(['button[data-testid="open-revit-file-button"] span, button[data-testid="open-revit-file-button"] div','color:'+(dark?'#112b1e':'#ffffff')+'!important;']);
  rules.push(['[data-testid$="id_searchbar_input"]::placeholder','color:var(--bm-home-muted)!important;opacity:1!important;']);
  rules.push(['.bm-modern-heading h1, .bm-modern-heading p','color:var(--bm-home-fg)!important;']);
  rules.push(['.bm-modern-toolbar, .bm-modern-filter, [data-testid="tile-card"]:hover','border-color:var(--bm-home-line)!important;']);
  if(brand)rules.push(['[data-testid="tile-card"]:nth-child(even), [data-testid="tile-card"]:nth-child(even) [data-testid="tile-wrapper"], [data-testid="tile-card"]:nth-child(even) [data-testid="info-wrapper"]','background:var(--bm-home-stripe)!important;']);
  if(version==='2023'){
    rules.push(['[data-testid="tile-view-wrapper"]','grid-template-columns:repeat(auto-fill,minmax(320px,1fr))!important;gap:18px!important;']);
    rules.push(['.bm-modern-heading h1','font-size:30px!important;']);
    rules.push(['[data-testid="thumbnail-wrapper"]','width:100px!important;min-width:100px!important;flex-basis:100px!important;']);
    rules.push(['[data-testid="thumbnailImage"]','width:88px!important;height:88px!important;']);
  }
  if(version==='2024'){
    rules.push(['[data-testid="tile-card"]','border-radius:6px!important;min-height:84px!important;']);
    rules.push(['[data-testid="tile-view-wrapper"]','gap:8px!important;']);
    rules.push(['[data-testid="thumbnail-wrapper"]','min-height:80px!important;']);
  }
  s.textContent=rules.map(function(r){return r[0].split(',').map(function(x){return pre+x.trim();}).join(',')+'{'+r[1]+'}';}).join('\n');
  s.textContent+='\n@media(max-width:1100px){'+pre+'.bm-modern-toolbar{display:block;}'+pre+'.bm-modern-controls{justify-content:flex-start;margin-top:12px;}}';
  s.textContent+='\n@media(max-width:1200px){html[data-bm-modern="2026"] [data-testid="info-wrapper"]{display:flex!important;flex-direction:column!important;align-items:flex-start!important;gap:3px!important;}html[data-bm-modern="2026"] .bm-modern-register-head{display:none!important;}}';
  s.textContent+='\n@media(max-width:700px){html[data-bm-modern] [data-testid="tile-view-wrapper"]{grid-template-columns:minmax(0,1fr)!important;}html[data-bm-modern] .bm-modern-toolbar{flex-wrap:wrap!important;}}';
  s.textContent+='\nhtml[data-bm-modern]{'+Object.keys(palette).map(function(k){return '--bm-home-'+k+':'+palette[k]+';';}).join('')+'}';
  s.textContent+='\n@media(max-width:1200px){html[data-bm-modern="2024"] [data-testid="info-wrapper"]{display:flex!important;flex-direction:column!important;align-items:flex-start!important;}html[data-bm-modern="2024"] .bm-modern-register-head{display:none!important;}}';
  add(s,document.head);sync();
  observer=new MutationObserver(function(){if(queued)return;queued=true;setTimeout(function(){queued=false;if(observer)observer.disconnect();sync();if(observer)observer.observe(document.body,{childList:true,subtree:true});},80);});observer.observe(document.body,{childList:true,subtree:true});return {applied:true,evidence:inspect()};
 }
 window.BIMaestroHomeConceptModern={apply:apply,inspect:inspect,restore:restore};
})();




