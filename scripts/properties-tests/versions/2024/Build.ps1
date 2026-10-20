param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2025',[string]$Name='experiment-v1')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$out=Join-Path $repo "tmp/properties-tests/2024-experiment"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$api="C:/Program Files/Autodesk/Revit $RevitVersion"
$dll=Join-Path $out "$Name.dll"
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
if([int]$RevitVersion -lt 2025){
 $framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
 $refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $framework $_) }
 & $csc /nologo /langversion:latest /target:library "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" @refs (Join-Path $PSScriptRoot 'Experiment.cs')
}else{
 $core='C:/Program Files/dotnet/shared/Microsoft.NETCore.App/10.0.10'
 $desktop='C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.10'
 $refs=@(Get-ChildItem $desktop,$core -Filter '*.dll' | Group-Object Name | ForEach-Object { $_.Group[0] } | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
 & $csc /nologo /nostdlib /langversion:latest /target:library "/out:$dll" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" @refs (Join-Path $PSScriptRoot 'Experiment.cs')
}
if($LASTEXITCODE -ne 0){throw 'Experiment compilation failed'}
Write-Output $dll
