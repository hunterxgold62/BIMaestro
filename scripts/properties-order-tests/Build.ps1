param([ValidateSet('2023','2024','2025','2026','2027')][string]$RevitVersion='2025',[ValidatePattern('^[-a-zA-Z0-9_]*$')][string]$OutputTag='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-order-tests/$RevitVersion$OutputTag"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
if(!(Test-Path "$api/RevitAPI.dll")){throw "Revit $RevitVersion absent"}
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
$json=Join-Path $repo 'packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll'
$sources=@('NativePropertiesGrid.cs','PropertiesPaletteLayout.cs','AppearanceOrdering.cs','AppearancePreferences.cs','PropertyPreviewRow.cs','PropertiesAppearanceService.cs','PropertiesPaletteRenderer.cs','PropertiesAppearanceMetrics.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources+=Join-Path $PSScriptRoot 'Probe.cs'
$dll=Join-Path $out 'BIMaestro.PropertiesOrderProbe.dll'
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
$xml='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro isolated order tests</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>16F3166F-AF76-444F-BBF9-CE851BD98221</AddInId><FullClassName>BIMaestro.PropertiesOrderTests.Probe</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $out 'BIMaestro.PropertiesOrderProbe.addin'),$xml)
Write-Output $out
