$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Push-Location $repo
try {
 $msbuild='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'
 foreach($year in @('2023','2024')) {
  $configuration=if($year -eq '2023'){'Debug'}else{'Release2024'}
  & $msbuild './BIMaestro/BIMaestro.csproj' /t:Build "/p:Configuration=$configuration" /p:Platform=AnyCPU /p:RestoreIgnoreFailedSources=true /m:1 /nr:false /verbosity:quiet /fl "/flp:logfile=tmp/home-tests/integration/validated-build$year.log;verbosity=minimal"
  if($LASTEXITCODE -ne 0){throw "Production $year build failed ($LASTEXITCODE)"}
  Write-Output "Production $year build passed"
 }
 foreach($year in @('2025','2026')) {
  & './tmp/dotnet10/dotnet.exe' build './BIMaestro/BIMaestro.Revit2025.csproj' -c Release "-p:RevitVersion=$year" --no-restore -v:q '-clp:ErrorsOnly;Summary' "-flp:logfile=tmp/home-tests/integration/validated-build$year.log;verbosity=minimal" /m:1 /nr:false
  if($LASTEXITCODE -ne 0){throw "Production $year build failed ($LASTEXITCODE)"}
  Write-Output "Production $year build passed"
 }
 foreach($year in @('2023','2024','2025','2026')) { & './scripts/home-tests/integration/Build.ps1' -RevitVersion $year }
 & './scripts/home-tests/integration/VerifyResources.ps1'
 Copy-Item -LiteralPath 'tmp/home-tests/integration/final-resource-verification.json' -Destination 'tmp/home-tests/integration/validated-resource-verification.json' -Force
} finally {Pop-Location}
