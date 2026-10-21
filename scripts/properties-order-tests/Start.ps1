param([ValidateSet('2023','2024','2025','2026','2027')][string]$RevitVersion='2025',[ValidatePattern('^[a-zA-Z0-9-]*$')][string]$OutputTag='',[switch]$ReplaceLabRegistration)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-order-tests/$RevitVersion$OutputTag"
$manifest=Join-Path $out 'BIMaestro.PropertiesOrderProbe.addin'
$registration=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.PropertiesOrderProbe.addin"
if(Test-Path -LiteralPath $registration){
 if(!$ReplaceLabRegistration){throw 'A test registration already exists; inspect before replacing.'}
 [xml]$old=Get-Content -LiteralPath $registration -Raw
 $assembly=[IO.Path]::GetFullPath([string]$old.RevitAddIns.AddIn.Assembly)
 $labRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/properties-order-tests'))+[IO.Path]::DirectorySeparatorChar
 if($old.RevitAddIns.AddIn.AddInId -ne '16F3166F-AF76-444F-BBF9-CE851BD98221' -or !$assembly.StartsWith($labRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Registration is not owned by this laboratory.'}
 Remove-Item -LiteralPath $registration
}
Copy-Item -LiteralPath $manifest -Destination $registration
$process=Start-Process -FilePath "C:/Program Files/Autodesk/Revit $RevitVersion/Revit.exe" -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path $out 'target-pid.txt'),$process.Id.ToString())
Write-Output "Revit $RevitVersion PID $($process.Id); temporary registration: $registration"
