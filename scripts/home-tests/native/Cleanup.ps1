param([ValidateSet('2023','2024','2025','2026')][string[]]$RevitVersion=@('2023','2024','2025','2026'))
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
foreach($version in $RevitVersion){
 $source=Join-Path $repo "tmp/home-tests/$version/BIMaestro.HomeProbe.addin"
 $target=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$version/BIMaestro.HomeProbe.addin"
 if(!(Test-Path -LiteralPath $target)){Write-Output "$version : no temporary manifest";continue}
 if(!(Test-Path -LiteralPath $source) -or [IO.File]::ReadAllText($target) -cne [IO.File]::ReadAllText($source)){throw "Unexpected manifest content for $version; no removal performed"}
 Remove-Item -LiteralPath $target
 Write-Output "$version : removed exact Home probe manifest"
}
