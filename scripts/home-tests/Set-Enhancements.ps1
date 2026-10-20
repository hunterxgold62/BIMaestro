param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025',[switch]$Restore)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo "tmp/home-tests/$RevitVersion"
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'home-enhancements.js'))
$action=if($Restore){'window.__bimaestroHomeEnhance.restore()'}else{'window.__bimaestroHomeEnhance.apply({group:true,alphabetical:true})'}
[IO.File]::WriteAllText((Join-Path $folder 'control.js'),$source+"`nreturn JSON.stringify($action);")
Write-Output "Home enhancements queued for $RevitVersion (restore=$Restore)"
