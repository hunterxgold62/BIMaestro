param(
    [ValidateSet('2024','2025')][string]$RevitVersion = '2024',
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'reference-base'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
if (Test-Path -LiteralPath (Join-Path $output 'started.txt')) { throw 'Utiliser un nouveau nom de passage pour préserver les résultats.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$msbuild = 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'
$config = if ($RevitVersion -eq '2024') { 'Release2024' } else { 'Debug' }
if ($RevitVersion -eq '2025') { $config = 'Release' }
$pluginOutput = Join-Path $repo ('BIMaestro/bin/TrainingReference' + $RevitVersion)
$assemblyName = 'BIMaestro.TrainingReferenceValidation' + $RevitVersion
$intermediate = Join-Path $output 'plugin-obj'
$productionProject = if ($RevitVersion -eq '2025') { 'BIMaestro/BIMaestro.Revit2025.csproj' } else { 'BIMaestro/BIMaestro.csproj' }
& $msbuild (Join-Path $repo $productionProject) /t:Build "/p:Configuration=$config" /p:Platform=AnyCPU /p:RestoreIgnoreFailedSources=true "/p:OutputPath=$pluginOutput/" "/p:IntermediateOutputPath=$intermediate/" "/p:AssemblyName=$assemblyName" /m /verbosity:quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Compilation complète de BIMaestro échouée.' }
$api = 'C:/Program Files/Autodesk/Revit ' + $RevitVersion
$project = '<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"/><PropertyGroup><Configuration>Release</Configuration><Platform>AnyCPU</Platform><OutputType>Library</OutputType><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><LangVersion>9.0</LangVersion><AssemblyName>BIMaestro.ReferenceValidation</AssemblyName><OutputPath>.\</OutputPath><PlatformTarget>x64</PlatformTarget></PropertyGroup><ItemGroup>'
foreach ($name in @('System','System.Core','WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) { $project += '<Reference Include="' + $name + '"/>' }
foreach ($name in @('RevitAPI','RevitAPIUI')) { $project += '<Reference Include="' + $name + '"><HintPath>' + $api + '/' + $name + '.dll</HintPath><Private>false</Private></Reference>' }
foreach ($name in @($assemblyName,'Newtonsoft.Json')) { $project += '<Reference Include="' + $name + '"><HintPath>' + $pluginOutput + '/' + $name + '.dll</HintPath><Private>true</Private></Reference>' }
$project += '</ItemGroup><ItemGroup><Compile Include="' + (Join-Path $PSScriptRoot 'ReferenceValidation.cs') + '"/></ItemGroup><Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/></Project>'
if ($RevitVersion -eq '2025') {
    $project = $project.Replace('<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">', '<Project Sdk="Microsoft.NET.Sdk">').Replace('<Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"/>', '').Replace('<Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/>', '').Replace('<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>', '<TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><EnableDefaultItems>false</EnableDefaultItems><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>')
    foreach ($name in @('System','System.Core','WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) { $project = $project.Replace('<Reference Include="' + $name + '"/>', '') }
}
[IO.File]::WriteAllText((Join-Path $output 'ReferenceValidation.csproj'), $project)
if ($RevitVersion -eq '2025') {
    [IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'), '<configuration><packageSources><clear/></packageSources></configuration>')
    & 'C:/Program Files/dotnet/dotnet.exe' build (Join-Path $output 'ReferenceValidation.csproj') --configfile (Join-Path $output 'NuGet.Config') -v:q -clp:ErrorsOnly
} else { & $msbuild (Join-Path $output 'ReferenceValidation.csproj') /t:Build /m /verbosity:quiet /clp:ErrorsOnly }
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc de tutoriel échouée.' }
Get-ChildItem -LiteralPath $pluginOutput -File -Filter '*.dll' | Where-Object { $_.Name -notmatch '^RevitAPI' } | Copy-Item -Destination $output
Copy-Item -LiteralPath (Join-Path $pluginOutput 'Demo') -Destination $output -Recurse -Force
if ($RevitVersion -ne '2024') { Copy-Item -LiteralPath (Join-Path $repo 'Demo/BIMaestro_Apprentissage_2024.rvt') -Destination $output }
$escaped = [Security.SecurityElement]::Escape((Join-Path $output 'BIMaestro.ReferenceValidation.dll'))
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro tutorial validation</Name><Assembly>' + $escaped + '</Assembly><AddInId>3C066066-CA0B-4544-87D1-E7246D8D7D54</AddInId><FullClassName>BIMaestro.Tests.ReferenceValidation</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary tutorial validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'), $manifest)
Write-Output $output
