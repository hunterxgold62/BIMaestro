param([ValidateSet('2025')][string]$RevitVersion='2025',[ValidatePattern('^[a-zA-Z0-9-]*$')][string]$OutputTag='',[switch]$ReplaceLabRegistration)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-custom-group-tests/$RevitVersion$OutputTag"
$manifest=Join-Path $out 'BIMaestro.PropertiesCustomGroupProbe.addin'
$registration=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.PropertiesCustomGroupProbe.addin"
if(!(Test-Path -LiteralPath $manifest -PathType Leaf)){throw 'Build the isolated laboratory before starting Revit.'}
[xml]$newManifest=Get-Content -LiteralPath $manifest -Raw
$newAssembly=[IO.Path]::GetFullPath([string]$newManifest.RevitAddIns.AddIn.Assembly)
$expectedAssembly=[IO.Path]::GetFullPath((Join-Path $out 'BIMaestro.PropertiesCustomGroupProbe.dll'))
if($newManifest.RevitAddIns.AddIn.AddInId -ne '95BC4886-024F-44E2-A4D3-37EE3B27DC25' -or $newAssembly -ne $expectedAssembly -or !(Test-Path -LiteralPath $newAssembly -PathType Leaf)){throw 'The laboratory manifest does not reference its existing isolated assembly.'}
if(Test-Path -LiteralPath $registration){
 if(!$ReplaceLabRegistration){throw 'A test registration already exists; inspect before replacing.'}
 [xml]$old=Get-Content -LiteralPath $registration -Raw
 $assembly=[IO.Path]::GetFullPath([string]$old.RevitAddIns.AddIn.Assembly)
 $labRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/properties-custom-group-tests'))+[IO.Path]::DirectorySeparatorChar
 if($old.RevitAddIns.AddIn.AddInId -ne '95BC4886-024F-44E2-A4D3-37EE3B27DC25' -or !$assembly.StartsWith($labRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Registration is not owned by this laboratory.'}
 Remove-Item -LiteralPath $registration
}
Copy-Item -LiteralPath $manifest -Destination $registration
$process=Start-Process -FilePath "C:/Program Files/Autodesk/Revit $RevitVersion/Revit.exe" -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path $out 'target-pid.txt'),$process.Id.ToString())
Write-Output "Revit $RevitVersion PID $($process.Id); temporary registration: $registration"
