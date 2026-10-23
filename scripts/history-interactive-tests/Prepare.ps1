param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'antibes-interactive-v1')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/history-interactive/' + $RunName)
if (Test-Path -LiteralPath $output) { throw 'Use a new RunName; existing interactive output is preserved.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt') -Destination (Join-Path $output 'Antibes-interactif.rvt')
(Get-FileHash -LiteralPath (Join-Path $output 'Antibes-interactif.rvt')).Hash | Set-Content -LiteralPath (Join-Path $output 'source-sha256.txt')
$buildLog = Join-Path $output 'build.log'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' (Join-Path $PSScriptRoot 'HistoryInteractive.csproj') /restore /t:Build "/p:ObserverOutput=$output/" /p:RestoreIgnoreFailedSources=true /verbosity:quiet *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 22; throw 'Interactive observer build failed.' }
$assembly = [Security.SecurityElement]::Escape((Join-Path $output 'BIMaestro.History.Interactive.dll'))
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Antibes interactive observer</Name><Assembly>' + $assembly + '</Assembly><AddInId>43D02C84-3DA7-4A69-8F06-2210B6E8F0CD</AddInId><FullClassName>BIMaestro.InteractiveTests.Observer</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Isolated-copy interactive validation observer</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.History.Interactive.addin'), $manifest)
Write-Output $output
