param([ValidateSet('2025')][string]$RevitVersion='2025',[ValidatePattern('^[-a-zA-Z0-9_]*$')][string]$OutputTag='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-custom-group-tests/$RevitVersion$OutputTag"
$existingDll=Join-Path $out 'BIMaestro.PropertiesCustomGroupProbe.dll'
if(Test-Path -LiteralPath $existingDll){throw 'This laboratory output already contains an assembly. Use a fresh OutputTag so a loaded assembly is never overwritten.'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
if(!(Test-Path "$api/RevitAPI.dll")){throw "Revit $RevitVersion absent"}
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
$json=Join-Path $repo 'packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll'
$sources=@('NativePropertiesGrid.cs','PropertiesPaletteLayout.cs','AppearanceOrdering.cs','AppearancePreferences.cs','PropertyPreviewRow.cs','PropertiesAppearanceService.cs','PropertiesPaletteRenderer.cs','PropertiesAppearanceMetrics.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources+=@('PropertiesAppearanceWindow.cs','PropertiesAppearanceWindow.Order.cs','PropertiesAppearanceWindow.Reset.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources+=Join-Path $repo 'BIMaestro/les couleurs/Proprietes/PropertiesAppearanceWindow.Groups.cs'
$sources+=@('NativeCustomGroupRows.cs','PropertiesNativeModuleCache.cs','PropertiesPaletteLayout.Custom.cs') | ForEach-Object {Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_"}
$sources+=Join-Path $PSScriptRoot 'Probe.cs' 
$sources+=Join-Path $PSScriptRoot 'Probe.Benchmark.cs'
$sources+=Join-Path $PSScriptRoot 'Probe.CustomGroups.cs'
$sources+=Join-Path $PSScriptRoot 'NativeGroupExperiment.cs'
$sources+=Join-Path $PSScriptRoot 'NativeModuleCache.cs'
$sources+=Join-Path $repo 'BIMaestro/les couleurs/Proprietes/PalettePresentationGate.cs'
$dll=Join-Path $out 'BIMaestro.PropertiesCustomGroupProbe.dll'
if([int]$RevitVersion -lt 2025){
 $framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
 $refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $framework $_) }
 & $csc /nologo /langversion:latest /target:library /define:BIMAESTRO_PROPERTIES_TESTS "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" "/reference:C:/Users/plemert/AppData/Local/BIMaestro/Bin/BIMaestro.dll" @refs @sources
}else{
 $runtime=Get-Content "$api/RevitAPI.runtimeconfig.json" -Raw | ConvertFrom-Json
 $version=$runtime.runtimeOptions.frameworks[0].version
 if(!$version){$version=$runtime.runtimeOptions.framework.version}
 $major=$version.Split('.')[0]
 $core=(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App' -Directory | Where-Object Name -Like "$major.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
 $desktop=(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App' -Directory | Where-Object Name -Like "$major.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
 if(!$core -or !$desktop){throw "Missing .NET $major desktop runtime"}
 $refs=@(Get-ChildItem $desktop,$core -Filter '*.dll' | Group-Object Name | ForEach-Object { $_.Group[0] } | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
 & $csc /nologo /nostdlib /langversion:latest /target:library /define:BIMAESTRO_PROPERTIES_TESTS "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$json" "/reference:C:/Users/plemert/AppData/Local/BIMaestro/Bin/BIMaestro.dll" @refs @sources
}
if($LASTEXITCODE -ne 0){throw 'Probe compilation failed'}
Copy-Item -LiteralPath $json -Destination $out -Force
[IO.File]::WriteAllText((Join-Path $out 'version.txt'),$RevitVersion)
$xml='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro isolated custom group laboratory</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>95BC4886-024F-44E2-A4D3-37EE3B27DC25</AddInId><FullClassName>BIMaestro.PropertiesCustomGroupTests.Probe</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $out 'BIMaestro.PropertiesCustomGroupProbe.addin'),$xml)
Write-Output $out
