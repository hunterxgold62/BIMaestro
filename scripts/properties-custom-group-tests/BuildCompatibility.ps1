param([ValidateSet('2023','2024','2025','2026','2027')][string]$RevitVersion='2025',[ValidatePattern('^[-a-zA-Z0-9_]*$')][string]$OutputTag='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/g$RevitVersion$OutputTag"
if(Test-Path -LiteralPath (Join-Path $out 'BIMaestro.PropertiesGroupCompatibilityProbe.dll')){throw 'Use a fresh OutputTag; never overwrite an assembly that may still be loaded.'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
if(!(Test-Path "$api/RevitAPI.dll")){throw "Revit $RevitVersion absent"}
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
$json=Join-Path $repo 'packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll'
$sources=@('NativePropertiesGrid.cs','PropertiesPaletteLayout.cs','AppearanceOrdering.cs','AppearancePreferences.cs','PropertyPreviewRow.cs','PropertiesAppearanceService.cs','PropertiesPaletteRenderer.cs','PropertiesAppearanceMetrics.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources+=@('NativeCustomGroupRows.cs','PropertiesNativeModuleCache.cs','PropertiesPaletteLayout.Custom.cs','PalettePresentationGate.cs') | ForEach-Object {Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_"}
# Reuse the existing disposable-fixture host without its 2025-only experiment/editor.
$probe=Get-Content (Join-Path $repo 'scripts/properties-order-tests/Probe.cs') -Raw
$probe=$probe.Replace('public class Probe : IExternalApplication','public partial class Probe : IExternalApplication').Replace('   IsolateInstalledAppearance(app);','   IsolateInstalledAppearance(app);'+[Environment]::NewLine+'   if(CustomGroupCommand(app,command))return;')
$probe=$probe.Replace('   args.SetRaiseWithoutDelay();','').Replace('   AppearanceStore.TestFilePath=','   args.SetRaiseWithoutDelay();'+[Environment]::NewLine+'   AppearanceStore.TestFilePath=')
$probe | Set-Content -LiteralPath (Join-Path $out 'Probe.cs') -Encoding UTF8
$custom=Get-Content (Join-Path $PSScriptRoot 'Probe.CustomGroups.cs') -Raw
$custom=$custom.Replace('BIMaestro.PropertiesCustomGroupTests','BIMaestro.PropertiesOrderTests').Replace('autoGroup=false;','').Replace('NativeGroupExperiment.SelectedOwner(app)','app.ActiveUIDocument.Document.GetElement(app.ActiveUIDocument.Selection.GetElementIds().FirstOrDefault() ?? ElementId.InvalidElementId)')
$source=Get-Content (Join-Path $PSScriptRoot 'Probe.cs') -Raw
$start=$source.IndexOf('  Dictionary<string,object> Schema(')
$end=$source.IndexOf('  Dictionary<string,string> Snapshot(',$start)
$custom=$custom.Insert($custom.LastIndexOf(' }'),$source.Substring($start,$end-$start))
$custom | Set-Content -LiteralPath (Join-Path $out 'Probe.CustomGroups.cs') -Encoding UTF8
$sources+=Join-Path $out 'Probe.cs'
$sources+=Join-Path $out 'Probe.CustomGroups.cs' 
$dll=Join-Path $out 'BIMaestro.PropertiesGroupCompatibilityProbe.dll'
if([int]$RevitVersion -lt 2025){
 $framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
 $refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $framework $_) }
 & $csc /nologo /langversion:latest /target:library /define:BIMAESTRO_PROPERTIES_TESTS "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" @refs @sources
}else{
 $runtime=Get-Content "$api/RevitAPI.runtimeconfig.json" -Raw | ConvertFrom-Json
 $version=$runtime.runtimeOptions.frameworks[0].version
 if(!$version){$version=$runtime.runtimeOptions.framework.version}
 $major=$version.Split('.')[0]
 $core=(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App' -Directory | Where-Object Name -Like "$major.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
 $desktop=(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App' -Directory | Where-Object Name -Like "$major.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
 if(!$core -or !$desktop){throw "Missing .NET $major desktop runtime"}
 $refs=@(Get-ChildItem $desktop,$core -Filter '*.dll' | Group-Object Name | ForEach-Object { $_.Group[0] } | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
 & $csc /nologo /nostdlib /langversion:latest /target:library /define:BIMAESTRO_PROPERTIES_TESTS "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" @refs @sources
}
if($LASTEXITCODE -ne 0){throw 'Probe compilation failed'}
Copy-Item -LiteralPath $json -Destination $out -Force
[IO.File]::WriteAllText((Join-Path $out 'version.txt'),$RevitVersion)
$xml='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro custom groups compatibility tests</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>DD42A276-3D6F-4EC3-8E21-41B0637EDD16</AddInId><FullClassName>BIMaestro.PropertiesOrderTests.Probe</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $out 'BIMaestro.PropertiesGroupCompatibilityProbe.addin'),$xml)
Write-Output $out
