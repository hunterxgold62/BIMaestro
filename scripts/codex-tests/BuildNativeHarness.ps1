param([ValidateSet('2023','2024')][string]$RevitVersion = '2023', [ValidatePattern('^[a-zA-Z0-9-]*$')][string]$RunName = '', [string]$FixtureFilter = '*.json', [switch]$FamilyEditOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runFolder = $RevitVersion
if ($RunName) { $runFolder += '-' + $RunName }
$output = Join-Path $repo ('tmp/codex-native-validation/' + $runFolder)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$api = 'C:/Program Files/Autodesk/Revit ' + $RevitVersion
$sources = Get-ChildItem (Join-Path $repo 'BIMaestro/commands/Codex') -Filter '*.cs' | Where-Object { $_.Name -notin @('CodexWindow.cs','CodexCommand.cs','CodexImageAttachment.cs') } | ForEach-Object FullName
$resources = @(Get-ChildItem (Join-Path $PSScriptRoot 'native-fixtures') -Filter $FixtureFilter | ForEach-Object { '/resource:' + $_.FullName + ',BIMaestro.CodexTests.' + $_.Name })
[string[]]$defines = @(if ($FamilyEditOnly) { '/define:FAMILY_EDIT_ONLY' })
$assembly = Join-Path $output 'BIMaestro.Codex.NativeValidation.dll'
$json = Join-Path $repo 'BIMaestro/bin/Release/Newtonsoft.Json.dll'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:9 /target:library "/out:$assembly" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" @defines @resources @sources (Join-Path $PSScriptRoot 'FamilyConfigurationNativeTests.cs') (Join-Path $PSScriptRoot 'FamilyEditNativeTests.cs') (Join-Path $PSScriptRoot 'NativeValidationApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc natif échouée.' }
Copy-Item -LiteralPath $json -Destination $output -Force
$escaped = [Security.SecurityElement]::Escape($assembly)
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro native validation</Name><Assembly>' + $escaped + '</Assembly><AddInId>F7D03B9A-8E73-4EDD-B299-36E5F4C937BE</AddInId><FullClassName>BIMaestro.CodexTests.NativeValidationApp</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary BIMaestro validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'), $manifest)
Write-Output $output
