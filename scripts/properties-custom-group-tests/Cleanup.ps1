$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest='C:/Users/plemert/AppData/Roaming/Autodesk/Revit/Addins/2025/BIMaestro.PropertiesCustomGroupProbe.addin'
if(!(Test-Path -LiteralPath $manifest)){return}
[xml]$xml=Get-Content -LiteralPath $manifest -Raw
$assembly=[IO.Path]::GetFullPath([string]$xml.RevitAddIns.AddIn.Assembly)
$root=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/properties-custom-group-tests'))+[IO.Path]::DirectorySeparatorChar
if($xml.RevitAddIns.AddIn.AddInId -ne '95BC4886-024F-44E2-A4D3-37EE3B27DC25' -or !$assembly.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing unowned registration'}
Remove-Item -LiteralPath $manifest
Write-Output 'Removed custom group laboratory registration (2025 only).'