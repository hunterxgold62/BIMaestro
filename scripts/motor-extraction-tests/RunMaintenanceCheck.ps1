param([switch]$SkipInstall, [string]$RunName='maintenance-native', [ValidateSet('full','inspect','auto','release','cases','generic','generic-only','valve955038')][string]$Mode='full', [string]$ProductDirectory='', [long[]]$InspectionIds=@())
$ErrorActionPreference='Stop'
if ($InspectionIds.Count -gt 0 -and $Mode -ne 'inspect') {throw 'InspectionIds requiert le mode inspect'}
if (@($InspectionIds | Where-Object {$_ -le 0}).Count -gt 0) {throw 'Les identifiants pour inspection doivent être strictement positifs'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$SkipInstall) { & (Join-Path $repo 'scripts/clash3d-tests/InstallRevit2025Fix.ps1') -RevitVersion 2025 -SourceDirectory $ProductDirectory }
$runRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/motor-extraction-builds'))
$folder=[IO.Path]::GetFullPath((Join-Path $runRoot $RunName))
if (!$folder.StartsWith($runRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Dossier de test invalide'}
$dll=Join-Path $folder 'BIMaestro.AntibesRouteCheck.dll'
if (!(Test-Path -LiteralPath $dll)) {throw 'Harness manquant'}
$manifest=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/ZZ.BIMaestro.AntibesRouteCheck.addin'
if (Test-Path -LiteralPath $manifest) {
    [xml]$previous=Get-Content -LiteralPath $manifest -Raw
    if ($previous.RevitAddIns.AddIn.FullClassName -ne 'BIMaestro.MotorTests.AntibesRouteCheck') {throw 'Manifeste inattendu'}
}
$xml=[xml]'<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Antibes route check</Name><Assembly></Assembly><AddInId>1A171F8E-C090-45F6-9B28-5B815DE4CB6F</AddInId><FullClassName>BIMaestro.MotorTests.AntibesRouteCheck</FullClassName><VendorId>BMST</VendorId><VendorDescription>Local read-only and rolled-back maintenance tests</VendorDescription></AddIn></RevitAddIns>'
$xml.RevitAddIns.AddIn.Assembly=$dll
$xml.Save($manifest)
try {
    $env:BIMAESTRO_ANTIBES_ROUTE_CHECK='1'
    $env:BIMAESTRO_MAINTENANCE_MODE=$Mode
    $env:BIMAESTRO_MAINTENANCE_INSPECTION_IDS=[string]::Join(',',@($InspectionIds | Select-Object -Unique))
    $env:BIMAESTRO_ANTIBES_COPY=Join-Path $repo 'tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt'
    $process=Start-Process -FilePath 'C:/Program Files/Autodesk/Revit 2025/Revit.exe' -ArgumentList '/nosplash' -WindowStyle Normal -PassThru
    $process.Id | Set-Content -LiteralPath (Join-Path $folder 'revit-pid.txt')
    Write-Output ('Test Revit 2025 lancé, PID '+$process.Id)
} finally {
    Remove-Item Env:BIMAESTRO_ANTIBES_ROUTE_CHECK -ErrorAction SilentlyContinue
    Remove-Item Env:BIMAESTRO_MAINTENANCE_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:BIMAESTRO_MAINTENANCE_INSPECTION_IDS -ErrorAction SilentlyContinue
    Remove-Item Env:BIMAESTRO_ANTIBES_COPY -ErrorAction SilentlyContinue
}
