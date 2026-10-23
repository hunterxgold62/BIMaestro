param([string]$RunName='maintenance-native-v3')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/motor-extraction-builds'))
$runFolder=[IO.Path]::GetFullPath((Join-Path $runRoot $RunName))
if(!$runFolder.StartsWith($runRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Dossier de test invalide'}
$pidFile=Join-Path $runFolder 'revit-pid.txt'
if (Test-Path -LiteralPath $pidFile) {
    $testPid=[int](Get-Content -LiteralPath $pidFile -Raw)
    $active=Get-Process -Id $testPid -ErrorAction SilentlyContinue
    if ($active -and $active.ProcessName -eq 'Revit') {throw 'Le processus de test est encore actif : attendre sa fermeture normale.'}
}
$manifest=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/ZZ.BIMaestro.AntibesRouteCheck.addin'
if (Test-Path -LiteralPath $manifest) {
    [xml]$xml=Get-Content -LiteralPath $manifest -Raw
    if ($xml.RevitAddIns.AddIn.FullClassName -ne 'BIMaestro.MotorTests.AntibesRouteCheck') {throw 'Manifeste de test inattendu, aucun retrait.'}
    $assembly=[IO.Path]::GetFullPath([string]$xml.RevitAddIns.AddIn.Assembly)
    $expected=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/motor-extraction-builds'))+[IO.Path]::DirectorySeparatorChar
    if (!$assembly.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) {throw 'Assemblage de test hors périmètre.'}
    Remove-Item -LiteralPath $manifest
    Write-Output 'Manifeste temporaire Antibes retiré.'
}
