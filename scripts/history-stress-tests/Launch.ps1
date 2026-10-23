param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName,
    [switch]$OpenWhenReady,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedProductSha256
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/history-stress/' + $RunName)
$registration = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.History.Stress.addin'
if (!(Test-Path -LiteralPath (Join-Path $output 'BIMaestro.History.Stress.addin'))) { throw 'Prepare this new run first.' }
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath (Join-Path $output 'observer-pid.txt'))) { throw 'Existing registration/run is preserved. Choose a new RunName.' }
try {
    Copy-Item -LiteralPath (Join-Path $output 'BIMaestro.History.Stress.addin') -Destination $registration
    $previousManualMode=[Environment]::GetEnvironmentVariable('BIMAESTRO_HISTORY_MANUAL_TEST','Process')
    try {
        [Environment]::SetEnvironmentVariable('BIMAESTRO_HISTORY_MANUAL_TEST','1','Process')
        $process = Start-Process -FilePath 'C:/Program Files/Autodesk/Revit 2025/Revit.exe' -WindowStyle Hidden -PassThru
    } finally {
        [Environment]::SetEnvironmentVariable('BIMAESTRO_HISTORY_MANUAL_TEST',$previousManualMode,'Process')
    }
    [IO.File]::WriteAllText((Join-Path $output 'observer-pid.txt'), $process.Id.ToString())
    Write-Output ('Stress Revit 2025 PID=' + $process.Id)
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    while (!(Test-Path -LiteralPath (Join-Path $output 'ready.json')) -and [DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $output 'error.txt')) { throw [IO.File]::ReadAllText((Join-Path $output 'error.txt')) }
        if ($process.HasExited) { throw 'Isolated stress process exited before readiness.' }
        Start-Sleep -Milliseconds 250
    }
    if (!(Test-Path -LiteralPath (Join-Path $output 'ready.json'))) { throw 'Readiness timeout. Preserve this process and inspect its logs.' }
    $ready = Get-Content -LiteralPath (Join-Path $output 'ready.json') -Raw | ConvertFrom-Json
    if ($ready.Pid -ne $process.Id) { throw 'Readiness PID differs from the launched process; no open request was written.' }
    $productMatches = !$ExpectedProductSha256 -or [string]::Equals($ExpectedProductSha256,$ready.Sha256,[StringComparison]::OrdinalIgnoreCase)
    @{ AtUtc=[DateTime]::UtcNow.ToString('o'); Pid=$process.Id; Product=$ready.Product; ActualProductSha256=$ready.Sha256; ExpectedProductSha256=$ExpectedProductSha256; ProductMatches=$productMatches; OpenWhenReady=[bool]$OpenWhenReady; OpenAllowed=($productMatches -and [bool]$OpenWhenReady); LauncherSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'launch-validation.json') -Encoding UTF8
    if (!$productMatches) { throw ('Loaded product SHA256 mismatch. Expected ' + $ExpectedProductSha256 + '; actual ' + $ready.Sha256 + '. No open request was written. Process PID ' + $process.Id + ' is preserved.') }
    if ($OpenWhenReady) {
        [IO.File]::WriteAllText((Join-Path $output 'open.request'), [DateTime]::UtcNow.ToString('O'))
    }
    Get-Content -LiteralPath (Join-Path $output 'ready.json')
} finally {
    if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration }
}
