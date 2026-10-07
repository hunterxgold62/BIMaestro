param([ValidateSet('2023','2024')][string]$RevitVersion = '2023', [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'native-create-v1', [string]$EnginePath = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
$configuration = if ($RevitVersion -eq '2023') { 'Release' } else { 'Release2024' }
$engine = Join-Path $repo ('BIMaestro/bin/' + $configuration + '/BIMaestro.dll')
if ($EnginePath) { $engine = [IO.Path]::GetFullPath($EnginePath) }
$json = Join-Path ([IO.Path]::GetDirectoryName($engine)) 'Newtonsoft.Json.dll'
$api = 'C:/Program Files/Autodesk/Revit ' + $RevitVersion
if (!(Test-Path -LiteralPath $engine)) { throw 'Build the matching Release configuration first.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assembly = Join-Path $output 'BIMaestro.FamilyCreationRegression.dll'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:9 /target:library "/out:$assembly" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" (Join-Path $PSScriptRoot 'FamilyCreationRegressionApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native regression compilation failed.' }
Copy-Item -LiteralPath $json -Destination $output -Force
[IO.File]::WriteAllText((Join-Path $output 'engine-path.txt'), $engine)
$escaped = [Security.SecurityElement]::Escape($assembly)
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro family creation regression</Name><Assembly>' + $escaped + '</Assembly><AddInId>343B46B6-9D01-44DC-8114-ED4FBE83F0FA</AddInId><FullClassName>BIMaestro.CodexTests.FamilyCreationRegressionApp</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'), $manifest)
Write-Output $output
