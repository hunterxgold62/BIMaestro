$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$out=Join-Path $repo 'tmp/properties-order-tests/unit'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$csc='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
$runtime=(Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App' -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1)
if(!$runtime){throw '.NET 10 runtime required'}
$refs=@(Get-ChildItem $runtime.FullName -Filter '*.dll' | ForEach-Object { try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); '/reference:'+$_.FullName } catch {} })
$sources=@('PropertiesPaletteLayout.cs','AppearanceOrdering.cs','PropertyPreviewRow.cs') | ForEach-Object { Join-Path $repo "BIMaestro/les couleurs/Proprietes/$_" }
$sources+=Join-Path $PSScriptRoot 'LayoutTests.cs'
$config=@{runtimeOptions=@{tfm='net10.0'; framework=@{name='Microsoft.NETCore.App';version=$runtime.Name}}} | ConvertTo-Json -Depth 5
foreach($suite in @(
 @{Name='LayoutTests';Define='BIMAESTRO_PROPERTIES_TESTS'},
 @{Name='ProductionQualificationTests';Define='PRODUCTION_QUALIFICATION_TESTS'}
)) {
 $dll=Join-Path $out ($suite.Name+'.dll')
 & $csc /nologo /nostdlib /langversion:latest /target:exe "/define:$($suite.Define)" "/out:$dll" @refs @sources
 if($LASTEXITCODE -ne 0){throw "$($suite.Name) compilation failed"}
 [IO.File]::WriteAllText((Join-Path $out ($suite.Name+'.runtimeconfig.json')),$config)
 & 'C:/Program Files/dotnet/dotnet.exe' $dll
 if($LASTEXITCODE -ne 0){throw "$($suite.Name) failed"}
}
