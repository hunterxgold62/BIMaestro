param([ValidateSet('2025','2026')][string]$RevitVersion='2026',[switch]$Restore)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$catalog=Get-Content (Join-Path $repo 'tmp/home-tests/catalog/catalog.json') -Raw|ConvertFrom-Json -AsHashtable
$payload=ConvertTo-Json -InputObject @($catalog.entries) -Depth 12 -Compress
$literal=ConvertTo-Json -InputObject $payload -Compress
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'home-catalog-probe.js'))
$revision=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($source+$payload))).Replace('-','')
$script=if($Restore){'if(window.__bmLogCatalogProbe)return JSON.stringify(window.__bmLogCatalogProbe.restore());return JSON.stringify({restored:true});'}else{
 "if(window.__bmLogCatalogRevision==='$revision' && window.__bmLogCatalogProbe)return JSON.stringify(window.__bmLogCatalogProbe.inspect());if(window.__bmLogCatalogProbe){window.__bmLogCatalogProbe.restore();delete window.__bmLogCatalogProbe;}var started=performance.now();window.__bmLogCatalogData=JSON.parse($literal);var parseMs=performance.now()-started;`n"+$source+"`nvar result=window.__bmLogCatalogProbe.inspect();result.metrics.catalogJsonParseMs=parseMs;window.__bmLogCatalogRevision='$revision';return JSON.stringify(result);"
}
[IO.File]::WriteAllText((Join-Path $repo "tmp/home-tests/$RevitVersion/control.js"),$script)
Write-Output "Excel catalogue probe queued in Home $RevitVersion (restore=$Restore)"
