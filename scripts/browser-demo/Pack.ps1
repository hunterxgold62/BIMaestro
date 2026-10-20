$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repo 'tmp/browser-linkedin-demo-final'
$files = @(Get-ChildItem -LiteralPath $folder -Filter '*.rvt' | Where-Object Name -NotMatch '\.\d{4}\.rvt$')
if ($files.Count -ne 6) { throw 'Six projets attendus.' }
$path = Join-Path $folder 'BIMaestro-6-projets-arborescences.zip'
$stream = [IO.File]::Open($path,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
 foreach ($file in $files) {
  $inputStream = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
  $entryStream = $zip.CreateEntry($file.Name).Open()
  try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose(); $inputStream.Dispose() }
 }
} finally { $zip.Dispose(); $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($path)
try { if ($check.Entries.Count -ne 6) { throw 'Archive incorrecte.' }; $check.Entries | Select-Object Name,Length }
finally { $check.Dispose() }
