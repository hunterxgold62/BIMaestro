/* Native Revit Home concepts. In-memory, reversible, no model operations. */
(function () {
  'use strict';
  if (!/\/ModelBrowser\/resource\/index\.html(?:[?#]|$)/i.test(location.href.replace(/\\/g, '/'))) return {applied:false,reason:'Not legacy Home'};
  if (window.BIMaestroHomeConceptLegacy) return window.BIMaestroHomeConceptLegacy.inspect();
  var key='data-bm-legacy', active=null, observer=null, timer=null, records=[], filter='all', query='', hideSamples=false;
  var themes={
    '2023':{name:'Atelier essentiel',subtitle:'Les bons fichiers, sans détour.',bg:'#f5f3ee',side:'#ebe8e0',card:'#fffefa',fg:'#283b36',muted:'#63716a',accent:'#0F5132',line:'#d9ded4',family:'#97643e'},
    '2024':{name:'Bureau de projets',subtitle:'Un registre lisible de vos projets et familles.',bg:'#f4f7f5',side:'#e7eee9',card:'#ffffff',fg:'#203c30',muted:'#62746a',accent:'#0F5132',line:'#d8e3dc',family:'#886343'}
  };
  themes['2025']=Object.assign({},themes['2023'],{name:'Bibliothèque',subtitle:'Un aperçu compact et des noms lisibles.',bg:'#f5f8f5',side:'#edf3ee'});
  themes['2026']=Object.assign({},themes['2024'],{name:'Index',subtitle:'Un index dense pour parcourir vos fichiers.'});
  function add(tag,cls,text,parent){var n=document.createElement(tag);n.className=cls;if(text!==undefined)n.textContent=text;if(parent)parent.appendChild(n);return n;}
  function css(t){var p='html['+key+'] ';function r(s,b){return p+s+'{'+b+'}';}
    return r('body,.App,.SplitLayout','background:'+t.bg+'!important;color:'+t.fg+'!important;')+
      r('.SplitLayout__secondary','width:232px!important;min-width:232px!important;max-width:232px!important;flex:0 0 232px!important;background:'+t.side+'!important;')+
      r('.SplitLayout__primary','min-width:0!important;flex:1!important;width:calc(100% - 232px)!important;background:'+t.bg+'!important;')+
      r('.MbSidePanel','width:232px!important;background:'+t.side+'!important;color:'+t.fg+'!important;')+
      r('.MbSidePanel__content','background:transparent!important;')+
      r('.MbSidePanel .ButtonGroup__title,.MbSidePanel .IconPanel,.MbSidePanel .FooterLink,.MbSidePanel span,.MbSidePanel p,.MbSidePanel label,.MbSidePanel div','color:'+t.fg+'!important;')+
      r('.MbSidePanel .IconPanel','background:transparent!important;')+
      r('.MbSidePanel .IconPanel__checked','background:'+t.card+'!important;border-left:3px solid '+t.accent+'!important;')+
      r('.MbSidePanel button','background:'+t.card+'!important;color:'+t.fg+'!important;border:1px solid '+t.line+'!important;border-radius:7px!important;')+
      r('.MbSidePanel button:hover','border-color:'+t.accent+'!important;')+
      r('.App__sidebar-button-group','max-width:calc(100% - 32px)!important;')+
      r('.RecentFilesView','box-sizing:border-box!important;width:100%!important;background:'+t.bg+'!important;color:'+t.fg+'!important;padding:28px 32px!important;')+
      r('.RecentFilesView__banner','display:none!important;')+
      r('.RecentFilesView__data','background:transparent!important;border:0!important;')+
      r('.RecentFilesView__group-title','background:transparent!important;color:'+t.fg+'!important;border-bottom:1px solid '+t.line+'!important;padding:18px 0 12px!important;font-size:13px!important;font-weight:700!important;letter-spacing:1.3px!important;')+
      r('.RecentFilesView__thumbnails','grid-template-columns:repeat(auto-fill,minmax('+(active==='2023'?'290':'210')+'px,1fr))!important;gap:16px!important;padding:18px 0 28px!important;align-items:stretch!important;justify-items:stretch!important;')+
      r('.RecentFilesView__thumbnails > div','min-width:0!important;')+
      r('.Thumbnail','box-sizing:border-box!important;width:100%!important;color:'+t.fg+'!important;background:'+t.card+'!important;border:1px solid '+t.line+'!important;border-radius:10px!important;overflow:hidden!important;position:relative!important;padding:12px!important;height:100%!important;box-shadow:0 3px 12px #00000008!important;')+
      r('.Thumbnail:hover','border-color:'+t.accent+'!important;box-shadow:0 5px 20px #00000018!important;')+
      r('.Thumbnail__container','height:'+(active==='2023'?'104':'142')+'px!important;background:'+(active==='2023'?'#f0f2ec':'#eef2f5')+'!important;border:none!important;border-radius:6px!important;margin:0 0 12px!important;overflow:hidden!important;')+
      r('.Thumbnail__bootstrap','box-sizing:border-box!important;width:100%!important;height:100%!important;margin:0!important;background:transparent!important;display:flex!important;align-items:center!important;justify-content:center!important;')+
      r('.Thumbnail__bootstrap img','box-sizing:border-box!important;border:0!important;width:100%!important;height:100%!important;max-height:100%!important;max-width:100%!important;object-fit:contain!important;background:transparent!important;')+
      r('.Thumbnail__caption','white-space:normal!important;overflow:visible!important;text-overflow:clip!important;overflow-wrap:anywhere!important;max-height:none!important;height:auto!important;line-height:1.4!important;font-size:14px!important;font-weight:600!important;padding-top:0!important;margin:7px 0!important;color:'+t.fg+'!important;')+
      r('.bm-legacy-meta','font-size:12px;line-height:1.55;color:'+t.muted+';overflow-wrap:anywhere;')+
      r('.bm-legacy-type','font-size:10px;font-weight:700;letter-spacing:1px;color:'+t.accent+';display:block;margin:2px 0 5px;')+
      r('[data-bm-kind="family"] .bm-legacy-type','color:'+t.family+';')+
      r('.bm-legacy-source','margin-top:7px;padding-top:7px;border-top:1px solid '+t.line+';font-size:12px;line-height:1.45;color:'+t.muted+';overflow-wrap:anywhere;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;')+
      r('.bm-legacy-hero','display:flex;align-items:flex-end;justify-content:space-between;gap:18px;margin:0 0 20px;')+
      r('.bm-legacy-kicker','font-size:10px;letter-spacing:2px;font-weight:700;color:'+t.accent+';margin:0 0 7px;')+
      r('.bm-legacy-heading','font-size:30px;line-height:1.15;font-weight:600;letter-spacing:-1px;color:'+t.fg+';margin:0 0 8px;')+
      r('.bm-legacy-subtitle','font-size:13px;color:'+t.muted+';margin:0;')+
      r('.bm-legacy-stats','display:flex;gap:24px;flex-shrink:0;')+
      r('.bm-legacy-stat strong','display:block;font-size:29px;font-weight:500;color:'+t.fg+';line-height:1.2;')+
      r('.bm-legacy-stat span','font-size:11px;color:'+t.muted+';')+
      r('.bm-legacy-toolbar','display:flex;align-items:center;gap:8px;flex-wrap:wrap;background:'+t.card+';border:1px solid '+t.line+';padding:12px;border-radius:10px;margin-bottom:6px;')+
      r('.bm-legacy-toolbar button','font:600 12px Segoe UI,sans-serif;cursor:pointer;border:1px solid '+t.line+';background:transparent;color:'+t.muted+';padding:8px 13px;border-radius:6px;')+
      r('.bm-legacy-toolbar button[aria-pressed="true"]','background:'+t.accent+';color:'+(active==='2023'?'#ffffff':'#102333')+';border-color:'+t.accent+';')+
      r('.bm-legacy-search','box-sizing:border-box;flex:1;min-width:180px;max-width:420px;padding:9px 12px;border-radius:6px;border:1px solid '+t.line+';background:'+t.bg+';color:'+t.fg+';font:13px Segoe UI,sans-serif;outline:none;')+
      r('.bm-legacy-search:focus','border-color:'+t.accent+';')+
      r('.bm-legacy-search::placeholder','color:'+t.muted+';opacity:1;')+
      r('.bm-legacy-status','font-size:11px;color:'+t.muted+';margin:10px 0 0;')+
      r('[data-bm-hidden="true"]','display:none!important;')+
      r('.bm-legacy-empty','padding:24px;color:'+t.muted+';border:1px dashed '+t.line+';border-radius:8px;margin-top:20px;')+
      (active==='2023'?r('.Thumbnail','display:grid!important;grid-template-columns:104px minmax(0,1fr)!important;column-gap:14px!important;align-content:start!important;')+r('.Thumbnail__container','grid-column:1;grid-row:1 / 5;margin:0!important;')+r('.Thumbnail__caption,.bm-legacy-type,.bm-legacy-meta,.bm-legacy-source','grid-column:2;'):'')+
      '@media(max-width:1050px){'+r('.bm-legacy-hero','align-items:flex-start;flex-direction:column;')+r('.RecentFilesView','padding:22px!important;')+'}';
  }
  function compactCss(t){
    var p='html['+key+'] ';function r(s,b){return p+s+'{'+b+'}';}
    var preview=active==='2023'?76:92;
    return r('.RecentFilesView','padding:24px 28px!important;')+
      r('.bm-legacy-heading','font-size:25px;letter-spacing:-.6px;margin-bottom:5px;')+
      r('.bm-legacy-kicker','font-size:10px;letter-spacing:1.5px;margin-bottom:6px;')+
      r('.bm-legacy-hero','align-items:center;margin-bottom:18px;')+
      r('.bm-legacy-stats','gap:8px;')+
      r('.bm-legacy-stat','display:flex;align-items:center;gap:6px;padding:7px 11px;border:1px solid '+t.line+';background:'+t.card+';border-radius:20px;')+
      r('.bm-legacy-stat strong','font-size:14px;font-weight:700;')+
      r('.bm-legacy-stat span','font-size:11px;')+
      r('.bm-legacy-toolbar','padding:10px;background:transparent;border:0;border-bottom:1px solid '+t.line+';border-radius:0;')+
      r('.bm-legacy-toolbar button[aria-pressed="true"]','color:#ffffff!important;')+
      r('.bm-legacy-search','background:#ffffff;max-width:420px;')+
      r('.RecentFilesView__thumbnails','grid-template-columns:repeat(auto-fill,minmax('+(active==='2023'?'305':'440')+'px,1fr))!important;gap:'+(active==='2023'?'12':'10')+'px!important;padding:12px 0 20px!important;')+
      r('.Thumbnail','display:grid!important;grid-template-columns:'+preview+'px minmax(0,1fr)!important;grid-template-rows:auto auto auto auto!important;column-gap:12px!important;align-content:start!important;padding:12px!important;min-height:'+(active==='2023'?'164':'146')+'px!important;border-radius:'+(active==='2023'?'9':'5')+'px!important;box-shadow:none!important;')+
      r('.Thumbnail__container','height:'+preview+'px!important;grid-column:1!important;grid-row:1 / 5!important;margin:0!important;background:#f3f6f4!important;border-radius:5px!important;')+
      r('.Thumbnail__caption,.bm-legacy-type,.bm-legacy-meta,.bm-legacy-source','grid-column:2!important;')+
      r('.Thumbnail__caption','font-size:13px!important;line-height:1.35!important;margin:2px 0 5px!important;')+
      r('.bm-legacy-type','font-size:9px;letter-spacing:.7px;margin:0 0 3px;')+
      r('.bm-legacy-meta','font-size:11px;line-height:1.45;')+
      r('.bm-legacy-source','font-size:10px;margin-top:5px;padding-top:5px;line-height:1.4;')+
      r('.RecentFilesView__group-title','font-size:11px!important;letter-spacing:.8px!important;padding:14px 0 8px!important;')+
      (active==='2024'?r('.RecentFilesView__thumbnails','grid-template-columns:repeat(2,minmax(0,1fr))!important;')+r('.Thumbnail','border-left:3px solid '+t.accent+'!important;')+r('.Thumbnail[data-bm-kind="family"]','border-left-color:'+t.family+'!important;'):'')+
      '@media(max-width:900px){'+r('.RecentFilesView__thumbnails','grid-template-columns:1fr!important;')+'}';
  }
  function refinedCss(t){
    var p='html['+key+'] ';function r(s,b){return p+s+'{'+b+'}';}
    var register=active==='2024'||active==='2026',preview=register?56:88;
    return r('.RecentFilesView','padding:30px 34px!important;')+
      r('.bm-legacy-hero','align-items:center;margin-bottom:24px;')+
      r('.bm-legacy-heading','font-size:30px;letter-spacing:-.8px;margin-bottom:8px;')+
      r('.bm-legacy-stats','gap:30px;')+
      r('.bm-legacy-stat','display:block;border:0;background:transparent;padding:0;border-radius:0;min-width:90px;')+
      r('.bm-legacy-stat strong','font-size:34px;line-height:1.1;font-weight:500;color:'+t.accent+';')+
      r('.bm-legacy-stat span','font-size:11px;display:block;margin-top:6px;')+
      r('.bm-legacy-toolbar','padding:12px;background:'+t.card+';border:1px solid '+t.line+';border-radius:6px;gap:8px;')+
      r('.bm-legacy-toolbar button','border-radius:4px;padding:9px 13px;')+
      r('.bm-legacy-search','background:#f7f9f7;max-width:460px;padding:10px 12px;')+
      r('.RecentFilesView__thumbnails','grid-template-columns:'+(register?'1fr':'repeat(auto-fill,minmax(320px,1fr))')+'!important;gap:'+(register?'7':'14')+'px!important;padding:14px 0 24px!important;')+
      r('.Thumbnail','grid-template-columns:'+preview+'px minmax(0,1fr)'+(register?' 155px 200px':'')+'!important;grid-template-rows:'+(register?'auto auto':'auto auto auto auto')+'!important;column-gap:16px!important;align-content:center!important;padding:'+(register?'12px 18px':'16px')+'!important;min-height:'+(register?'82px':'158px')+'!important;border-radius:6px!important;box-shadow:none!important;')+
      r('.Thumbnail__container','height:'+preview+'px!important;grid-row:1 / 5!important;background:#f1f5f1!important;border-radius:4px!important;')+
      r('.Thumbnail__caption','font-size:14px!important;line-height:1.4!important;margin:3px 0 5px!important;')+
      r('.bm-legacy-type','font-size:9px;letter-spacing:1px;margin:0 0 3px;')+
      r('.bm-legacy-meta','font-size:11px;line-height:1.6;')+
      r('.bm-legacy-source','font-size:11px;margin-top:7px;padding-top:7px;line-height:1.5;')+
      (register?r('.bm-legacy-type','grid-row:1!important;align-self:end;')+r('.Thumbnail__caption','grid-row:2!important;align-self:start;')+r('.bm-legacy-meta','grid-column:3!important;grid-row:1 / 3!important;align-self:center;')+r('.bm-legacy-source','grid-column:4!important;grid-row:1 / 3!important;align-self:center;border-top:0;border-left:1px solid '+t.line+';padding:0 0 0 16px;margin:0;')+r('.Thumbnail:hover','background:#f4f9f5!important;'):'')+
      '@media(max-width:1000px){'+r('.Thumbnail','grid-template-columns:'+preview+'px minmax(0,1fr)!important;')+r('.bm-legacy-meta,.bm-legacy-source','grid-column:2!important;grid-row:auto!important;border-left:0;padding:0;')+'}';
  }
  function productionCss(t){
    var p='html['+key+'] ';function r(s,b){return s.split(',').map(function(x){return p+x.trim();}).join(',')+'{'+b+'}';}
    var register=active==='2024'||active==='2026';
    return r('.MbSidePanel .IconPanel','box-sizing:border-box!important;margin:0!important;width:100%!important;min-height:40px!important;padding:10px 24px!important;border-left:3px solid transparent!important;display:flex!important;align-items:center!important;')+
      r('.MbSidePanel .IconPanel__checked','border-left-color:'+t.accent+'!important;background:'+t.card+'!important;font-weight:600!important;')+
      r('.bm-legacy-search,.Thumbnail__container','background:'+t.bg+'!important;')+
      r('.bm-legacy-toolbar button[aria-pressed="true"]','background:'+t.accent+'!important;color:'+(window.__bmHomeDark===true?'#112b1e':'#ffffff')+'!important;')+
      r('.Thumbnail:hover','background:'+t.card+'!important;')+
      r('.Thumbnail','display:grid!important;align-self:stretch!important;height:100%!important;')+
      (active==='2025'?r('.Thumbnail','min-height:142px!important;')+r('.RecentFilesView__thumbnails','grid-template-columns:repeat(auto-fill,minmax(360px,1fr))!important;'):active==='2026'?r('.Thumbnail','min-height:70px!important;padding:8px 14px!important;'):'')+
      '@media(max-width:1100px){'+r('.bm-legacy-hero','flex-wrap:wrap;')+r('.Thumbnail','grid-template-columns:88px minmax(0,1fr)!important;')+r('.bm-legacy-meta,.bm-legacy-source','grid-column:2!important;grid-row:auto!important;border-left:0!important;padding:0!important;')+'}';
  }
  function captureCards(){
    document.querySelectorAll('.RecentFilesView__thumbnails-thumbnail').forEach(function(c){
      if(records.some(function(x){return x.card===c;}))return;
      var path=c.getAttribute('data-for')||'',family=/\.rfa$/i.test(path),parts=path.split(/[\\/]/),folder=parts.slice(0,-1),source=folder.slice(-2).join(' / ');
      if(/[\\/]BIMaestro[\\/]Codex[\\/]Families[\\/]/i.test(path))source='Familles enregistrées';
      else if(/[\\/]Samples[\\/]/i.test(path))source=(folder[folder.length-2]||'Revit')+' / Exemples';
      else if(source.length>46)source=source.slice(0,43)+'…';
      var tip=document.getElementById(path), vals=tip?Array.prototype.map.call(tip.querySelectorAll('.RecentFileInfo__content'),function(x){return x.textContent.trim();}):[];
      var oldTitle=c.getAttribute('title');c.title=path;c.setAttribute('data-bm-kind',family?'family':'project');
      var label=add('span','bm-legacy-type',family?'FAMILLE · RFA':'PROJET · RVT');c.insertBefore(label,c.querySelector('.Thumbnail__caption'));
      var meta=add('div','bm-legacy-meta',vals[3]?'Taille · '+vals[3]:'',c);
      if(vals[2])add('div','', 'Modifié · '+vals[2],meta);
      var src=add('div','bm-legacy-source',source||'Chemin non communiqué',c);src.title=path;
      records.push({card:c,wrapper:c.parentElement,path:path.toLowerCase(),family:family,sample:/[\\/]Samples[\\/]/i.test(path),title:oldTitle,nodes:[label,meta,src]});
    });
  }
  function refresh(){
    if(!active)return;observer.disconnect();
    records=records.filter(function(x){return document.documentElement.contains(x.card);});
    captureCards();
    var totals=[records.filter(function(x){return !x.family;}).length,records.filter(function(x){return x.family;}).length];
    document.querySelectorAll('.bm-legacy-stat strong').forEach(function(n,i){n.textContent=String(totals[i]||0);});
    var shown=0;records.forEach(function(x){var yes=(filter==='all'||(filter==='family')===x.family)&&(!hideSamples||!x.sample)&&(!query||(x.path+' '+x.card.querySelector('.Thumbnail__caption').textContent.toLowerCase()).indexOf(query)!==-1);x.wrapper.setAttribute('data-bm-hidden',String(!yes));if(yes)shown++;});
    document.querySelectorAll('.RecentFilesView__thumbnails').forEach(function(grid){var visible=Array.prototype.some.call(grid.children,function(n){return n.getAttribute('data-bm-hidden')!=='true';});grid.parentElement.setAttribute('data-bm-hidden',String(!visible));});
    var status=document.querySelector('.bm-legacy-status');if(status)status.textContent=shown+' fichier'+(shown>1?'s':'')+' affiché'+(shown>1?'s':'')+' · recherche par nom et dossier';
    var empty=document.querySelector('.bm-legacy-empty');if(empty)empty.style.display=shown?'none':'block';
    document.querySelectorAll('[data-bm-filter]').forEach(function(b){b.setAttribute('aria-pressed',String(filter===b.getAttribute('data-bm-filter')));});
    observer.observe(document.querySelector('.RecentFilesView'),{childList:true,subtree:true});
  }
  function inspect(){return {concept:active,cards:records.length,ready:!!active&&records.length>0,pending:!!active&&records.length===0,visible:records.filter(function(x){return x.wrapper.getAttribute('data-bm-hidden')!=='true';}).length,projects:records.filter(function(x){return !x.family;}).length,families:records.filter(function(x){return x.family;}).length,query:query,filter:filter,hideSamples:hideSamples,style:!!document.getElementById('bm-legacy-style'),overflow:document.documentElement.scrollWidth>window.innerWidth+2,geometry:records.slice(0,3).map(function(x){var c=x.card.getBoundingClientRect(),n=x.card.querySelector(".Thumbnail__container"),i=n?n.getBoundingClientRect():{};return {width:c.width,height:c.height,previewWidth:i.width,previewHeight:i.height};})};}
  function restore(){if(observer)observer.disconnect();clearTimeout(timer);records.forEach(function(x){x.nodes.forEach(function(n){n.remove();});x.card.removeAttribute('data-bm-kind');if(x.title===null)x.card.removeAttribute('title');else x.card.setAttribute('title',x.title);});document.querySelectorAll('[data-bm-hidden]').forEach(function(n){n.removeAttribute('data-bm-hidden');});document.querySelectorAll('.bm-legacy-owned').forEach(function(n){n.remove();});var s=document.getElementById('bm-legacy-style');if(s)s.remove();document.documentElement.removeAttribute(key);records=[];active=null;return inspect();}
  function apply(version){
    if(!themes[version]||!document.querySelector('.RecentFilesView')||!document.querySelector('.MbSidePanel'))return {applied:false,reason:'Legacy Home absent'};
    restore();if(window.BIMaestroHomeProbe)window.BIMaestroHomeProbe.restore();active=String(version);filter='all';query='';hideSamples=false;var t=Object.assign({},themes[active]);if(window.__bmHomeDark===true)Object.assign(t,{bg:'#18211e',side:'#1e2b25',card:'#25352d',fg:'#edf5ef',muted:'#afc4b6',accent:'#7ed5a4',line:'#40584a',family:'#dec298'});var view=document.querySelector('.RecentFilesView');
    document.documentElement.setAttribute(key,active);var style=add('style','','',document.head);style.id='bm-legacy-style';var colorKeys=['bg','side','card','fg','muted','accent','line','family'],cssTheme=Object.assign({},t),vars='html['+key+']{';colorKeys.forEach(function(k){vars+='--bm-home-'+k+':'+t[k]+';';cssTheme[k]='var(--bm-home-'+k+')';});style.textContent=vars+'}'+css(cssTheme)+compactCss(cssTheme)+refinedCss(cssTheme)+productionCss(cssTheme);captureCards();
    var hero=add('section','bm-legacy-hero bm-legacy-owned'),intro=add('div','',undefined,hero);add('p','bm-legacy-kicker',t.name.toUpperCase(),intro);add('h1','bm-legacy-heading',active==='2023'?'Reprendre votre travail':'Votre registre de fichiers',intro);add('p','bm-legacy-subtitle',t.subtitle,intro);
    var stats=add('div','bm-legacy-stats',undefined,hero);[[records.filter(function(x){return !x.family;}).length,'projets récents'],[records.filter(function(x){return x.family;}).length,'familles récentes']].forEach(function(v){var stat=add('div','bm-legacy-stat',undefined,stats);add('strong','',String(v[0]),stat);add('span','',v[1],stat);});view.insertBefore(hero,view.firstChild);
    var bar=add('div','bm-legacy-toolbar bm-legacy-owned');var search=add('input','bm-legacy-search',undefined,bar);search.type='search';search.placeholder='Rechercher un fichier ou un dossier…';search.setAttribute('aria-label','Rechercher par nom ou chemin');search.oninput=function(){if(search.getAttribute('data-history-scope')==='history')return;query=search.value.toLowerCase().trim();refresh();};
    [['all','Tout'],['project','Projets'],['family','Familles']].forEach(function(v){var b=add('button','',v[1],bar);b.type='button';b.setAttribute('data-bm-filter',v[0]);b.onclick=function(){filter=v[0];refresh();};});
    var samples=add('button','','Masquer les exemples',bar);samples.type='button';samples.setAttribute('data-hc-samples','true');samples.setAttribute('aria-pressed','false');samples.onclick=function(){hideSamples=!hideSamples;samples.setAttribute('aria-pressed',String(hideSamples));refresh();};hero.insertAdjacentElement('afterend',bar);
    var status=add('p','bm-legacy-status bm-legacy-owned','');bar.insertAdjacentElement('afterend',status);add('div','bm-legacy-empty bm-legacy-owned','Aucun fichier ne correspond à ces filtres.',view);
    observer=new MutationObserver(function(){clearTimeout(timer);timer=setTimeout(refresh,90);});refresh();return {applied:true,evidence:inspect()};
  }
  window.BIMaestroHomeConceptLegacy={apply:apply,inspect:inspect,restore:restore,setQuery:function(value){query=String(value||'').toLowerCase().trim();refresh();}};return inspect();
})();



