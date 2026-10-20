param([string]$Name='experiment2026-r5')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$out=Join-Path $repo 'tmp/properties-tests/2026-round2'
New-Item -ItemType Directory -Force $out | Out-Null
$refs=@(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.10','C:/Program Files/dotnet/shared/Microsoft.NETCore.App/10.0.10' -Filter '*.dll' | Group-Object Name | ForEach-Object { $_.Group[0] } | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /nostdlib /langversion:latest /target:library ("/out:"+(Join-Path $out ($Name+'.dll'))) '/reference:C:/Program Files/Autodesk/Revit 2026/RevitAPI.dll' '/reference:C:/Program Files/Autodesk/Revit 2026/RevitAPIUI.dll' @refs (Join-Path $PSScriptRoot 'Experiment.cs')
if($LASTEXITCODE -ne 0){throw 'Compilation failed'}
