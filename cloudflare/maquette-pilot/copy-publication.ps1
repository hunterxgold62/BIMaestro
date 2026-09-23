param(
  [Parameter(Mandatory = $true)][string]$Token,
  [Parameter(Mandatory = $true)][string]$PublicationId,
  [Parameter(Mandatory = $true)][int]$Revision,
  [Parameter(Mandatory = $true)][long]$ExpectedBytes,
  [string]$Bucket = 'bimaestro-maquettes'
)

$ErrorActionPreference = 'Stop'
$endpoint = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share'
$apiKey = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo'
$wrangler = Join-Path $PSScriptRoot '..\..\viewer-web\node_modules\.bin\wrangler.cmd'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('bimaestro-r2-copy-' + [guid]::NewGuid().ToString('N'))

function Request-Share([hashtable]$body) {
  Invoke-RestMethod -Uri $endpoint -Method Post -ContentType 'application/json' -Headers @{ apikey = $apiKey } -Body ($body | ConvertTo-Json -Compress -Depth 8)
}

if (-not (Test-Path -LiteralPath $wrangler)) { throw 'Wrangler est introuvable.' }
New-Item -ItemType Directory -Path $directory | Out-Null
try {
  $share = Request-Share @{ action = 'resolve'; token = $Token }
  if ($share.publication.id -ne $PublicationId -or $share.publication.revision -ne $Revision) { throw 'Publication ou révision inattendue.' }
  $assets = @($share.manifest.assets)
  if ($assets.Count -lt 2 -or ($assets | Measure-Object -Property bytes -Sum).Sum -ne $ExpectedBytes) { throw 'Le manifeste ne correspond pas aux données attendues.' }
  $urls = @{ 'index.zip' = $share.packageUrl }
  $tileNames = @($assets | Where-Object name -ne 'index.zip' | ForEach-Object name)
  for ($offset = 0; $offset -lt $tileNames.Count; $offset += 24) {
    $batch = @($tileNames | Select-Object -Skip $offset -First 24)
    $result = Request-Share @{ action = 'tile'; token = $Token; revision = $Revision; names = $batch }
    foreach ($tile in $result.tiles) { $urls[$tile.name] = $tile.url }
  }
  $copied = 0L
  foreach ($asset in $assets) {
    if ($asset.name -notmatch '^(index\.zip|tile-\d{5}\.glb\.gz)$' -or -not $urls[$asset.name]) { throw "Fichier inattendu : $($asset.name)" }
    $localFile = Join-Path $directory $asset.name
    try {
      Invoke-WebRequest -Uri $urls[$asset.name] -OutFile $localFile
      $actualSize = (Get-Item -LiteralPath $localFile).Length
      $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $localFile).Hash.ToLowerInvariant()
      if ($actualSize -ne $asset.bytes -or $actualHash -ne $asset.sha256) { throw "Intégrité incorrecte : $($asset.name)" }
      & $wrangler r2 object put "$Bucket/$PublicationId/$Revision/$($asset.name)" --file $localFile --content-type application/octet-stream --remote | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "Envoi R2 échoué : $($asset.name)" }
      $copied += $actualSize
      Write-Output "Copie vérifiée : $($asset.name) ($actualSize octets)"
    } finally { Remove-Item -LiteralPath $localFile -ErrorAction SilentlyContinue }
  }
  Write-Output "TOTAL $($assets.Count) fichiers, $copied octets, $Bucket/$PublicationId/$Revision"
} finally { Remove-Item -LiteralPath $directory -ErrorAction SilentlyContinue }
