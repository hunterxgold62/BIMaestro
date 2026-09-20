param([ValidateSet('2023','2024','2025')][string]$RevitVersion = '2024', [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'history-v1')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$api = 'C:/Program Files/Autodesk/Revit ' + $RevitVersion
$json = Join-Path $repo 'BIMaestro/bin/Debug/Newtonsoft.Json.dll'
$assembly = Join-Path $output 'BIMaestro.History.NativeValidation.dll'
$defines = @(if ($RevitVersion -eq '2024') { '/define:REVIT2024' })
$networkSource = Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs'
if ($RevitVersion -eq '2025') {
    & 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' (Join-Path $PSScriptRoot 'HistoryNative2025.csproj') /restore /t:Build "/p:HistoryOutput=$output/" /p:RestoreIgnoreFailedSources=true /verbosity:minimal
} else {
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:9 /target:library @defines "/out:$assembly" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" $networkSource (Join-Path $repo 'BIMaestro/commands/ElementIdExtensions.cs') (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs') (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs') (Join-Path $PSScriptRoot 'HistoryNativeTests.cs')
}
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc historique échouée.' }
Copy-Item -LiteralPath $json -Destination $output -Force
$escaped = [Security.SecurityElement]::Escape($assembly)
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro history validation</Name><Assembly>' + $escaped + '</Assembly><AddInId>C496D5C0-942C-4694-9128-BA8C7E88A947</AddInId><FullClassName>BIMaestro.HistoryTests.HistoryNativeTests</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary history validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'), $manifest)
Write-Output $output
