param(
    [string]$MSBuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\BIMaestro.Navisworks\BIMaestro.Navisworks.csproj'
foreach ($year in @(2025, 2027)) {
    & $MSBuild $project /t:Rebuild /p:Configuration=Release "/p:NavisworksYear=$year" /nr:false /p:UseSharedCompilation=false /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Échec de compilation Navisworks $year" }
}
