param(
    [ValidateSet('2023','2024')][string]$RevitVersion = '2024',
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'clash-tutorial-01'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
if (Test-Path -LiteralPath (Join-Path $output 'started.txt')) { throw 'Utiliser un nouveau nom de passage pour préserver les résultats.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$msbuild = 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'
$config = if ($RevitVersion -eq '2024') { 'Release2024' } else { 'Debug' }
$pluginOutput = Join-Path $repo ('BIMaestro/bin/ClashTutorial' + $RevitVersion)
$assemblyName = 'BIMaestro.ClashTutorialValidation' + $RevitVersion
$intermediate = Join-Path $output 'plugin-obj'
& $msbuild (Join-Path $repo 'BIMaestro/BIMaestro.csproj') /t:Build "/p:Configuration=$config" /p:Platform=AnyCPU /p:RestoreIgnoreFailedSources=true "/p:OutputPath=$pluginOutput/" "/p:IntermediateOutputPath=$intermediate/" "/p:AssemblyName=$assemblyName" /m /verbosity:quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Compilation complète de BIMaestro échouée.' }
$api = 'C:/Program Files/Autodesk/Revit ' + $RevitVersion
$project = '<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"/><PropertyGroup><Configuration>Release</Configuration><Platform>AnyCPU</Platform><OutputType>Library</OutputType><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><LangVersion>9.0</LangVersion><AssemblyName>BIMaestro.TutorialValidation</AssemblyName><OutputPath>.\</OutputPath><PlatformTarget>x64</PlatformTarget></PropertyGroup><ItemGroup>'
foreach ($name in @('System','System.Core','WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) { $project += '<Reference Include="' + $name + '"/>' }
foreach ($name in @('RevitAPI','RevitAPIUI')) { $project += '<Reference Include="' + $name + '"><HintPath>' + $api + '/' + $name + '.dll</HintPath><Private>false</Private></Reference>' }
foreach ($name in @($assemblyName,'Newtonsoft.Json')) { $project += '<Reference Include="' + $name + '"><HintPath>' + $pluginOutput + '/' + $name + '.dll</HintPath><Private>true</Private></Reference>' }
$project += '</ItemGroup><ItemGroup><Compile Include="' + (Join-Path $PSScriptRoot 'TutorialValidation.cs') + '"/></ItemGroup><Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/></Project>'
[IO.File]::WriteAllText((Join-Path $output 'TutorialValidation.csproj'), $project)
& $msbuild (Join-Path $output 'TutorialValidation.csproj') /t:Build /m /verbosity:quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc de tutoriel échouée.' }
Get-ChildItem -LiteralPath $pluginOutput -File -Filter '*.dll' | Where-Object { $_.Name -notmatch '^RevitAPI' } | Copy-Item -Destination $output
Copy-Item -LiteralPath (Join-Path $pluginOutput 'Demo') -Destination $output -Recurse -Force
$escaped = [Security.SecurityElement]::Escape((Join-Path $output 'BIMaestro.TutorialValidation.dll'))
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro tutorial validation</Name><Assembly>' + $escaped + '</Assembly><AddInId>3C066066-CA0B-4544-87D1-E7246D8D7D54</AddInId><FullClassName>BIMaestro.Tests.TutorialValidation</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary tutorial validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'), $manifest)
Write-Output $output
