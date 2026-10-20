param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunFolder = 'browser-icons-variety', [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/' + $RunFolder)
$registration = 'C:/Users/lemer/AppData/Roaming/Autodesk/Revit/Addins/2024/ZZ.BIMaestro.BrowserDemo.addin'
if (Test-Path -LiteralPath $registration) { throw 'Un manifeste de démonstration existe déjà.' }
if (Test-Path -LiteralPath (Join-Path $output 'started.txt')) { throw 'Cette génération a déjà été lancée.' }
Copy-Item -LiteralPath (Join-Path $output 'ZZ.BIMaestro.BrowserDemo.addin') -Destination $registration
[IO.File]::WriteAllText((Join-Path $output 'run.flag'), 'generate')
if ($VerifyOnly) { [IO.File]::WriteAllText((Join-Path $output 'verify.flag'), 'verify') }
$process = Start-Process -FilePath 'C:/Program Files/Autodesk/Revit 2024/Revit.exe' -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path $output 'launched-pid.txt'), $process.Id.ToString())
Write-Output ('Revit lancé : ' + $process.Id)
