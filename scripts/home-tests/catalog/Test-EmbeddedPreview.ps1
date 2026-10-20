param([string]$FixtureJson='tmp/home-tests/catalog/embedded-benchmark.json')
$ErrorActionPreference='Stop'
$root=Resolve-Path (Join-Path $PSScriptRoot '../../..')
Push-Location $root
try {
 $compiler='C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe'
 if(!(Test-Path -LiteralPath $compiler)){throw 'Visual Studio C# compiler missing'}
 New-Item -ItemType Directory -Path tmp/home-tests/catalog -Force | Out-Null
 & $compiler /nologo /out:tmp/home-tests/catalog/PreviewTest.exe 'BIMaestro/les couleurs/Home/HomeEmbeddedPreview.cs' scripts/home-tests/catalog/PreviewTest.cs
 if($LASTEXITCODE -ne 0){throw 'Compilation failed'}
 $fixtures=Get-Content -LiteralPath $FixtureJson -Raw | ConvertFrom-Json
 & ./tmp/home-tests/catalog/PreviewTest.exe $fixtures.path | Tee-Object -FilePath tmp/home-tests/catalog/csharp-preview-tests.txt
 if($LASTEXITCODE -ne 0){throw 'Preview checks failed'}
} finally {Pop-Location}
