$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$homeSources=Join-Path $repo 'BIMaestro/les couleurs/Home'
$versions=@{ '2023'='Debug'; '2024'='Release2024'; '2025'='Revit2025/Release'; '2026'='Revit2026/Release' }
$result=@()
foreach($year in @('2023','2024','2025','2026')) {
 $dll=Join-Path $repo ('BIMaestro/bin/'+$versions[$year]+'/BIMaestro.dll')
 $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll))
 $resources=@()
 foreach($source in Get-ChildItem (Join-Path $homeSources 'Resources') -Filter '*.js') {
  $resourceName='BIMaestro.Home.'+$source.Name
  $stream=$assembly.GetManifestResourceStream($resourceName)
  if($null -eq $stream){throw "Missing $resourceName in $year"}
  try {$hasher=[Security.Cryptography.SHA256]::Create(); $embeddedHash=[Convert]::ToHexString($hasher.ComputeHash($stream))} finally {$stream.Dispose();$hasher.Dispose()}
  $sourceHash=(Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
  if($embeddedHash -ne $sourceHash){throw "Stale embedded resource $resourceName in $year"}
  $resources+=@{name=$resourceName;sha256=$sourceHash}
 }
 $result+=@{year=$year;dll=$dll;sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash;modifiedUtc=(Get-Item -LiteralPath $dll).LastWriteTimeUtc.ToString('O');resources=$resources}
}
$sources=@(Get-ChildItem -LiteralPath $homeSources -Filter '*.cs' | ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;modifiedUtc=$_.LastWriteTimeUtc.ToString('O')}})
$report=@{verifiedUtc=[DateTime]::UtcNow.ToString('O');sourceFiles=$sources;builds=$result}
$destination=Join-Path $repo 'tmp/home-tests/integration/final-resource-verification.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output "All four Home resources match current sources in all four production binaries. $destination"
