param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'clash-matrix-20261026', [ValidateSet('2023','2024','2025','2026','2027')][string[]]$Versions = @('2023','2024','2025','2026','2027'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($year in $Versions) {
    $folder = Join-Path $repo "tmp/codex-native-validation/$year-$RunName"
    & (Join-Path $repo 'scripts/codex-tests/StartNativeHarness.ps1') -RevitVersion $year -RunName $RunName
    $testProcess = Get-Process -Id ([int](Get-Content -LiteralPath (Join-Path $folder 'launched-pid.txt') -Raw))
    [void]$testProcess.Handle
    $deadline = [DateTime]::UtcNow.AddMinutes(6)
    while (!(Test-Path -LiteralPath (Join-Path $folder 'exited-cleanly.txt')) -and [DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath (Join-Path $folder 'finished.txt')) -and (Get-Content -LiteralPath (Join-Path $folder 'finished.txt') -Raw) -like '*fatal*') { throw "Revit $year : fatal test error; see $folder" }
        Start-Sleep -Milliseconds 1000
    }
    if (!(Test-Path -LiteralPath (Join-Path $folder 'exited-cleanly.txt'))) { throw "Revit $year : tests ou sortie non terminés ; voir $folder" }
    if (!$testProcess.WaitForExit(45000)) { throw "Revit $year : processus encore ouvert après OnShutdown" }
    [IO.File]::WriteAllText((Join-Path $folder 'process-exit-code.txt'), $testProcess.ExitCode.ToString())
    if ($testProcess.ExitCode -ne 0) { throw "Revit $year : arrêt anormal ($($testProcess.ExitCode))" }
    $result = Get-Content -LiteralPath (Join-Path $folder 'result.json') -Raw | ConvertFrom-Json
    Write-Output "Revit $year : $(@($result.tests).Count) tests, $($result.failed) failures, clean shutdown"
}
