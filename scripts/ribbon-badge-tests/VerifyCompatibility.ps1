param(
    [ValidateSet('2023','2024','2025','2026','2027')][string[]]$RevitVersions = @('2023','2024','2025','2026','2027'),
    [switch]$SkipBuild,
    [string]$DotNet = '',
    [string]$MSBuild = 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
$compatRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$DotNet) {
    $DotNet = Join-Path $compatRepo 'tmp/dotnet10/dotnet.exe'
    if (!(Test-Path -LiteralPath $DotNet)) { $DotNet = (Get-Command dotnet -ErrorAction Stop).Source }
}
$compatOutput = Join-Path $compatRepo 'tmp/ribbon-compatibility'
New-Item -ItemType Directory -Path $compatOutput -Force | Out-Null
Push-Location $compatRepo
try {
    & $DotNet build scripts/ribbon-badge-tests/CompatibilityCheck.csproj -p:NuGetAudit=false --verbosity quiet *> (Join-Path $compatOutput 'checker-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Compilation du vérificateur impossible.' }
    $compatResults = foreach ($compatYear in $RevitVersions) {
        $compatHost = 'C:/Program Files/Autodesk/Revit ' + $compatYear
        if (!(Test-Path -LiteralPath (Join-Path $compatHost 'RevitAPI.dll'))) {
            [pscustomobject]@{Version=$compatYear;Status='UNAVAILABLE';Detail='Revit/API absents : compilation et test natif non vérifiés'}
            continue
        }
        $compatBin = switch ($compatYear) {
            '2023' { 'BIMaestro/bin/Release' }
            '2024' { 'BIMaestro/bin/Release2024' }
            default { 'BIMaestro/bin/Revit' + $compatYear + '/Release' }
        }
        $compatLog = Join-Path $compatOutput ('build-' + $compatYear + '.log')
        if (!$SkipBuild) {
            if ([int]$compatYear -le 2024) {
                $compatConfiguration = if ($compatYear -eq '2024') { 'Release2024' } else { 'Release' }
                & $MSBuild BIMaestro/BIMaestro.csproj /t:Build ("/p:Configuration="+$compatConfiguration) /p:RestoreIgnoreFailedSources=true /m:1 /nr:false /verbosity:minimal *> $compatLog
            } else {
                & $DotNet build BIMaestro/BIMaestro.Revit2025.csproj -c Release ("-p:RevitVersion="+$compatYear) -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --verbosity quiet *> $compatLog
            }
            if ($LASTEXITCODE -ne 0) {
                [pscustomobject]@{Version=$compatYear;Status='FAIL';Detail='Compilation échouée : '+$compatLog}
                continue
            }
        }
        $compatCheckLog = Join-Path $compatOutput ('contracts-' + $compatYear + '.log')
        & $DotNet tmp/ribbon-compat-check/net10.0-windows/CompatibilityCheck.dll $compatHost (Join-Path $compatBin 'BIMaestro.dll') 'BIMaestro/app et excel/Touche/RibbonToolTipExamples.cs' *> $compatCheckLog
        $compatStatus = if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }
        [pscustomobject]@{Version=$compatYear;Status=$compatStatus;Detail='API/runtime, contrats du ruban et médias : '+$compatCheckLog}
    }
    $compatResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $compatOutput 'results.json') -Encoding utf8
    $compatResults | Format-Table -AutoSize
    if (@($compatResults | Where-Object Status -ne 'PASS').Count) { exit 1 }
} finally { Pop-Location }
