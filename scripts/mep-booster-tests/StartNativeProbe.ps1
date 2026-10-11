param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'mep-booster-native-2025')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/' + $RunName)
$dll = Join-Path $repo 'BIMaestro/bin/Release/BIMaestro.dll'
$installed = 'C:\Users\lemer\AppData\Local\BIMaestro\Bin\BIMaestro.dll'
$addinRoot = 'C:\Users\lemer\AppData\Roaming\Autodesk\Revit\Addins\2025'
$registration = Join-Path $addinRoot 'BIMaestro.MepBooster.NativeProbe.addin'
if (Test-Path -LiteralPath $registration) { throw 'A previous probe registration exists.' }
if (Test-Path -LiteralPath (Join-Path $output 'loaded.txt')) { throw 'A previous probe run exists.' }
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) {
    if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before replacing the installed DLL.' }
    Copy-Item -LiteralPath $installed -Destination (Join-Path $output 'BIMaestro.before-probe.dll')
    Copy-Item -LiteralPath $dll -Destination $installed -Force
}
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw 'Installed DLL hash mismatch.' }
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/bin/Release/Demo/Maquette/BIMaestro_Apprentissage_2024.rvt') -Destination (Join-Path $output 'BoosterFixture.rvt')
$assembly = [Security.SecurityElement]::Escape((Join-Path $output 'BIMaestro.MepBooster.NativeProbe.dll'))
$xml = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro MEP native probe</Name><Assembly>' + $assembly + '</Assembly><AddInId>FEA7424B-9F44-4657-B17D-86BE3F675102</AddInId><FullClassName>BIMaestro.MepTests.NativeProbe</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText($registration, $xml)
$process = Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path $output 'launched-pid.txt'), $process.Id.ToString())
Write-Output ('Revit 2025 probe PID: ' + $process.Id)
Write-Output ('Installed shared DLL SHA256: ' + (Get-FileHash -LiteralPath $installed).Hash)
