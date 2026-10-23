param([ValidateSet('2023','2024','2025','2026','2027')][string[]]$RevitVersion = @('2023','2025'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sdk = Join-Path $repo 'tmp/dotnet10/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
foreach ($year in $RevitVersion) {
    $api = "C:/Program Files/Autodesk/Revit $year"
    if (!(Test-Path -LiteralPath (Join-Path $api 'RevitAPI.dll'))) { throw "Missing Revit $year API" }
    $logFolder = Join-Path $repo "tmp/reservation-tests/$year"
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    $log = Join-Path $logFolder 'build.log'
    & $sdk build (Join-Path $PSScriptRoot 'Reservation.Compile.csproj') --nologo -v:minimal "-p:RevitVersion=$year" '-p:UseSharedCompilation=false' "-p:BaseIntermediateOutputPath=obj/$year/" --configfile (Join-Path $PSScriptRoot 'NuGet.Config') *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log | Select-String ' error |Erreur\(s\)|ÉCHEC|failed'
        throw "Autoréservation compile failed for Revit $year; see $log"
    }
    Write-Output "PASS Revit $year - $log"
}
