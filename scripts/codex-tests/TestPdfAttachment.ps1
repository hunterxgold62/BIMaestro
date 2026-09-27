param(
    [Parameter(Mandatory = $true)][string]$PdfPath,
    [ValidateSet('Release', 'Release2024', 'Debug')][string]$Configuration = 'Release2024',
    [string]$AssemblyDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceBin = if ($AssemblyDirectory) { [IO.Path]::GetFullPath($AssemblyDirectory) }
             else { Join-Path $repoRoot ('BIMaestro/bin/' + $Configuration) }
$testBin = Join-Path $repoRoot ('tmp/codex-tests/pdf-' + [Guid]::NewGuid().ToString('N'))
$compiler = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$pdfSource = Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexPdfAttachment.cs'
$resolverSource = Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexPdfAssemblyResolver.cs'
$probeSource = Join-Path $PSScriptRoot 'PdfAttachmentProbe.cs'
$testExe = Join-Path $testBin 'PdfAttachmentProbe.exe'
$pdfFullPath = [IO.Path]::GetFullPath($PdfPath)

if (!(Test-Path -LiteralPath $pdfFullPath -PathType Leaf)) { throw "PDF introuvable : $pdfFullPath" }
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Compilateur C# introuvable.' }
if (!(Test-Path -LiteralPath (Join-Path $sourceBin 'UglyToad.PdfPig.dll') -PathType Leaf)) {
    throw "Assemblages PDF absents dans $sourceBin. Compiler BIMaestro avant ce test."
}

New-Item -ItemType Directory -Path $testBin -Force | Out-Null
$dependencyNames = @(
    'UglyToad.PdfPig*.dll', 'SkiaSharp*.dll', 'HarfBuzzSharp.dll',
    'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'Microsoft.Bcl*.dll',
    'System.*.dll'
)
foreach ($name in $dependencyNames) {
    Get-ChildItem -Path (Join-Path $sourceBin $name) -File -ErrorAction SilentlyContinue |
        Copy-Item -Destination $testBin -Force
}
$references = @('UglyToad.PdfPig.dll', 'UglyToad.PdfPig.DocumentLayoutAnalysis.dll',
    'UglyToad.PdfPig.Rendering.Skia.dll', 'SkiaSharp.dll', 'System.Memory.dll') |
    ForEach-Object { '/reference:' + (Join-Path $testBin $_) }
& $compiler /nologo /langversion:9 /target:exe /platform:x64 "/out:$testExe" @references $pdfSource $resolverSource $probeSource
if ($LASTEXITCODE -ne 0) { throw 'Compilation du test PDF échouée.' }

# Use the same raw .NET Framework binding behavior as Revit; the PDF reader
# supplies narrowly scoped resolution for its NuGet dependencies.
& $testExe $pdfFullPath
if ($LASTEXITCODE -ne 0) { throw 'Extraction ou rendu du PDF échoué.' }
