param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$out=Join-Path $repo "tmp/home-tests/integration/$RevitVersion"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
$dll=Join-Path $out 'BIMaestro.HomeIntegration.dll'
$homeSources=Join-Path $repo 'BIMaestro/les couleurs/Home'
$sources=@(Get-ChildItem -LiteralPath $homeSources -Filter '*.cs' | ForEach-Object FullName)
if(!$sources.Count){throw 'Production Home sources are not present yet'}
$sources+=Join-Path $PSScriptRoot 'HomeIntegration.cs'
$resources=@(Get-ChildItem -LiteralPath $homeSources -Filter '*.js' -Recurse | ForEach-Object { '/resource:'+ $_.FullName+',BIMaestro.Home.'+$_.Name })
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
$dependencyRoot=Join-Path $repo 'BIMaestro/bin/Debug'
$names=@('Newtonsoft.Json','NPOI.Core','NPOI.OOXML','NPOI.OpenXml4Net','NPOI.OpenXmlFormats','ICSharpCode.SharpZipLib','BouncyCastle.Cryptography','Microsoft.IO.RecyclableMemoryStream','SixLabors.ImageSharp','SixLabors.Fonts','ExtendedNumerics.BigDecimal','Enums.NET','ZString')
$dependencies=@($names | ForEach-Object {Join-Path $dependencyRoot ($_.ToString()+'.dll')} | Where-Object {Test-Path -LiteralPath $_})
$dependencyRefs=@($dependencies | ForEach-Object {'/reference:'+$_})
if([int]$RevitVersion -lt 2025){
 $framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
 $refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','Facades/netstandard.dll') | ForEach-Object {'/reference:'+(Join-Path $framework $_)}
}else{
 # These locally installed Revit patches use .NET 10; this is not a stock Revit SDK target.
 $core='C:/Program Files/dotnet/shared/Microsoft.NETCore.App/10.0.10'
 $desktop='C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.10'
 $refs=@(Get-ChildItem $desktop,$core -Filter '*.dll' | Group-Object Name | ForEach-Object {$_.Group[0]} | ForEach-Object {try {[void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName}catch {}})
}
$flags=@('/nologo','/langversion:latest','/target:library','/platform:x64',"/out:$dll","/reference:$api/RevitAPI.dll","/reference:$api/RevitAPIUI.dll")
if([int]$RevitVersion -ge 2025){$flags+='/nostdlib'}
& $csc @flags @refs @dependencyRefs @resources @sources
if($LASTEXITCODE -ne 0){throw 'Home integration compilation failed'}
foreach($dependency in $dependencies){Copy-Item -LiteralPath $dependency -Destination $out -Force}
$xml='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Home integration test</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>A3B73AD8-9457-4BC0-90DB-EC8E28EB160D</AddInId><FullClassName>BIMaestro.HomeTests.HomeIntegration</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary actual Home feature integration test</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $out 'BIMaestro.HomeIntegration.addin'),$xml)
Write-Output $out

