param([ValidateSet('2023','2024','2025')][string]$RevitVersion='2024', [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName='clash-v1', [string]$ReservationAssembly='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/codex-native-validation/' + $RevitVersion + '-' + $RunName)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$module=Join-Path $repo 'BIMaestro/commands/analyse erreur maquette 3D'
$api='C:/Program Files/Autodesk/Revit ' + $RevitVersion
$assemblyName='BIMaestro.ClashValidation'
$xaml=(Get-Content -LiteralPath (Join-Path $module 'SmartCheckWindow.xaml') -Raw).Replace('/BIMaestro;component/', '/' + $assemblyName + ';component/')
[IO.File]::WriteAllText((Join-Path $output 'SmartCheckWindow.xaml'),$xaml)
# Only the user-document storage root differs in this copy; persistence logic remains production code.
$testDocumentsLiteral='"' + $output.Replace('\','/').Replace('"','\"') + '/test-documents"'
$state=(Get-Content -LiteralPath (Join-Path $module 'SmartCheckState.cs') -Raw).Replace('Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)', $testDocumentsLiteral)
[IO.File]::WriteAllText((Join-Path $output 'SmartCheckState.cs'),$state)
$project='<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"/><PropertyGroup><Configuration>Release</Configuration><Platform>AnyCPU</Platform><OutputType>Library</OutputType><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><LangVersion>9.0</LangVersion><AssemblyName>' + $assemblyName + '</AssemblyName><OutputPath>.\</OutputPath><PlatformTarget>x64</PlatformTarget><AlwaysCompileMarkupFilesInSeparateDomain>false</AlwaysCompileMarkupFilesInSeparateDomain></PropertyGroup><ItemGroup>'
foreach ($name in @('System','System.Core','WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) { $project += '<Reference Include="' + $name + '"/>' }
foreach ($name in @('RevitAPI','RevitAPIUI')) { $project += '<Reference Include="' + $name + '"><HintPath>' + $api + '/' + $name + '.dll</HintPath><Private>false</Private></Reference>' }
$json=Join-Path $repo 'BIMaestro/bin/Release/Newtonsoft.Json.dll'
if (!$ReservationAssembly) { $ReservationAssembly=Join-Path $repo ('BIMaestro/bin/ClashActionValidation' + $RevitVersion + '/BIMaestro.ClashActionValidation.dll') }
$reservationDll=[IO.Path]::GetFullPath($ReservationAssembly)
if (!(Test-Path -LiteralPath $reservationDll)) { throw 'Compile the Autoréservation validation assembly first, or specify -ReservationAssembly. See the Clash 3D test README.' }
$project += '<Reference Include="' + [IO.Path]::GetFileNameWithoutExtension($reservationDll) + '"><HintPath>' + [Security.SecurityElement]::Escape($reservationDll) + '</HintPath><Private>true</Private></Reference>'
$project += '<Reference Include="Newtonsoft.Json"><HintPath>' + $json + '</HintPath></Reference></ItemGroup><ItemGroup>'
foreach ($name in @('SmartClashEngine.cs','SmartClashVisual.cs','SmartVisualCapture.cs','SmartCheck.Types.cs','SmartExternalHandler.cs','SmartClashReport.cs','SmartIssueInspector.cs','SmartCheckWindow.xaml.cs','SmartCheckWindow.Tutorial.cs')) { $project += '<Compile Include="' + (Join-Path $module $name) + '"/>' }
$project += '<Compile Include="SmartCheckState.cs"/>'
$project += '<Compile Include="' + (Join-Path $repo 'BIMaestro/commands/Tutoriels/DemoClashExercise.cs') + '"/>'
foreach ($name in @('NativeStubs.cs','NativeValidation.cs','ReservationValidation.cs')) { $project += '<Compile Include="' + (Join-Path $PSScriptRoot $name) + '"/>' }
$project += '<Compile Include="' + (Join-Path $repo 'BIMaestro/commands/ElementIdExtensions.cs') + '"/><Page Include="SmartCheckWindow.xaml"/><Page Include="' + (Join-Path $repo 'BIMaestro/Themes/BIMaestroTheme.xaml') + '"><Link>Themes/BIMaestroTheme.xaml</Link></Page></ItemGroup><Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/></Project>'
if ($RevitVersion -eq '2025') {
    $project=$project.Replace('<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">','<Project Sdk="Microsoft.NET.Sdk">').Replace('<Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"/>','').Replace('<Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/>','').Replace('<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>','<TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><EnableDefaultItems>false</EnableDefaultItems><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath><DefineConstants>REVIT2025_OR_GREATER</DefineConstants>')
    foreach ($name in @('System','System.Core','WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) { $project=$project.Replace('<Reference Include="' + $name + '"/>','') }
}
[IO.File]::WriteAllText((Join-Path $output 'ClashValidation.csproj'),$project)
if ($RevitVersion -eq '2025') {
    [IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear/></packageSources></configuration>')
    & 'C:/Program Files/dotnet/dotnet.exe' build (Join-Path $output 'ClashValidation.csproj') --configfile (Join-Path $output 'NuGet.Config') -v:q -clp:ErrorsOnly
} else {
    & 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' (Join-Path $output 'ClashValidation.csproj') /t:Build /m /verbosity:quiet /nologo /clp:ErrorsOnly
}
if ($LASTEXITCODE -ne 0) { throw 'Compilation du banc Clash 3D échouée.' }
$escaped=[Security.SecurityElement]::Escape((Join-Path $output ($assemblyName + '.dll')))
$manifest='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Clash validation</Name><Assembly>' + $escaped + '</Assembly><AddInId>F7D03B9A-8E73-4EDD-B299-36E5F4C937BE</AddInId><FullClassName>Analyse.Tests.NativeValidation</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary clash validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.NativeValidation.addin'),$manifest)
Write-Output $output
