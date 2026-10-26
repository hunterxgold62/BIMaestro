param([ValidateSet('2023','2024','2025','2026','2027')][string]$RevitVersion='2025',[ValidatePattern('^[a-zA-Z0-9-]*$')][string]$OutputTag='',[switch]$ReplaceLabRegistration)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/g$RevitVersion$OutputTag"
$manifest=Join-Path $out 'BIMaestro.PropertiesGroupCompatibilityProbe.addin'
$registration=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.PropertiesGroupCompatibilityProbe.addin"
if(Test-Path -LiteralPath $registration){
 if(!$ReplaceLabRegistration){throw 'A test registration already exists; inspect before replacing.'}
 [xml]$old=Get-Content -LiteralPath $registration -Raw
 $assembly=[IO.Path]::GetFullPath([string]$old.RevitAddIns.AddIn.Assembly)
 $labRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp'))+[IO.Path]::DirectorySeparatorChar
 if($old.RevitAddIns.AddIn.AddInId -ne 'DD42A276-3D6F-4EC3-8E21-41B0637EDD16' -or !$assembly.StartsWith($labRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Registration is not owned by this laboratory.'}
 Remove-Item -LiteralPath $registration
}
Copy-Item -LiteralPath $manifest -Destination $registration
$process=Start-Process -FilePath "C:/Program Files/Autodesk/Revit $RevitVersion/Revit.exe" -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path $out 'target-pid.txt'),$process.Id.ToString())
Write-Output "Revit $RevitVersion PID $($process.Id); temporary registration: $registration"
