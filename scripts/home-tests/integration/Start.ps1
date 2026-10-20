param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$out=Join-Path $repo "tmp/home-tests/integration/$RevitVersion"
$source=Join-Path $out 'BIMaestro.HomeIntegration.addin'
$target=Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.HomeIntegration.addin"
if(!(Test-Path -LiteralPath $source)){throw 'Build first'}
if((Test-Path -LiteralPath $target) -and [IO.File]::ReadAllText($target) -ne [IO.File]::ReadAllText($source)){throw 'Different integration manifest already installed'}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
$launchTime=[DateTime]::UtcNow
try {
 Copy-Item -LiteralPath $source -Destination $target -Force
 $process=Start-Process -FilePath "C:/Program Files/Autodesk/Revit $RevitVersion/Revit.exe" -WindowStyle Hidden -PassThru
 [IO.File]::WriteAllText((Join-Path $out 'launched-pid.txt'),$process.Id.ToString())
 Write-Output "Revit $RevitVersion PID $($process.Id). Waiting for integration startup."
 $deadline=[DateTime]::UtcNow.AddSeconds(180)
 $started=Join-Path $out 'started.txt'
 while([DateTime]::UtcNow -lt $deadline){
  if((Test-Path -LiteralPath $started) -and (Get-Item -LiteralPath $started).LastWriteTimeUtc -ge $launchTime){Write-Output 'Integration started; removing temporary registration.';break}
  if($process.HasExited){throw "Revit exited before integration startup: $($process.ExitCode)"}
  Start-Sleep -Milliseconds 500
 }
 if(!(Test-Path -LiteralPath $started) -or (Get-Item -LiteralPath $started).LastWriteTimeUtc -lt $launchTime){throw 'Integration startup timeout; interaction may be pending'}
} finally {
 if((Test-Path -LiteralPath $target) -and [IO.File]::ReadAllText($target) -eq [IO.File]::ReadAllText($source)){Remove-Item -LiteralPath $target}
}
