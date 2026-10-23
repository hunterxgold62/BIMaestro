param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'antibes-interactive-v1')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/history-interactive/' + $RunName)
$registration = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.History.Interactive.addin'
if (!(Test-Path -LiteralPath (Join-Path $output 'BIMaestro.History.Interactive.addin'))) { throw 'Prepare the observer first.' }
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath (Join-Path $output 'observer-pid.txt'))) { throw 'Existing observer registration/run is preserved. Use a fresh run.' }
try {
    Copy-Item -LiteralPath (Join-Path $output 'BIMaestro.History.Interactive.addin') -Destination $registration
    $process = Start-Process -FilePath 'C:/Program Files/Autodesk/Revit 2025/Revit.exe' -WindowStyle Hidden -PassThru
    [IO.File]::WriteAllText((Join-Path $output 'observer-pid.txt'), $process.Id.ToString())
    Write-Output ('Isolated Revit 2025 PID=' + $process.Id)
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    while (!(Test-Path -LiteralPath (Join-Path $output 'ready.json')) -and [DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $output 'error.txt')) { throw [IO.File]::ReadAllText((Join-Path $output 'error.txt')) }
        if ($process.HasExited) { throw 'Isolated process exited before observer readiness.' }
        Start-Sleep -Milliseconds 250
    }
    if (!(Test-Path -LiteralPath (Join-Path $output 'ready.json'))) { throw 'Observer startup timeout; inspect isolated UI without touching user processes.' }
    Get-Content -LiteralPath (Join-Path $output 'ready.json')
} finally {
    if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration }
}
