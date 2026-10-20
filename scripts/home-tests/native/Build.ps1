param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$out=Join-Path $repo "tmp/home-tests/$RevitVersion"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
$dll=Join-Path $out 'BIMaestro.HomeProbe.dll'
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
if([int]$RevitVersion -lt 2025){
 $framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
 $refs=@('mscorlib.dll','System.dll','System.Core.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $framework $_) }
 & $csc /nologo /langversion:latest /target:library "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" @refs (Join-Path $PSScriptRoot 'HomeProbe.cs')
}else{
 # Installed 2025.5/2026 binaries on this PC reference System.Runtime 10.
 $core='C:/Program Files/dotnet/shared/Microsoft.NETCore.App/10.0.10'
 $desktop='C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.10'
 $refs=@(Get-ChildItem $desktop,$core -Filter '*.dll' | Group-Object Name | ForEach-Object { $_.Group[0] } | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
 & $csc /nologo /nostdlib /langversion:latest /target:library "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" @refs (Join-Path $PSScriptRoot 'HomeProbe.cs')
}
if($LASTEXITCODE -ne 0){throw 'Home probe compilation failed'}
$xml='<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Home probe</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>35D7D906-40E6-4551-A0E4-411A824F373D</AddInId><FullClassName>BIMaestro.HomeTests.HomeProbe</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Temporary Home-only experiment</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $out 'BIMaestro.HomeProbe.addin'),$xml)
Write-Output $out
