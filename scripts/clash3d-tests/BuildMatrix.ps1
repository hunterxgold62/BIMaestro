param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'clash-matrix-20261026', [ValidateSet('2023','2024','2025','2026','2027')][string[]]$Versions = @('2023','2024','2025','2026','2027'), [switch]$ProductionOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repo 'tmp/dotnet10/dotnet.exe'
$msbuild = 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'
$results = @()
$assemblyName = if ($ProductionOnly) { 'BIMaestro' } else { 'BIMaestro.ClashActionValidation' }
$binPrefix = if ($ProductionOnly) { 'ClashVerified' } else { 'ClashActionValidation' }
foreach ($year in $Versions) {
    $folder = Join-Path $repo "tmp/codex-native-validation/$year-$RunName"
    if (Test-Path -LiteralPath (Join-Path $folder 'started.txt')) { throw "Passage $year déjà démarré : choisir un nouveau RunName." }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $bin = Join-Path $repo "BIMaestro/bin/$binPrefix$year"
    $log = Join-Path $folder 'production-build.log'
    if ([int]$year -lt 2025) {
        $configuration = if ($year -eq '2024') { 'Release2024' } else { 'Release' }
        & $msbuild (Join-Path $repo 'BIMaestro/BIMaestro.csproj') /t:Build "/p:Configuration=$configuration" /p:Platform=AnyCPU "/p:OutputPath=$bin/" "/p:IntermediateOutputPath=obj/ClashMatrix$year/" "/p:AssemblyName=$assemblyName" /m:1 /nr:false /v:q *> $log
    } else {
        & $dotnet build (Join-Path $repo 'BIMaestro/BIMaestro.Revit2025.csproj') --no-restore -c Release "-p:RevitVersion=$year" "-p:BaseIntermediateOutputPath=obj/Revit$year/" "-p:IntermediateOutputPath=obj/ClashMatrix$year/" "-p:OutputPath=$bin/" "-p:AssemblyName=$assemblyName" /m:1 /nr:false -v:q *> $log
    }
    $production = $LASTEXITCODE -eq 0
    $harness = $false
    if ($production -and !$ProductionOnly) {
        try {
            & (Join-Path $PSScriptRoot 'Build.ps1') -RevitVersion $year -RunName $RunName -ReservationAssembly (Join-Path $bin 'BIMaestro.ClashActionValidation.dll') *> (Join-Path $folder 'harness-build.log')
            $harness = $LASTEXITCODE -eq 0
        } catch { Write-Output "Harness $year failed: $_" }
    }
    $results += [pscustomobject]@{version=$year;production=$production;harness=$harness;folder=$folder}
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo "output/$RunName-builds.json")
    Write-Output "$year : production=$production harness=$harness"
}
