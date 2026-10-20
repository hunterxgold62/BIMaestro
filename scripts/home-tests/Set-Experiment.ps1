param(
 [ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025',
 [ValidateSet('inspect','compact','dark','blueprint','large','restore','dom')][string]$Preset='inspect'
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo "tmp/home-tests/$RevitVersion"
if(!(Test-Path -LiteralPath $folder)){throw 'Build the probe first'}
$body=if($Preset -eq 'dom'){
 'return JSON.stringify({title:document.title,url:location.href,html:document.documentElement.outerHTML});'
}else{
 $source="if(window.BIMaestroHomeProbe){window.BIMaestroHomeProbe.restore();delete window.BIMaestroHomeProbe;}`n"+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'home-presets.js'))
 $action=switch($Preset){
  'inspect' {'window.__bimaestroHomeInspect()'}
  'restore' {'window.__bimaestroHomeRestore()'}
  default {"window.__bimaestroHomeApply('$Preset')"}
 }
 $source+"`nreturn JSON.stringify($action);"
}
[IO.File]::WriteAllText((Join-Path $folder 'control.js'),$body)
Write-Output "Revit ${RevitVersion}: $Preset queued. Read the next result-*.txt and inspect the real window."
