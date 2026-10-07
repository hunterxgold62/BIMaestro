param([ValidateSet('2024','2025')][string]$RevitVersion = '2024', [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'reference-base')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
$source = Join-Path $output 'BIMaestro.NativeValidation.addin'
$addinFolder = Join-Path $env:APPDATA ('Autodesk/Revit/Addins/' + $RevitVersion)
$registration = Join-Path $addinFolder 'BIMaestro.ReferenceValidation.addin'
if (!(Test-Path -LiteralPath $source)) { throw 'Compiler le banc avant de le lancer.' }
if (Test-Path -LiteralPath (Join-Path $output 'started.txt')) { throw 'Utiliser un nouveau nom de passage.' }
if (Test-Path -LiteralPath $registration) { throw 'Un banc de tutoriel est déjà inscrit. Aucun remplacement.' }
if (Test-Path -LiteralPath (Join-Path $addinFolder 'BIMaestro.NativeValidation.addin')) { throw 'Un autre banc natif est inscrit. Attendre son chargement avant de lancer ce banc.' }
$registered = $false
try {
    New-Item -ItemType Directory -Path $addinFolder -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $registration
    $registered = $true
    $process = Start-Process -FilePath ('C:/Program Files/Autodesk/Revit ' + $RevitVersion + '/Revit.exe') -WindowStyle Hidden -PassThru
    [IO.File]::WriteAllText((Join-Path $output 'launched-pid.txt'), $process.Id.ToString())
    Write-Output ('Instance tutoriel lancée : PID ' + $process.Id)
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while (!(Test-Path -LiteralPath (Join-Path $output 'started.txt')) -and [DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'Revit a quitté avant le chargement du banc.' }
        Start-Sleep -Milliseconds 500
    }
    if (!(Test-Path -LiteralPath (Join-Path $output 'started.txt'))) { throw 'Revit attend probablement une interaction au démarrage.' }
    Write-Output 'Banc tutoriel chargé ; manifeste temporaire retiré.'
} finally {
    if ($registered -and (Test-Path -LiteralPath $registration)) { Remove-Item -LiteralPath $registration }
}
