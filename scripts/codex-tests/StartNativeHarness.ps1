param([ValidateSet('2023','2024')][string]$RevitVersion = '2023')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion)
$source = Join-Path $output 'BIMaestro.NativeValidation.addin'
$registration = Join-Path $env:APPDATA ('Autodesk/Revit/Addins/' + $RevitVersion + '/BIMaestro.NativeValidation.addin')
if (!(Test-Path -LiteralPath $source)) { throw 'Compiler le banc avant de le lancer.' }
if (Test-Path -LiteralPath $registration) { throw 'Un manifeste de validation existe déjà. Aucun remplacement automatique.' }
if (Test-Path -LiteralPath (Join-Path $output 'started.txt')) { throw 'Un résultat précédent existe. Archiver explicitement les marqueurs avant une nouvelle exécution.' }
$registered = $false
try {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($registration)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $registration
    $registered = $true
    $process = Start-Process -FilePath ('C:/Program Files/Autodesk/Revit ' + $RevitVersion + '/Revit.exe') -WindowStyle Hidden -PassThru
    [IO.File]::WriteAllText((Join-Path $output 'launched-pid.txt'), $process.Id.ToString())
    Write-Output ('Instance de validation lancée : PID ' + $process.Id)
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while (!(Test-Path -LiteralPath (Join-Path $output 'started.txt')) -and [DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'Revit a quitté avant de charger le banc.' }
        Start-Sleep -Milliseconds 500
    }
    if (!(Test-Path -LiteralPath (Join-Path $output 'started.txt'))) { throw 'Le banc ne démarre pas : Revit attend probablement une interaction au démarrage.' }
    Write-Output 'Banc chargé ; inscription temporaire supprimée. Les tests continuent dans cette instance.'
} finally {
    if ($registered -and (Test-Path -LiteralPath $registration)) { Remove-Item -LiteralPath $registration }
}
