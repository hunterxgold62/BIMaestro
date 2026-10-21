param([ValidatePattern('^[-a-zA-Z0-9_]*$')][string]$OutputTag='')
$ErrorActionPreference='Stop'
$results=foreach($year in 2023..2027){
 $installed=Test-Path "C:/Program Files/Autodesk/Revit $year/RevitAPI.dll"
 if(!$installed){[pscustomobject]@{Version=$year;Installed=$false;Built=$false};continue}
 & (Join-Path $PSScriptRoot 'Build.ps1') -RevitVersion "$year" -OutputTag $OutputTag | Out-Host
 [pscustomobject]@{Version=$year;Installed=$true;Built=$true}
}
$results | Format-Table -AutoSize
