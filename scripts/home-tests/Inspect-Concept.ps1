param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2026')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script=@'
return JSON.stringify((function(){
 var api=window.BIMaestroHomeConceptModern||window.BIMaestroHomeConceptLegacy;
 var cards=Array.from(document.querySelectorAll('[data-testid="tile-card"],.RecentFilesView__thumbnails-thumbnail'));
 var samples=cards.slice(0,5).map(function(c){var list=[],n=c.querySelector('[data-testid="info-wrapper"]')||c;
 for(var i=0;n&&i<5;i++,n=n.parentElement){var s=getComputedStyle(n),r=n.getBoundingClientRect();list.push({tag:n.tagName,cls:n.className,testid:n.getAttribute('data-testid'),height:r.height,width:r.width,display:s.display,cssHeight:s.height,minHeight:s.minHeight,maxHeight:s.maxHeight,rows:s.gridTemplateRows,autoRows:s.gridAutoRows,overflow:s.overflow});}
 return list;});
 return {concept:api?api.inspect():null,geometry:samples,viewport:{width:innerWidth,height:innerHeight},dom:document.documentElement.outerHTML};
})());
'@
[IO.File]::WriteAllText((Join-Path $repo "tmp/home-tests/$RevitVersion/control.js"),$script)
