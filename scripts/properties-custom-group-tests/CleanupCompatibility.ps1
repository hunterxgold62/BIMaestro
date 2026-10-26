param([ValidateSet('2023','2024','2025','2026','2027')][string[]]$RevitVersion=@('2023','2024','2025','2026','2027'))
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$boundary=[IO.Path]::GetFullPath((Join-Path $repo 'tmp'))+[IO.Path]::DirectorySeparatorChar
foreach($year in $RevitVersion){
 $manifest=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$year/BIMaestro.PropertiesGroupCompatibilityProbe.addin"
 if(!(Test-Path -LiteralPath $manifest)){continue}
 [xml]$xml=Get-Content -LiteralPath $manifest -Raw
 $assembly=[IO.Path]::GetFullPath([string]$xml.RevitAddIns.AddIn.Assembly)
 if($xml.RevitAddIns.AddIn.AddInId -ne 'DD42A276-3D6F-4EC3-8E21-41B0637EDD16' -or !$assembly.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($assembly) -ne 'BIMaestro.PropertiesGroupCompatibilityProbe.dll'){throw "Refusing an unowned registration: $manifest"}
 Remove-Item -LiteralPath $manifest
 Write-Output "Removed compatibility test registration: $year"
}
